using Microsoft.AspNetCore.Components.Authorization;

namespace SyntaxCircus.Blazor.Auth;

/// <summary>
/// Scoped <see cref="IUserAccessTokenProvider"/>. Mirrors the user-token half of
/// <see cref="ApiAuthHandler"/> (HttpContext present: <see cref="ServerRequestOidcTokenResolver"/>;
/// absent: authentication state, then <see cref="CachedUserTokenResolver"/>) and deliberately has no
/// client-credentials fallback.
/// </summary>
internal sealed class UserAccessTokenProvider(
    IHttpContextAccessor httpContextAccessor,
    ServerRequestOidcTokenResolver requestResolver,
    AuthenticationStateProvider authenticationStateProvider,
    IUserTokenCacheKeyProvider cacheKeyProvider,
    CachedUserTokenResolver circuitResolver,
    OidcTokenRefreshService refreshService,
    SessionExpiryBroker sessionExpiryBroker,
    ILogger<UserAccessTokenProvider> logger) : IUserAccessTokenProvider
{
    public async ValueTask<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        var httpContext = httpContextAccessor.HttpContext;
        if (httpContext is not null)
        {
            return await ResolveForRequestAsync(httpContext, cancellationToken).ConfigureAwait(false);
        }

        AuthenticationState authState;
        try
        {
            authState = await authenticationStateProvider.GetAuthenticationStateAsync().ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            // Thrown when resolved outside a circuit scope: there is no user to act as.
            logger.LogDebug(ex, "Failed to resolve authentication state; no user access token is available.");
            return null;
        }

        if (authState.User.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        var cacheKey = cacheKeyProvider.GetCacheKey(authState.User);
        if (string.IsNullOrWhiteSpace(cacheKey))
        {
            return null;
        }

        return await circuitResolver.ResolveAsync(cacheKey, refreshService, sessionExpiryBroker.Publish, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> ResolveForRequestAsync(HttpContext httpContext, CancellationToken cancellationToken)
    {
        if (httpContext.User.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        var resolution = await requestResolver.ResolveAsync(httpContext, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(resolution.Token) && resolution.IsExpired && !string.IsNullOrWhiteSpace(resolution.CacheKey))
        {
            sessionExpiryBroker.Publish(resolution.CacheKey);
        }

        return string.IsNullOrWhiteSpace(resolution.Token) ? null : resolution.Token;
    }
}
