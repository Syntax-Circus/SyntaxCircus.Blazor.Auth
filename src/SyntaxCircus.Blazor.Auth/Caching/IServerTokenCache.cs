namespace SyntaxCircus.Blazor.Auth;

/// <summary>
/// Server-side cache of OIDC access/refresh tokens keyed by a cache key (see
/// <see cref="IUserTokenCacheKeyProvider"/>). Populated during the initial HTTP request (where
/// HttpContext is available) so Blazor Server's interactive SignalR phase — where HttpContext is
/// null — can still forward bearer tokens to downstream APIs.
/// </summary>
public interface IServerTokenCache
{
    Task<ServerTokenCacheEntry?> GetAsync(string cacheKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the stored refresh token for the given cache key even if the access token itself
    /// has already expired, so the Blazor circuit path can silently renew without an HttpContext.
    /// </summary>
    Task<string?> GetRefreshTokenAsync(string cacheKey, CancellationToken cancellationToken = default);

    Task SetAsync(string cacheKey, ServerTokenCacheEntry entry, CancellationToken cancellationToken = default);

    Task RemoveAsync(string cacheKey, CancellationToken cancellationToken = default);

    Task<T> WithRefreshLockAsync<T>(
        string cacheKey,
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken = default);
}
