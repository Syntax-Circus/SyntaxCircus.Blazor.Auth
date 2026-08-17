using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;

namespace SyntaxCircus.Blazor.Auth;

public static class BlazorTokenForwardingExtensions
{
    /// <summary>
    /// Registers Blazor Server OIDC token forwarding: the server-side token cache (Redis-backed
    /// when <c>Authentication:Oidc:TokenCache:Redis:Enabled</c> is set, in-process otherwise),
    /// the refresh/resolver services, the client-credentials (M2M) fallback provider, and
    /// <see cref="ApiAuthHandler"/> for use as a typed-client <c>DelegatingHandler</c>.
    /// </summary>
    public static IServiceCollection AddBlazorTokenForwarding(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddHttpContextAccessor();
        services.AddScoped<SessionStateService>();
        services.AddSingleton<IUserTokenCacheKeyProvider, UserTokenCacheKeyProvider>();
        services.AddScoped<ServerRequestOidcTokenResolver>();
        services.AddTransient<ApiAuthHandler>();
        services.AddHttpClient<OidcTokenRefreshService>();

        services.Configure<AuthOptions>(configuration.GetSection(AuthOptions.SectionName));
        services.Configure<ApiOptions>(configuration.GetSection(ApiOptions.SectionName));
        services.Configure<ApiClientCredentialsOptions>(configuration.GetSection(ApiClientCredentialsOptions.SectionName));

        services.AddHttpClient(ApiClientCredentialsTokenProvider.HttpClientName);
        services.AddSingleton<IApiClientCredentialsTokenProvider, ApiClientCredentialsTokenProvider>();

        var redisOptions = configuration.GetSection(AuthOptions.SectionName).Get<AuthOptions>()?.TokenCache.Redis;
        if (redisOptions is { Enabled: true } && !string.IsNullOrWhiteSpace(redisOptions.ConnectionString))
        {
            services.AddStackExchangeRedisCache(cacheOptions =>
            {
                cacheOptions.Configuration = redisOptions.ConnectionString;
                cacheOptions.InstanceName = redisOptions.InstanceName;
            });

            var protectionProvider = redisOptions.Protection.Enabled
                ? BuildRedisProtectionProvider(redisOptions)
                : null;

            services.AddSingleton<IServerTokenCache>(sp => new RedisServerTokenCache(
                sp.GetRequiredService<IDistributedCache>(),
                sp.GetRequiredService<IOptions<AuthOptions>>(),
                protectionProvider));
        }
        else
        {
            services.AddSingleton<IServerTokenCache, ServerTokenCache>();
            services.TryAddSingleton<IDistributedCache, MemoryDistributedCache>();
            services.TryAddSingleton<IOptions<MemoryDistributedCacheOptions>>(
                Options.Create(new MemoryDistributedCacheOptions()));
        }

        return services;
    }

    /// <summary>
    /// Builds a standalone <see cref="IDataProtectionProvider"/> whose key ring is persisted to the
    /// same Redis instance as the token cache, isolated from the host application's own
    /// <c>AddDataProtection()</c> setup (so it never affects cookies, antiforgery, or anything else
    /// the host app protects). The Redis connection is opened lazily, on first key-ring access —
    /// not eagerly here — to match the lazy-connect behavior of <see cref="StackExchangeRedisCacheServiceCollectionExtensions.AddStackExchangeRedisCache"/>.
    /// </summary>
    private static IDataProtectionProvider BuildRedisProtectionProvider(AuthOptions.RedisTokenCacheOptions redisOptions)
    {
        var lazyMultiplexer = new Lazy<IConnectionMultiplexer>(() => ConnectionMultiplexer.Connect(redisOptions.ConnectionString));

        var protectionServices = new ServiceCollection();
        protectionServices
            .AddDataProtection()
            .SetApplicationName("SyntaxCircus.Blazor.Auth.RedisServerTokenCache")
            .PersistKeysToStackExchangeRedis(
                () => lazyMultiplexer.Value.GetDatabase(),
                $"{redisOptions.InstanceName}DataProtection-Keys");

        return protectionServices.BuildServiceProvider().GetRequiredService<IDataProtectionProvider>();
    }

    /// <summary>
    /// Adds middleware that eagerly resolves (and refreshes) the current request's OIDC token
    /// into <see cref="IServerTokenCache"/> while headers are still writable. Call after
    /// <c>UseAuthorization()</c> and before antiforgery.
    /// </summary>
    public static IApplicationBuilder UseBlazorTokenCache(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<BlazorTokenCacheMiddleware>();
    }
}
