using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Components.Authorization;

namespace SyntaxCircus.Blazor.Auth;

/// <summary>
/// Attaches a bearer token to outgoing typed-client requests. Prefers the current user's OIDC
/// access token (resolved via <see cref="ServerRequestOidcTokenResolver"/> when an HttpContext is
/// available, or via the server-side <see cref="IServerTokenCache"/> during the Blazor Server
/// SignalR circuit phase where it isn't). Falls back to a client-credentials (M2M) token when the
/// user is anonymous and <see cref="IApiClientCredentialsTokenProvider"/> is configured. A 401
/// response evicts the cached user token and marks the session expired.
/// </summary>
public sealed class ApiAuthHandler : DelegatingHandler
{
    private readonly IHttpContextAccessor httpContextAccessor;
    private readonly IServerTokenCache tokenCache;
    private readonly ServerRequestOidcTokenResolver resolver;
    private readonly OidcTokenRefreshService refreshService;
    private readonly IApiClientCredentialsTokenProvider clientCredentialsTokenProvider;
    private readonly ILogger<ApiAuthHandler> logger;
    private readonly SessionExpiryBroker? sessionExpiryBroker;
    private readonly AuthenticationStateProvider? compatibilityAuthenticationStateProvider;
    private readonly IUserTokenCacheKeyProvider? compatibilityCacheKeyProvider;
    private readonly SessionStateService? compatibilitySessionStateService;

    /// <summary>Creates the handler used by the registered production path.</summary>
    public ApiAuthHandler(
        IHttpContextAccessor httpContextAccessor,
        IServerTokenCache tokenCache,
        ServerRequestOidcTokenResolver resolver,
        OidcTokenRefreshService refreshService,
        IApiClientCredentialsTokenProvider clientCredentialsTokenProvider,
        ILogger<ApiAuthHandler> logger,
        SessionExpiryBroker sessionExpiryBroker)
    {
        this.httpContextAccessor = httpContextAccessor;
        this.tokenCache = tokenCache;
        this.resolver = resolver;
        this.refreshService = refreshService;
        this.clientCredentialsTokenProvider = clientCredentialsTokenProvider;
        this.logger = logger;
        this.sessionExpiryBroker = sessionExpiryBroker;
    }

    /// <summary>Preserves source compatibility for callers that constructed the handler directly.</summary>
    public ApiAuthHandler(
        IHttpContextAccessor httpContextAccessor,
        IServerTokenCache tokenCache,
        ServerRequestOidcTokenResolver resolver,
        OidcTokenRefreshService refreshService,
        AuthenticationStateProvider authenticationStateProvider,
        IUserTokenCacheKeyProvider cacheKeyProvider,
        SessionStateService sessionStateService,
        IApiClientCredentialsTokenProvider clientCredentialsTokenProvider,
        ILogger<ApiAuthHandler> logger)
        : this(
            httpContextAccessor,
            tokenCache,
            resolver,
            refreshService,
            clientCredentialsTokenProvider,
            logger,
            sessionExpiryBroker: null!)
    {
        compatibilityAuthenticationStateProvider = authenticationStateProvider;
        compatibilityCacheKeyProvider = cacheKeyProvider;
        compatibilitySessionStateService = sessionStateService;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var (token, cacheKey, source) = await ResolveAccessTokenAsync(request, cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        else
        {
            logger.LogWarning("No access token available ({Source}) for {Path}", source, request.RequestUri?.PathAndQuery);
        }

        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.Unauthorized && source == TokenSource.UserOidc && cacheKey is not null)
        {
            await tokenCache.RemoveAsync(cacheKey, cancellationToken).ConfigureAwait(false);
            PublishExpired(cacheKey);
        }

        return response;
    }

    private async Task<(string? Token, string? CacheKey, TokenSource Source)> ResolveAccessTokenAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var (userToken, userCacheKey) = await TryResolveUserTokenAsync(request, cancellationToken).ConfigureAwait(false);
        if (userToken is not null)
        {
            return (userToken, userCacheKey, TokenSource.UserOidc);
        }

        if (clientCredentialsTokenProvider.IsConfigured)
        {
            try
            {
                var m2mToken = await clientCredentialsTokenProvider.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
                return (m2mToken, null, TokenSource.ClientCredentials);
            }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException)
            {
                logger.LogWarning(ex, "Client-credentials token retrieval failed; request will go out unauthenticated.");
            }
        }

        return (null, userCacheKey, TokenSource.None);
    }

    private async Task<(string? Token, string? CacheKey)> TryResolveUserTokenAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var httpContext = httpContextAccessor.HttpContext;
        if (httpContext is not null)
        {
            if (httpContext.User.Identity?.IsAuthenticated != true)
            {
                return (null, null);
            }

            var resolution = await resolver.ResolveAsync(httpContext, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(resolution.Token) && resolution.IsExpired && !string.IsNullOrWhiteSpace(resolution.CacheKey))
            {
                PublishExpired(resolution.CacheKey);
            }

            return (resolution.Token, resolution.CacheKey);
        }

        if (request.Options.TryGetValue(BlazorTokenRequestOptions.CacheKey, out string? requestCacheKey)
            && !string.IsNullOrWhiteSpace(requestCacheKey))
        {
            var cacheKey = requestCacheKey.Trim();
            return (await TryResolveCachedTokenAsync(cacheKey, cancellationToken).ConfigureAwait(false), cacheKey);
        }

        if (compatibilityAuthenticationStateProvider is null || compatibilityCacheKeyProvider is null)
        {
            return (null, null);
        }

        // Compatibility mode retains the v0.1.5 direct-construction circuit behavior.
        try
        {
            var authState = await compatibilityAuthenticationStateProvider.GetAuthenticationStateAsync().ConfigureAwait(false);
            if (authState.User.Identity?.IsAuthenticated != true)
            {
                return (null, null);
            }

            var cacheKey = compatibilityCacheKeyProvider.GetCacheKey(authState.User);
            if (string.IsNullOrWhiteSpace(cacheKey))
            {
                return (null, null);
            }

            return (await TryResolveCachedTokenAsync(cacheKey, cancellationToken).ConfigureAwait(false), cacheKey);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Failed to resolve authentication state from the current circuit.");
            return (null, null);
        }
    }

    private async Task<string?> TryResolveCachedTokenAsync(string cacheKey, CancellationToken cancellationToken)
    {
        var cachedEntry = await tokenCache.GetAsync(cacheKey, cancellationToken).ConfigureAwait(false);
        if (cachedEntry is not null)
        {
            return cachedEntry.AccessToken;
        }

        return await TryRefreshInCircuitAsync(cacheKey, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> TryRefreshInCircuitAsync(string cacheKey, CancellationToken cancellationToken)
    {
        var refreshToken = await tokenCache.GetRefreshTokenAsync(cacheKey, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return null;
        }

        return await tokenCache.WithRefreshLockAsync(
            cacheKey,
            async lockCt =>
            {
                var cached = await tokenCache.GetAsync(cacheKey, lockCt).ConfigureAwait(false);
                if (cached is not null)
                {
                    return cached.AccessToken;
                }

                var refreshed = await refreshService.RefreshAsync(refreshToken, lockCt).ConfigureAwait(false);
                if (refreshed is null)
                {
                    PublishExpired(cacheKey);
                    return null;
                }

                await tokenCache.SetAsync(
                    cacheKey,
                    new ServerTokenCacheEntry(refreshed.AccessToken, refreshed.RefreshToken, refreshed.IdToken, refreshed.ExpiresAt),
                    lockCt).ConfigureAwait(false);
                return refreshed.AccessToken;
            },
            cancellationToken).ConfigureAwait(false);
    }

    private void PublishExpired(string cacheKey)
    {
        if (sessionExpiryBroker is not null)
        {
            sessionExpiryBroker.Publish(cacheKey);
            return;
        }

        compatibilitySessionStateService?.MarkExpired();
    }

    private enum TokenSource
    {
        None,
        UserOidc,
        ClientCredentials,
    }
}
