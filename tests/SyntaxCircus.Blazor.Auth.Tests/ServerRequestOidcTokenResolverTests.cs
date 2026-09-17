using NSubstitute.Core;

namespace SyntaxCircus.Blazor.Auth.Tests;

public class ServerRequestOidcTokenResolverTests
{
    private static ServerRequestOidcTokenResolver CreateResolver(
        IServerTokenCache tokenCache,
        OidcTokenRefreshService refreshService,
        int refreshSkewSeconds = 60,
        int fallbackLifetimeSeconds = 300,
        TimeProvider? timeProvider = null)
    {
        var options = Options.Create(new AuthOptions
        {
            TokenCache = new AuthOptions.TokenCacheOptions
            {
                RefreshSkewSeconds = refreshSkewSeconds,
                FallbackAccessTokenLifetimeSeconds = fallbackLifetimeSeconds,
            },
        });

        return new ServerRequestOidcTokenResolver(
            tokenCache,
            refreshService,
            new UserTokenCacheKeyProvider(),
            options,
            NullLogger<ServerRequestOidcTokenResolver>.Instance,
            timeProvider);
    }

    /// <summary>A <see cref="TimeProvider"/> whose clock only moves when <see cref="Advance"/> is called.</summary>
    private sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset now = start;

        public void Advance(TimeSpan by) => now += by;

        public override DateTimeOffset GetUtcNow() => now;
    }

    private static OidcTokenRefreshService CreateNeverCalledRefreshService()
        => RefreshServiceFactory.Create(_ => throw new InvalidOperationException("Refresh should not have been called.")).Service;

    [Fact]
    public async Task ResolveAsync_NullHttpContext_ThrowsArgumentNullException()
    {
        var resolver = CreateResolver(new ServerTokenCache(), CreateNeverCalledRefreshService());

        await Should.ThrowAsync<ArgumentNullException>(() => resolver.ResolveAsync(null!, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ResolveAsync_UnauthenticatedUser_ReturnsDefaultResolution()
    {
        var (context, _) = FakeAuthenticationContext.CreateUnauthenticated();
        var resolver = CreateResolver(new ServerTokenCache(), CreateNeverCalledRefreshService());

        var resolution = await resolver.ResolveAsync(context, TestContext.Current.CancellationToken);

        resolution.Token.ShouldBeNull();
        resolution.Subject.ShouldBeNull();
        resolution.CacheKey.ShouldBeNull();
        resolution.IsExpired.ShouldBeFalse();
    }

    [Fact]
    public async Task ResolveAsync_SecondCallOnSameContext_ReusesCachedResolutionWithoutReAuthenticating()
    {
        var (context, authService) = FakeAuthenticationContext.CreateAuthenticated("user-1", new Dictionary<string, string>
        {
            ["access_token"] = "access-1",
            ["refresh_token"] = "refresh-1",
            ["expires_at"] = DateTimeOffset.UtcNow.AddHours(1).UtcDateTime.ToString("O"),
        });
        var resolver = CreateResolver(new ServerTokenCache(), CreateNeverCalledRefreshService());

        await resolver.ResolveAsync(context, TestContext.Current.CancellationToken);
        await resolver.ResolveAsync(context, TestContext.Current.CancellationToken);

        await authService.Received(1).AuthenticateAsync(context, CookieAuthenticationDefaults.AuthenticationScheme);
    }

    [Fact]
    public async Task ResolveAsync_NoAccessTokenInCookie_FallsBackToServerTokenCache()
    {
        var (context, _) = FakeAuthenticationContext.CreateAuthenticated("user-1");
        var tokenCache = new ServerTokenCache();
        await tokenCache.SetAsync("user:user-1", new ServerTokenCacheEntry("cached-access", "cached-refresh", null, DateTimeOffset.UtcNow.AddHours(1)), TestContext.Current.CancellationToken);
        var resolver = CreateResolver(tokenCache, CreateNeverCalledRefreshService());

        var resolution = await resolver.ResolveAsync(context, TestContext.Current.CancellationToken);

        resolution.Token.ShouldBe("cached-access");
        resolution.Subject.ShouldBe("user-1");
        resolution.CacheKey.ShouldBe("user:user-1");
        resolution.IsExpired.ShouldBeFalse();
    }

    [Fact]
    public async Task ResolveAsync_NoAccessTokenInCookieOrCache_ReturnsNullToken()
    {
        var (context, _) = FakeAuthenticationContext.CreateAuthenticated("user-1");
        var resolver = CreateResolver(new ServerTokenCache(), CreateNeverCalledRefreshService());

        var resolution = await resolver.ResolveAsync(context, TestContext.Current.CancellationToken);

        resolution.Token.ShouldBeNull();
        resolution.CacheKey.ShouldBe("user:user-1");
    }

    [Fact]
    public async Task ResolveAsync_ValidUnexpiredCookieToken_CachesItAndReturnsIt()
    {
        var (context, _) = FakeAuthenticationContext.CreateAuthenticated("user-1", new Dictionary<string, string>
        {
            ["access_token"] = "access-1",
            ["expires_at"] = DateTimeOffset.UtcNow.AddHours(1).UtcDateTime.ToString("O"),
        });
        var tokenCache = new ServerTokenCache();
        var resolver = CreateResolver(tokenCache, CreateNeverCalledRefreshService());

        var resolution = await resolver.ResolveAsync(context, TestContext.Current.CancellationToken);

        resolution.Token.ShouldBe("access-1");
        resolution.IsExpired.ShouldBeFalse();
        var cached = await tokenCache.GetAsync("user:user-1", TestContext.Current.CancellationToken);
        cached!.AccessToken.ShouldBe("access-1");
    }

    [Fact]
    public async Task ResolveAsync_ExpiredCookieTokenNoRefreshToken_RemovesFromCacheAndMarksExpired()
    {
        var (context, _) = FakeAuthenticationContext.CreateAuthenticated("user-1", new Dictionary<string, string>
        {
            ["access_token"] = "access-1",
            ["expires_at"] = DateTimeOffset.UtcNow.AddMinutes(-5).UtcDateTime.ToString("O"),
        });
        var tokenCache = new ServerTokenCache();
        await tokenCache.SetAsync("user:user-1", new ServerTokenCacheEntry("stale-cached", "stale-refresh", null, DateTimeOffset.UtcNow.AddMinutes(-5)), TestContext.Current.CancellationToken);
        var resolver = CreateResolver(tokenCache, CreateNeverCalledRefreshService());

        var resolution = await resolver.ResolveAsync(context, TestContext.Current.CancellationToken);

        resolution.Token.ShouldBeNull();
        resolution.IsExpired.ShouldBeTrue();
    }

    [Fact]
    public async Task ResolveAsync_NearExpiryCookieTokenNoRefreshToken_NotYetPastExpiry_ReturnsAccessTokenNotExpired()
    {
        var (context, _) = FakeAuthenticationContext.CreateAuthenticated("user-1", new Dictionary<string, string>
        {
            ["access_token"] = "access-1",
            ["expires_at"] = DateTimeOffset.UtcNow.AddSeconds(30).UtcDateTime.ToString("O"),
        });
        var resolver = CreateResolver(new ServerTokenCache(), CreateNeverCalledRefreshService(), refreshSkewSeconds: 60);

        var resolution = await resolver.ResolveAsync(context, TestContext.Current.CancellationToken);

        resolution.Token.ShouldBe("access-1");
        resolution.IsExpired.ShouldBeFalse();
    }

    [Fact]
    public async Task ResolveAsync_NearExpiryWithRefreshToken_RefreshSucceeds_UpdatesCacheAndSignsIn()
    {
        var (context, authService) = FakeAuthenticationContext.CreateAuthenticated("user-1", new Dictionary<string, string>
        {
            ["access_token"] = "access-1",
            ["refresh_token"] = "refresh-1",
            ["expires_at"] = DateTimeOffset.UtcNow.AddSeconds(30).UtcDateTime.ToString("O"),
        });
        var tokenCache = new ServerTokenCache();
        var (refreshService, _) = RefreshServiceFactory.Create(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { access_token = "refreshed-access", refresh_token = "refreshed-refresh", expires_in = 3600 }),
        });
        var resolver = CreateResolver(tokenCache, refreshService, refreshSkewSeconds: 60);

        var resolution = await resolver.ResolveAsync(context, TestContext.Current.CancellationToken);

        resolution.Token.ShouldBe("refreshed-access");
        resolution.IsExpired.ShouldBeFalse();
        var cached = await tokenCache.GetAsync("user:user-1", TestContext.Current.CancellationToken);
        cached!.AccessToken.ShouldBe("refreshed-access");
        await authService.Received(1).SignInAsync(
            context,
            CookieAuthenticationDefaults.AuthenticationScheme,
            Arg.Any<ClaimsPrincipal>(),
            Arg.Any<AuthenticationProperties>());
    }

    [Fact]
    public async Task ResolveAsync_NearExpiryWithRefreshToken_AnotherRequestAlreadyCachedFreshToken_UsesCachedValueWithoutRefreshing()
    {
        var (context, _) = FakeAuthenticationContext.CreateAuthenticated("user-1", new Dictionary<string, string>
        {
            ["access_token"] = "access-1",
            ["refresh_token"] = "refresh-1",
            ["expires_at"] = DateTimeOffset.UtcNow.AddSeconds(30).UtcDateTime.ToString("O"),
        });
        var tokenCache = new ServerTokenCache();
        await tokenCache.SetAsync("user:user-1", new ServerTokenCacheEntry("already-fresh", "refresh-1", null, DateTimeOffset.UtcNow.AddHours(1)), TestContext.Current.CancellationToken);
        var resolver = CreateResolver(tokenCache, CreateNeverCalledRefreshService(), refreshSkewSeconds: 60);

        var resolution = await resolver.ResolveAsync(context, TestContext.Current.CancellationToken);

        resolution.Token.ShouldBe("already-fresh");
    }

    [Fact]
    public async Task ResolveAsync_NearExpiryRefreshFailsAndTokenAlreadyExpired_RemovesFromCache()
    {
        var (context, _) = FakeAuthenticationContext.CreateAuthenticated("user-1", new Dictionary<string, string>
        {
            ["access_token"] = "access-1",
            ["refresh_token"] = "refresh-1",
            ["expires_at"] = DateTimeOffset.UtcNow.AddMilliseconds(-1).UtcDateTime.ToString("O"),
        });
        var tokenCache = new ServerTokenCache();
        var (refreshService, _) = RefreshServiceFactory.Create(_ => new HttpResponseMessage(HttpStatusCode.BadRequest));
        var resolver = CreateResolver(tokenCache, refreshService, refreshSkewSeconds: 60);

        var resolution = await resolver.ResolveAsync(context, TestContext.Current.CancellationToken);

        resolution.Token.ShouldBeNull();
        resolution.IsExpired.ShouldBeTrue();
        (await tokenCache.GetRefreshTokenAsync("user:user-1", TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task ResolveAsync_NearExpiryRefreshFailsButNotYetExpired_ReturnsCurrentAccessToken()
    {
        var (context, _) = FakeAuthenticationContext.CreateAuthenticated("user-1", new Dictionary<string, string>
        {
            ["access_token"] = "access-1",
            ["refresh_token"] = "refresh-1",
            ["expires_at"] = DateTimeOffset.UtcNow.AddSeconds(30).UtcDateTime.ToString("O"),
        });
        var tokenCache = new ServerTokenCache();
        var (refreshService, _) = RefreshServiceFactory.Create(_ => new HttpResponseMessage(HttpStatusCode.BadRequest));
        var resolver = CreateResolver(tokenCache, refreshService, refreshSkewSeconds: 60);

        var resolution = await resolver.ResolveAsync(context, TestContext.Current.CancellationToken);

        resolution.Token.ShouldBe("access-1");
        resolution.IsExpired.ShouldBeFalse();
    }

    [Fact]
    public async Task ResolveAsync_LongLivedCircuitContext_PastCachedValidityWindow_ReResolvesInsteadOfReusingStaleResolutionForever()
    {
        var start = DateTimeOffset.UtcNow;
        var (context, authService) = FakeAuthenticationContext.CreateAuthenticated("user-1", new Dictionary<string, string>
        {
            ["access_token"] = "access-1",
            ["expires_at"] = start.AddMinutes(2).UtcDateTime.ToString("O"),
        });
        var clock = new ManualTimeProvider(start);
        var resolver = CreateResolver(new ServerTokenCache(), CreateNeverCalledRefreshService(), refreshSkewSeconds: 60, timeProvider: clock);

        // First call, simulating a Blazor Server circuit's very first API call. Its resolution gets
        // cached onto the same HttpContext.Items instance that this circuit's ambient HttpContext
        // keeps alive for its entire (potentially hours-long) SignalR connection.
        var first = await resolver.ResolveAsync(context, TestContext.Current.CancellationToken);
        first.Token.ShouldBe("access-1");

        // A second call on the SAME HttpContext, still well inside the token's validity window,
        // must keep reusing the cached resolution rather than re-authenticating (existing
        // single-resolution-per-request behavior/perf optimization). (No "refresh_token" is stored
        // on this fixture, so each *real* resolution costs two AuthenticateAsync calls - one
        // directly, one via the fallback HttpContext.GetTokenAsync lookup for the absent
        // refresh_token - so "no new real resolution happened" shows up as the call count staying
        // flat across this second call, not as exactly one call overall.)
        await resolver.ResolveAsync(context, TestContext.Current.CancellationToken);
        var callsAfterCacheHit = authService.ReceivedCalls().Count(call => call.GetMethodInfo().Name == nameof(IAuthenticationService.AuthenticateAsync));
        callsAfterCacheHit.ShouldBe(2);

        // Advance the clock past the access token's (skew-adjusted) validity window without the
        // circuit ever producing a new HTTP request - exactly what "Admin tab left open several
        // minutes past the last successful call, no reload" looks like in production. The resolver
        // must now re-check the cookie/cache instead of returning the same resolution it computed
        // minutes earlier.
        clock.Advance(TimeSpan.FromMinutes(3));

        var third = await resolver.ResolveAsync(context, TestContext.Current.CancellationToken);

        var callsAfterReResolve = authService.ReceivedCalls().Count(call => call.GetMethodInfo().Name == nameof(IAuthenticationService.AuthenticateAsync));
        callsAfterReResolve.ShouldBe(4, "a real re-resolution should have happened once the cached validity window elapsed");
        third.Token.ShouldBeNull();
        third.IsExpired.ShouldBeTrue();
    }

    [Fact]
    public async Task ResolveAsync_NearExpiryCookieButCacheHasDivergedRefreshTokenAndFreshAccessToken_UsesCachedTokenAndResyncsCookieWithoutRefreshing()
    {
        var (context, authService) = FakeAuthenticationContext.CreateAuthenticated("user-1", new Dictionary<string, string>
        {
            ["access_token"] = "access-1",
            ["refresh_token"] = "refresh-1",
            ["expires_at"] = DateTimeOffset.UtcNow.AddSeconds(30).UtcDateTime.ToString("O"),
        });
        var tokenCache = new ServerTokenCache();
        await tokenCache.SetAsync("user:user-1", new ServerTokenCacheEntry("cache-fresher-access", "refresh-2", null, DateTimeOffset.UtcNow.AddHours(1)), TestContext.Current.CancellationToken);
        var resolver = CreateResolver(tokenCache, CreateNeverCalledRefreshService(), refreshSkewSeconds: 60);

        var resolution = await resolver.ResolveAsync(context, TestContext.Current.CancellationToken);

        resolution.Token.ShouldBe("cache-fresher-access");
        resolution.IsExpired.ShouldBeFalse();
        await authService.Received(1).SignInAsync(
            context,
            CookieAuthenticationDefaults.AuthenticationScheme,
            Arg.Any<ClaimsPrincipal>(),
            Arg.Any<AuthenticationProperties>());
    }

    [Fact]
    public async Task ResolveAsync_NearExpiryCookieStaleAndCacheAccessTokenAlsoExpiredButRefreshTokenRotated_RefreshesWithCachedRefreshTokenNotCookies()
    {
        var (context, authService) = FakeAuthenticationContext.CreateAuthenticated("user-1", new Dictionary<string, string>
        {
            ["access_token"] = "access-1",
            ["refresh_token"] = "refresh-1",
            ["expires_at"] = DateTimeOffset.UtcNow.AddMinutes(-5).UtcDateTime.ToString("O"),
        });
        var tokenCache = new ServerTokenCache();
        await tokenCache.SetAsync("user:user-1", new ServerTokenCacheEntry("cache-stale-access", "refresh-2", null, DateTimeOffset.UtcNow.AddMinutes(-1)), TestContext.Current.CancellationToken);
        var (refreshService, _) = RefreshServiceFactory.Create(request =>
        {
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            if (body.Contains("refresh_token=refresh-2", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new { access_token = "refreshed-access", refresh_token = "refresh-3", expires_in = 3600 }),
                };
            }

            // The cookie's refresh token (refresh-1) has already been rotated away at the IdP.
            return new HttpResponseMessage(HttpStatusCode.BadRequest);
        });
        var resolver = CreateResolver(tokenCache, refreshService, refreshSkewSeconds: 60);

        var resolution = await resolver.ResolveAsync(context, TestContext.Current.CancellationToken);

        resolution.Token.ShouldBe("refreshed-access");
        resolution.IsExpired.ShouldBeFalse();
        var cached = await tokenCache.GetAsync("user:user-1", TestContext.Current.CancellationToken);
        cached!.AccessToken.ShouldBe("refreshed-access");
        cached.RefreshToken.ShouldBe("refresh-3");
        await authService.Received(1).SignInAsync(
            context,
            CookieAuthenticationDefaults.AuthenticationScheme,
            Arg.Any<ClaimsPrincipal>(),
            Arg.Any<AuthenticationProperties>());
    }
}
