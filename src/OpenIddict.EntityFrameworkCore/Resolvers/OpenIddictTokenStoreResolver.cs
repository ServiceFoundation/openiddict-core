﻿/*
 * Licensed under the Apache License, Version 2.0 (http://www.apache.org/licenses/LICENSE-2.0)
 * See https://github.com/openiddict/openiddict-core for more information concerning
 * the license and the contributors participating to this project.
 */

using System;
using System.Collections.Concurrent;
using System.Text;
using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using OpenIddict.Core;
using OpenIddict.EntityFrameworkCore.Models;

namespace OpenIddict.EntityFrameworkCore
{
    /// <summary>
    /// Exposes a method allowing to resolve a token store.
    /// </summary>
    public class OpenIddictTokenStoreResolver : IOpenIddictTokenStoreResolver
    {
        private static readonly ConcurrentDictionary<Type, Type> _cache = new ConcurrentDictionary<Type, Type>();
        private readonly IOptionsMonitor<OpenIddictEntityFrameworkCoreOptions> _options;
        private readonly IServiceProvider _provider;

        public OpenIddictTokenStoreResolver(
            [NotNull] IOptionsMonitor<OpenIddictEntityFrameworkCoreOptions> options,
            [NotNull] IServiceProvider provider)
        {
            _options = options;
            _provider = provider;
        }

        /// <summary>
        /// Returns a token store compatible with the specified token type or throws an
        /// <see cref="InvalidOperationException"/> if no store can be built using the specified type.
        /// </summary>
        /// <typeparam name="TToken">The type of the Token entity.</typeparam>
        /// <returns>An <see cref="IOpenIddictTokenStore{TToken}"/>.</returns>
        public IOpenIddictTokenStore<TToken> Get<TToken>() where TToken : class
        {
            var store = _provider.GetService<IOpenIddictTokenStore<TToken>>();
            if (store != null)
            {
                return store;
            }

            var type = _cache.GetOrAdd(typeof(TToken), key =>
            {
                var root = OpenIddictCoreHelpers.FindGenericBaseType(key, typeof(OpenIddictToken<,,>));
                if (root == null)
                {
                    throw new InvalidOperationException(new StringBuilder()
                        .AppendLine("The specified token type is not compatible with the Entity Framework Core stores.")
                        .Append("When enabling the Entity Framework Core stores, make sure you use the built-in ")
                        .Append("'OpenIddictToken' entity (from the 'OpenIddict.EntityFrameworkCore.Models' package) ")
                        .Append("or a custom entity that inherits from the generic 'OpenIddictToken' entity.")
                        .ToString());
                }

                var context = _options.CurrentValue.ContextType;
                if (context == null)
                {
                    throw new InvalidOperationException(new StringBuilder()
                        .AppendLine("No Entity Framework Core context was specified in the OpenIddict options.")
                        .Append("To configure the OpenIddict Entity Framework Core stores to use a specific 'DbContext', ")
                        .Append("use 'options.AddEntityFrameworkCoreStores().UseContext<TContext>()'.")
                        .ToString());
                }

                return typeof(OpenIddictTokenStore<,,,,>).MakeGenericType(
                    /* TToken: */ key,
                    /* TApplication: */ root.GenericTypeArguments[1],
                    /* TAuthorization: */ root.GenericTypeArguments[2],
                    /* TContext: */ context,
                    /* TKey: */ root.GenericTypeArguments[0]);
            });

            return (IOpenIddictTokenStore<TToken>) _provider.GetRequiredService(type);
        }
    }
}
