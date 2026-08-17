using System.Globalization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace SyntaxCircus.Blazor.Auth;

/// <summary>
/// Resolves the current request's OIDC access token: reads it from the auth cookie, refreshes it
/// (lock-guarded, cache-checked) when it's within <c>RefreshSkewSeconds</c> of expiry, and falls
/// back to <see cref="IServerTokenCache"/> when the cookie has nothing usable for this request.
/// Caches its result on <see cref="HttpContext.Items"/> so a single request only resolves once.
/// </summary>
public sealed class ServerRequestOidcTokenResolver(
    IServerTokenCache tokenCache,
    OidcTokenRefreshService refreshService,
    IUserTokenCacheKeyProvider cacheKeyProvider,
    IOptions<AuthOptions> options,
    ILogger<ServerRequestOidcTokenResolver> logger)
{
    internal const string HttpContextItemKey = "SyntaxCircus.Blazor.Auth.ServerRequestOidcTokenResolution";

    public async Task<ServerRequestOidcTokenResolution> ResolveAsync(HttpContext httpContext, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        if (httpContext.Items.TryGetValue(HttpContextItemKey, out var cached)
            && cached is ServerRequestOidcTokenResolution cachedResolution)
        {
            return cachedResolution;
        }

        var resolution = await ResolveCoreAsync(httpContext, cancellationToken).ConfigureAwait(false);
        httpContext.Items[HttpContextItemKey] = resolution;
        return resolution;
    }

    private async Task<ServerRequestOidcTokenResolution> ResolveCoreAsync(HttpContext httpContext, CancellationToken cancellationToken)
    {
        if (httpContext.User.Identity?.IsAuthenticated != true)
        {
            return default;
        }

        var subject = UserTokenCacheKeyProvider.ResolveSubject(httpContext.User);
        var cacheKey = cacheKeyProvider.GetCacheKey(subject);
        if (string.IsNullOrWhiteSpace(cacheKey))
        {
            return default;
        }

        var authenticateResult = await httpContext.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme).ConfigureAwait(false);
        var authProperties = authenticateResult.Succeeded ? authenticateResult.Properties : null;

        var accessToken = await ReadTokenAsync(httpContext, authProperties, "access_token").ConfigureAwait(false);
        var refreshToken = await ReadTokenAsync(httpContext, authProperties, "refresh_token").ConfigureAwait(false);
        var expiresAtText = await ReadTokenAsync(httpContext, authProperties, "expires_at").ConfigureAwait(false);

        var refreshSkew = TimeSpan.FromSeconds(Math.Max(0, options.Value.TokenCache.RefreshSkewSeconds));
        var fallbackLifetime = TimeSpan.FromSeconds(Math.Max(30, options.Value.TokenCache.FallbackAccessTokenLifetimeSeconds));
        var now = DateTimeOffset.UtcNow;

        if (string.IsNullOrWhiteSpace(accessToken))
        {
            // Nothing in the cookie for this request — fall back to whatever is already cached
            // (e.g. resolved earlier in the same circuit).
            var cachedEntry = await tokenCache.GetAsync(cacheKey, cancellationToken).ConfigureAwait(false);
            return new ServerRequestOidcTokenResolution(cachedEntry?.AccessToken, subject, cacheKey, false);
        }

        var expiresAt = OidcTokenExpiry.Resolve(expiresAtText, expiresIn: null, accessToken, now, fallbackLifetime);
        if (expiresAt - refreshSkew > now)
        {
            await tokenCache.SetAsync(cacheKey, new ServerTokenCacheEntry(accessToken, refreshToken, null, expiresAt), cancellationToken).ConfigureAwait(false);
            return new ServerRequestOidcTokenResolution(accessToken, subject, cacheKey, false);
        }

        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            var isExpired = expiresAt <= now;
            if (isExpired)
            {
                await tokenCache.RemoveAsync(cacheKey, cancellationToken).ConfigureAwait(false);
            }

            return new ServerRequestOidcTokenResolution(isExpired ? null : accessToken, subject, cacheKey, isExpired);
        }

        return await tokenCache.WithRefreshLockAsync(
            cacheKey,
            async lockCt => await ResolveNearExpiryTokenAsync(
                httpContext,
                authenticateResult,
                cacheKey,
                subject,
                refreshToken,
                accessToken,
                expiresAt,
                lockCt).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs inside the per-cache-key refresh lock. Detects the case where a SignalR-circuit-path
    /// refresh (<see cref="ApiAuthHandler.TryRefreshInCircuitAsync"/>) already rotated the refresh
    /// token in <see cref="IServerTokenCache"/> with no HttpContext available to persist it back to
    /// the cookie — the stale cookie's refresh token may already be rejected by an OIDC provider
    /// that rotates refresh tokens on use. When the cache's refresh token differs from the cookie's,
    /// prefer the cache: reuse its access token directly if still usable, or fall back to refreshing
    /// with the cache's (fresher) refresh token instead of the cookie's.
    /// </summary>
    private async Task<ServerRequestOidcTokenResolution> ResolveNearExpiryTokenAsync(
        HttpContext httpContext,
        AuthenticateResult authenticateResult,
        string cacheKey,
        string? subject,
        string cookieRefreshToken,
        string currentAccessToken,
        DateTimeOffset currentExpiry,
        CancellationToken cancellationToken)
    {
        var cachedRefreshToken = await tokenCache.GetRefreshTokenAsync(cacheKey, cancellationToken).ConfigureAwait(false);
        var refreshTokenDiverged = !string.IsNullOrWhiteSpace(cachedRefreshToken)
            && !string.Equals(cachedRefreshToken, cookieRefreshToken, StringComparison.Ordinal);

        if (!refreshTokenDiverged)
        {
            return await RefreshNearExpiryTokenAsync(
                httpContext, authenticateResult, cacheKey, subject, cookieRefreshToken, currentAccessToken, currentExpiry, cancellationToken).ConfigureAwait(false);
        }

        var cachedEntry = await tokenCache.GetAsync(cacheKey, cancellationToken).ConfigureAwait(false);
        if (cachedEntry is not null)
        {
            await PersistRefreshedTokensAsync(httpContext, authenticateResult, cachedEntry).ConfigureAwait(false);
            return new ServerRequestOidcTokenResolution(cachedEntry.AccessToken, subject, cacheKey, false);
        }

        // The cache's own access token has also expired, but its refresh token is fresher than the
        // cookie's — refresh with that instead of retrying the cookie's already-invalid one.
        return await RefreshNearExpiryTokenAsync(
            httpContext, authenticateResult, cacheKey, subject, cachedRefreshToken!, currentAccessToken, currentExpiry, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ServerRequestOidcTokenResolution> RefreshNearExpiryTokenAsync(
        HttpContext httpContext,
        AuthenticateResult authenticateResult,
        string cacheKey,
        string? subject,
        string refreshToken,
        string currentAccessToken,
        DateTimeOffset currentExpiry,
        CancellationToken cancellationToken)
    {
        var raced = await tokenCache.GetAsync(cacheKey, cancellationToken).ConfigureAwait(false);
        if (raced is not null)
        {
            return new ServerRequestOidcTokenResolution(raced.AccessToken, subject, cacheKey, false);
        }

        var refreshed = await refreshService.RefreshAsync(refreshToken, cancellationToken).ConfigureAwait(false);
        if (refreshed is null)
        {
            logger.LogWarning("OIDC token refresh failed for cache key '{CacheKey}'.", cacheKey);
            var isExpired = currentExpiry <= DateTimeOffset.UtcNow;
            if (isExpired)
            {
                await tokenCache.RemoveAsync(cacheKey, cancellationToken).ConfigureAwait(false);
            }

            return new ServerRequestOidcTokenResolution(isExpired ? null : currentAccessToken, subject, cacheKey, isExpired);
        }

        await tokenCache.SetAsync(
            cacheKey,
            new ServerTokenCacheEntry(refreshed.AccessToken, refreshed.RefreshToken, refreshed.IdToken, refreshed.ExpiresAt),
            cancellationToken).ConfigureAwait(false);
        await PersistRefreshedTokensAsync(httpContext, authenticateResult, refreshed).ConfigureAwait(false);
        return new ServerRequestOidcTokenResolution(refreshed.AccessToken, subject, cacheKey, false);
    }

    private static async Task<string?> ReadTokenAsync(HttpContext httpContext, AuthenticationProperties? authProperties, string tokenName)
    {
        var value = authProperties?.GetTokenValue(tokenName);
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value.Trim();
        }

        var contextValue = await httpContext.GetTokenAsync(CookieAuthenticationDefaults.AuthenticationScheme, tokenName).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(contextValue) ? null : contextValue.Trim();
    }

    private static Task PersistRefreshedTokensAsync(HttpContext httpContext, AuthenticateResult authenticateResult, OidcTokenRefreshResult refreshed)
        => PersistTokensAsync(httpContext, authenticateResult, refreshed.AccessToken, refreshed.RefreshToken, refreshed.IdToken, refreshed.ExpiresAtTokenValue);

    /// <summary>
    /// Re-signs the cookie from a <see cref="IServerTokenCache"/> entry that's ahead of what the
    /// cookie currently holds (see <see cref="ResolveNearExpiryTokenAsync"/>), rather than from a
    /// freshly-issued <see cref="OidcTokenRefreshResult"/>.
    /// </summary>
    private static Task PersistRefreshedTokensAsync(HttpContext httpContext, AuthenticateResult authenticateResult, ServerTokenCacheEntry entry)
        => PersistTokensAsync(
            httpContext,
            authenticateResult,
            entry.AccessToken,
            entry.RefreshToken,
            entry.IdToken,
            entry.ExpiresAtUtc.UtcDateTime.ToString("o", CultureInfo.InvariantCulture));

    private static async Task PersistTokensAsync(
        HttpContext httpContext,
        AuthenticateResult authenticateResult,
        string accessToken,
        string? refreshToken,
        string? idToken,
        string expiresAtTokenValue)
    {
        if (!authenticateResult.Succeeded
            || authenticateResult.Principal is null
            || authenticateResult.Properties is null
            || httpContext.Response.HasStarted)
        {
            return;
        }

        var properties = authenticateResult.Properties;
        properties.UpdateTokenValue("access_token", accessToken);
        if (!string.IsNullOrWhiteSpace(refreshToken))
        {
            properties.UpdateTokenValue("refresh_token", refreshToken);
        }

        properties.UpdateTokenValue("expires_at", expiresAtTokenValue);
        if (!string.IsNullOrWhiteSpace(idToken))
        {
            properties.UpdateTokenValue("id_token", idToken);
        }

        await httpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, authenticateResult.Principal, properties).ConfigureAwait(false);
    }
}
