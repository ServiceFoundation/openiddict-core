﻿/*
 * Licensed under the Apache License, Version 2.0 (http://www.apache.org/licenses/LICENSE-2.0)
 * See https://github.com/openiddict/openiddict-core for more information concerning
 * the license and the contributors participating to this project.
 */

using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using AspNet.Security.OpenIdConnect.Extensions;
using AspNet.Security.OpenIdConnect.Primitives;
using AspNet.Security.OpenIdConnect.Server;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using OpenIddict.Abstractions;

namespace OpenIddict.Server
{
    /// <summary>
    /// Provides the logic necessary to extract, validate and handle OpenID Connect requests.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public partial class OpenIddictServerProvider : OpenIdConnectServerProvider
    {
        public readonly ILogger _logger;
        public readonly IOpenIddictApplicationManager _applicationManager;
        public readonly IOpenIddictAuthorizationManager _authorizationManager;
        public readonly IOpenIddictScopeManager _scopeManager;
        public readonly IOpenIddictTokenManager _tokenManager;

        public OpenIddictServerProvider(
            [NotNull] ILogger<OpenIddictServerProvider> logger,
            [NotNull] IOpenIddictApplicationManager applicationManager,
            [NotNull] IOpenIddictAuthorizationManager authorizationManager,
            [NotNull] IOpenIddictScopeManager scopeManager,
            [NotNull] IOpenIddictTokenManager tokenManager)
        {
            _logger = logger;
            _applicationManager = applicationManager;
            _authorizationManager = authorizationManager;
            _scopeManager = scopeManager;
            _tokenManager = tokenManager;
        }

        public override Task ProcessChallengeResponse([NotNull] ProcessChallengeResponseContext context)
        {
            Debug.Assert(context.Request.IsAuthorizationRequest() ||
                         context.Request.IsTokenRequest(),
                "The request should be an authorization or token request.");

            // Add the custom properties that are marked as public
            // as authorization or token response properties.
            var parameters = GetParameters(context.Request, context.Properties);
            foreach (var (property, parameter, value) in parameters)
            {
                context.Response.AddParameter(parameter, value);
            }

            return base.ProcessChallengeResponse(context);
        }

        public override async Task ProcessSigninResponse([NotNull] ProcessSigninResponseContext context)
        {
            var options = (OpenIddictServerOptions) context.Options;

            Debug.Assert(context.Request.IsAuthorizationRequest() ||
                         context.Request.IsTokenRequest(),
                "The request should be an authorization or token request.");

            if (context.Request.IsTokenRequest() && (context.Request.IsAuthorizationCodeGrantType() ||
                                                     context.Request.IsRefreshTokenGrantType()))
            {
                // Note: when handling a grant_type=authorization_code or refresh_token request,
                // the OpenID Connect server middleware allows creating authentication tickets
                // that are completely disconnected from the original code or refresh token ticket.
                // This scenario is deliberately not supported in OpenIddict and all the tickets
                // must be linked. To ensure the properties are flowed from the authorization code
                // or the refresh token to the new ticket, they are manually restored if necessary.
                if (!context.Ticket.Properties.HasProperty(OpenIdConnectConstants.Properties.TokenId))
                {
                    // Retrieve the original authentication ticket from the request properties.
                    var ticket = context.Request.GetProperty<AuthenticationTicket>(
                        OpenIddictConstants.Properties.AuthenticationTicket);
                    Debug.Assert(ticket != null, "The authentication ticket shouldn't be null.");

                    foreach (var property in ticket.Properties.Items)
                    {
                        // Don't override the properties that have been
                        // manually set on the new authentication ticket.
                        if (context.Ticket.HasProperty(property.Key))
                        {
                            continue;
                        }

                        context.Ticket.AddProperty(property.Key, property.Value);
                    }

                    // Always include the "openid" scope when the developer doesn't explicitly call SetScopes.
                    // Note: the application is allowed to specify a different "scopes": in this case,
                    // don't replace the "scopes" property stored in the authentication ticket.
                    if (context.Request.HasScope(OpenIdConnectConstants.Scopes.OpenId) && !context.Ticket.HasScope())
                    {
                        context.Ticket.SetScopes(OpenIdConnectConstants.Scopes.OpenId);
                    }

                    context.IncludeIdentityToken = context.Ticket.HasScope(OpenIdConnectConstants.Scopes.OpenId);
                }

                context.IncludeRefreshToken = context.Ticket.HasScope(OpenIdConnectConstants.Scopes.OfflineAccess);

                // Always include a refresh token for grant_type=refresh_token requests if
                // rolling tokens are enabled and if the offline_access scope was specified.
                if (context.Request.IsRefreshTokenGrantType())
                {
                    context.IncludeRefreshToken &= options.UseRollingTokens;
                }

                // If token revocation was explicitly disabled,
                // none of the following security routines apply.
                if (options.DisableTokenRevocation)
                {
                    await base.ProcessSigninResponse(context);

                    return;
                }

                var token = context.Request.GetProperty($"{OpenIddictConstants.Properties.Token}:{context.Ticket.GetTokenId()}");
                Debug.Assert(token != null, "The token shouldn't be null.");

                // If rolling tokens are enabled or if the request is a grant_type=authorization_code request,
                // mark the authorization code or the refresh token as redeemed to prevent future reuses.
                // If the operation fails, return an error indicating the code/token is no longer valid.
                // See https://tools.ietf.org/html/rfc6749#section-6 for more information.
                if (options.UseRollingTokens || context.Request.IsAuthorizationCodeGrantType())
                {
                    if (!await TryRedeemTokenAsync(token))
                    {
                        context.Reject(
                            error: OpenIdConnectConstants.Errors.InvalidGrant,
                            description: context.Request.IsAuthorizationCodeGrantType() ?
                                "The specified authorization code is no longer valid." :
                                "The specified refresh token is no longer valid.");

                        return;
                    }
                }

                if (context.Request.IsRefreshTokenGrantType())
                {
                    // When rolling tokens are enabled, try to revoke all the previously issued tokens
                    // associated with the authorization if the request is a refresh_token request.
                    // If the operation fails, silently ignore the error and keep processing the request:
                    // this may indicate that one of the revoked tokens was modified by a concurrent request.
                    if (options.UseRollingTokens)
                    {
                        await TryRevokeTokensAsync(context.Ticket);
                    }

                    // When rolling tokens are disabled, try to extend the expiration date
                    // of the existing token instead of returning a new refresh token
                    // with a new expiration date if sliding expiration was not disabled.
                    // If the operation fails, silently ignore the error and keep processing
                    // the request: this may indicate that a concurrent refresh token request
                    // already updated the expiration date associated with the refresh token.
                    if (!options.UseRollingTokens && options.UseSlidingExpiration)
                    {
                        await TryExtendTokenAsync(token, context.Ticket, options);
                    }
                }
            }

            // If no authorization was explicitly attached to the authentication ticket,
            // create an ad hoc authorization if an authorization code or a refresh token
            // is going to be returned to the client application as part of the response.
            if (!context.Ticket.HasProperty(OpenIddictConstants.Properties.AuthorizationId) &&
                (context.IncludeAuthorizationCode || context.IncludeRefreshToken))
            {
                await CreateAuthorizationAsync(context.Ticket, options, context.Request);
            }

            // Add the custom properties that are marked as public as authorization or
            // token response properties and remove them from the authentication ticket
            // so they are not persisted in the authorization code/access/refresh token.
            // Note: make sure the foreach statement iterates on a copy of the ticket
            // as the property collection is modified when the property is removed.
            var parameters = GetParameters(context.Request, context.Ticket.Properties);
            foreach (var (property, parameter, value) in parameters.ToArray())
            {
                context.Response.AddParameter(parameter, value);
                context.Ticket.RemoveProperty(property);
            }

            await base.ProcessSigninResponse(context);
        }

        public override Task ProcessSignoutResponse([NotNull] ProcessSignoutResponseContext context)
        {
            Debug.Assert(context.Request.IsLogoutRequest(), "The request should be a logout request.");

            // Add the custom properties that are marked as public as logout response properties.
            var parameters = GetParameters(context.Request, context.Properties);
            foreach (var (property, parameter, value) in parameters)
            {
                context.Response.AddParameter(parameter, value);
            }

            return base.ProcessSignoutResponse(context);
        }

        public void Import([NotNull] OpenIdConnectServerProvider provider)
        {
            OnMatchEndpoint = provider.MatchEndpoint;

            OnExtractAuthorizationRequest = provider.ExtractAuthorizationRequest;
            OnExtractConfigurationRequest = provider.ExtractConfigurationRequest;
            OnExtractCryptographyRequest = provider.ExtractCryptographyRequest;
            OnExtractIntrospectionRequest = provider.ExtractIntrospectionRequest;
            OnExtractLogoutRequest = provider.ExtractLogoutRequest;
            OnExtractRevocationRequest = provider.ExtractRevocationRequest;
            OnExtractTokenRequest = provider.ExtractTokenRequest;
            OnExtractUserinfoRequest = provider.ExtractUserinfoRequest;
            OnValidateAuthorizationRequest = provider.ValidateAuthorizationRequest;
            OnValidateConfigurationRequest = provider.ValidateConfigurationRequest;
            OnValidateCryptographyRequest = provider.ValidateCryptographyRequest;
            OnValidateIntrospectionRequest = provider.ValidateIntrospectionRequest;
            OnValidateLogoutRequest = provider.ValidateLogoutRequest;
            OnValidateRevocationRequest = provider.ValidateRevocationRequest;
            OnValidateTokenRequest = provider.ValidateTokenRequest;
            OnValidateUserinfoRequest = provider.ValidateUserinfoRequest;

            OnHandleAuthorizationRequest = provider.HandleAuthorizationRequest;
            OnHandleConfigurationRequest = provider.HandleConfigurationRequest;
            OnHandleCryptographyRequest = provider.HandleCryptographyRequest;
            OnHandleIntrospectionRequest = provider.HandleIntrospectionRequest;
            OnHandleLogoutRequest = provider.HandleLogoutRequest;
            OnHandleRevocationRequest = provider.HandleRevocationRequest;
            OnHandleTokenRequest = provider.HandleTokenRequest;
            OnHandleUserinfoRequest = provider.HandleUserinfoRequest;

            OnApplyAuthorizationResponse = provider.ApplyAuthorizationResponse;
            OnApplyConfigurationResponse = provider.ApplyConfigurationResponse;
            OnApplyCryptographyResponse = provider.ApplyCryptographyResponse;
            OnApplyIntrospectionResponse = provider.ApplyIntrospectionResponse;
            OnApplyLogoutResponse = provider.ApplyLogoutResponse;
            OnApplyRevocationResponse = provider.ApplyRevocationResponse;
            OnApplyTokenResponse = provider.ApplyTokenResponse;
            OnApplyUserinfoResponse = provider.ApplyUserinfoResponse;

            OnProcessChallengeResponse = provider.ProcessChallengeResponse;
            OnProcessSigninResponse = provider.ProcessSigninResponse;
            OnProcessSignoutResponse = provider.ProcessSignoutResponse;

            OnDeserializeAccessToken = provider.DeserializeAccessToken;
            OnDeserializeAuthorizationCode = provider.DeserializeAuthorizationCode;
            OnDeserializeIdentityToken = provider.DeserializeIdentityToken;
            OnDeserializeRefreshToken = provider.DeserializeRefreshToken;

            OnSerializeAccessToken = provider.SerializeAccessToken;
            OnSerializeAuthorizationCode = provider.SerializeAuthorizationCode;
            OnSerializeIdentityToken = provider.SerializeIdentityToken;
            OnSerializeRefreshToken = provider.SerializeRefreshToken;
        }
    }
}