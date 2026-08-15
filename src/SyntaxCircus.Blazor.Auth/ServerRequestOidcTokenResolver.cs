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
            async lockCt => await RefreshNearExpiryTokenAsync(
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

    private static async Task PersistRefreshedTokensAsync(HttpContext httpContext, AuthenticateResult authenticateResult, OidcTokenRefreshResult refreshed)
    {
        if (!authenticateResult.Succeeded
            || authenticateResult.Principal is null
            || authenticateResult.Properties is null
            || httpContext.Response.HasStarted)
        {
            return;
        }

        var properties = authenticateResult.Properties;
        properties.UpdateTokenValue("access_token", refreshed.AccessToken);
        properties.UpdateTokenValue("refresh_token", refreshed.RefreshToken);
        properties.UpdateTokenValue("expires_at", refreshed.ExpiresAtTokenValue);
        if (!string.IsNullOrWhiteSpace(refreshed.IdToken))
        {
            properties.UpdateTokenValue("id_token", refreshed.IdToken);
        }

        await httpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, authenticateResult.Principal, properties).ConfigureAwait(false);
    }
}
