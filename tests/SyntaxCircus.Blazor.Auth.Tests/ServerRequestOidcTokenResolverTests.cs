namespace SyntaxCircus.Blazor.Auth.Tests;

public class ServerRequestOidcTokenResolverTests
{
    private static ServerRequestOidcTokenResolver CreateResolver(
        IServerTokenCache tokenCache,
        OidcTokenRefreshService refreshService,
        int refreshSkewSeconds = 60,
        int fallbackLifetimeSeconds = 300)
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
            NullLogger<ServerRequestOidcTokenResolver>.Instance);
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
}
