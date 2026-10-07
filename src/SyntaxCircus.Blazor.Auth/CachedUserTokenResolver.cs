using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;

namespace SyntaxCircus.Blazor.Auth;

/// <summary>
/// The single cache-then-refresh implementation for the Blazor circuit phase, where no
/// <see cref="HttpContext"/> (and so no auth cookie) is available. Shared by
/// <see cref="ApiAuthHandler"/> and the user access-token provider so the two can never
/// drift apart. It reads <see cref="IServerTokenCache"/>, refreshes under the per-key lock when
/// the cached access token is missing, expired or inside the configured refresh-skew window, and
/// writes the refreshed tokens back.
/// </summary>
/// <remarks>
/// Registered as a singleton, so it only takes singleton-safe dependencies. The refresh service
/// (a typed <see cref="HttpClient"/>, transient) and the expiry publisher (which differs between
/// the production and compatibility handler constructors) are passed per call instead.
/// </remarks>
internal sealed class CachedUserTokenResolver(
    IServerTokenCache tokenCache,
    IOptions<AuthOptions> options,
    TimeProvider? timeProvider = null,
    ILogger<CachedUserTokenResolver>? logger = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly ILogger logger = (ILogger?)logger ?? NullLogger.Instance;

    /// <summary>
    /// Returns a usable access token for <paramref name="cacheKey"/>, refreshing it if needed, or
    /// <see langword="null"/> when there is none. A failed refresh publishes expiry through
    /// <paramref name="onExpired"/> only when no still-valid token remains to hand back.
    /// </summary>
    public async Task<string?> ResolveAsync(
        string cacheKey,
        OidcTokenRefreshService refreshService,
        Action<string> onExpired,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheKey);
        ArgumentNullException.ThrowIfNull(refreshService);
        ArgumentNullException.ThrowIfNull(onExpired);

        var refreshSkew = TimeSpan.FromSeconds(Math.Max(0, options.Value.TokenCache.RefreshSkewSeconds));

        var entry = await tokenCache.GetAsync(cacheKey, cancellationToken).ConfigureAwait(false);
        if (entry is not null && entry.IsUsable(clock.GetUtcNow(), refreshSkew))
        {
            return entry.AccessToken;
        }

        // Fast exit that avoids taking the lock when nothing could be refreshed anyway. The refresh
        // token actually used is re-read inside the lock below.
        var observedRefreshToken = await tokenCache.GetRefreshTokenAsync(cacheKey, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(observedRefreshToken))
        {
            return StillValidToken(entry, clock.GetUtcNow());
        }

        return await tokenCache.WithRefreshLockAsync(
            cacheKey,
            async lockCt =>
            {
                var cached = await tokenCache.GetAsync(cacheKey, lockCt).ConfigureAwait(false);
                if (cached is not null && cached.IsUsable(clock.GetUtcNow(), refreshSkew))
                {
                    return cached.AccessToken;
                }

                // A caller that held the lock before us may have rotated the refresh token (and
                // IdPs that rotate reject the old one), so use the value as of now, not the one read
                // before waiting for the lock.
                var refreshToken = await tokenCache.GetRefreshTokenAsync(cacheKey, lockCt).ConfigureAwait(false);
                var stillValid = StillValidToken(cached, clock.GetUtcNow());
                if (string.IsNullOrWhiteSpace(refreshToken))
                {
                    return stillValid;
                }

                OidcTokenRefreshResult? refreshed;
                try
                {
                    refreshed = await refreshService.RefreshAsync(refreshToken, lockCt).ConfigureAwait(false);
                }
                catch (Exception ex) when (
                    stillValid is not null
                    && !lockCt.IsCancellationRequested
                    && !cancellationToken.IsCancellationRequested
                    && ex is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException)
                {
                    // A transient IdP failure must not turn a token that is still valid into an error.
                    logger.LogWarning(ex, "Token refresh failed inside the refresh-skew window; using the still-valid cached token.");
                    return stillValid;
                }

                if (refreshed is null)
                {
                    if (stillValid is null)
                    {
                        onExpired(cacheKey);
                    }

                    return stillValid;
                }

                await tokenCache.SetAsync(
                    cacheKey,
                    new ServerTokenCacheEntry(refreshed.AccessToken, refreshed.RefreshToken, refreshed.IdToken, refreshed.ExpiresAt),
                    lockCt).ConfigureAwait(false);
                return refreshed.AccessToken;
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static string? StillValidToken(ServerTokenCacheEntry? entry, DateTimeOffset now)
        => entry is not null && !string.IsNullOrWhiteSpace(entry.AccessToken) && entry.ExpiresAtUtc > now
            ? entry.AccessToken
            : null;
}
