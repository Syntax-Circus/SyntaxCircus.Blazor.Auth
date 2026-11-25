namespace SyntaxCircus.Blazor.Auth.Tests;

/// <summary>
/// End-to-end isolation of cached tokens between one user's sign-in sessions, through the real
/// cookie handler.
/// </summary>
public class SessionTokenIsolationTests
{
    [Fact]
    public async Task TwoSignInsOfOneUser_UseDifferentCacheKeys()
    {
        using var host = CookieSessionHost.Create();
        var first = host.NewBrowser();
        var second = host.NewBrowser();
        await first.LoginAsync("user-1", "A", expiresIn: 3600);
        await second.LoginAsync("user-1", "B", expiresIn: 3600);

        var firstView = await first.MeAsync();
        var secondView = await second.MeAsync();

        firstView.CacheKey.ShouldNotBeNull();
        secondView.CacheKey.ShouldNotBe(firstView.CacheKey);
        firstView.CachedRefreshToken.ShouldBe("refresh-A");
        secondView.CachedRefreshToken.ShouldBe("refresh-B");
    }

    [Fact]
    public async Task OlderSessionNearExpiry_NeverReceivesTheNewerSessionsTokens()
    {
        using var host = CookieSessionHost.Create();
        var older = host.NewBrowser();
        await older.LoginAsync("user-1", "A", expiresIn: 30);
        var copiedOlderCookie = older.Copy();
        var newer = host.NewBrowser();
        await newer.LoginAsync("user-1", "B", expiresIn: 3600);
        (await newer.MeAsync()).Token.ShouldBe("access-B");

        var view = await copiedOlderCookie.MeAsync();
        var cookie = await copiedOlderCookie.CookieTokensAsync();

        view.Token.ShouldBe("access-A+");
        view.CachedRefreshToken.ShouldBe("refresh-A+");
        cookie.AccessToken.ShouldBe("access-A+");
        cookie.RefreshToken.ShouldBe("refresh-A+");
        host.IdentityProvider.Presented.ShouldNotContain("refresh-B");
        (await newer.MeAsync()).Token.ShouldBe("access-B");
    }

    [Fact]
    public async Task TokenRefresh_KeepsTheSessionIdAndCacheKey()
    {
        using var host = CookieSessionHost.Create();
        var browser = host.NewBrowser();
        await browser.LoginAsync("user-1", "A", expiresIn: 30);

        var refreshing = await browser.MeAsync();
        var afterRefresh = await browser.MeAsync();

        refreshing.Token.ShouldBe("access-A+");
        (await browser.CookieTokensAsync()).RefreshToken.ShouldBe("refresh-A+");
        afterRefresh.CacheKey.ShouldBe(refreshing.CacheKey);
        afterRefresh.SessionIds.ShouldHaveSingleItem().ShouldBe(refreshing.SessionIds.ShouldHaveSingleItem());
    }

    [Fact]
    public async Task AppReSigningTheCurrentPrincipal_KeepsTheSessionId()
    {
        using var host = CookieSessionHost.Create();
        var browser = host.NewBrowser();
        await browser.LoginAsync("user-1", "A", expiresIn: 3600);
        var before = await browser.MeAsync();

        await browser.GetAsync("/resign");

        (await browser.MeAsync()).SessionIds.ShouldBe(before.SessionIds);
    }

    [Fact]
    public async Task SigningInAgainInTheSameBrowser_StartsANewSession()
    {
        using var host = CookieSessionHost.Create();
        var browser = host.NewBrowser();
        await browser.LoginAsync("user-1", "A", expiresIn: 3600);
        var first = await browser.MeAsync();

        await browser.LoginAsync("user-1", "B", expiresIn: 3600);

        (await browser.MeAsync()).CacheKey.ShouldNotBe(first.CacheKey);
    }

    [Fact]
    public async Task SessionIdSuppliedByTheIdentityProvider_IsReplacedOnSignIn()
    {
        using var host = CookieSessionHost.Create();
        var victim = host.NewBrowser();
        await victim.LoginAsync("user-1", "A", expiresIn: 3600);
        var victimId = (await victim.MeAsync()).SessionIds.ShouldHaveSingleItem();
        var attacker = host.NewBrowser();

        await attacker.LoginAsync("user-1", "B", expiresIn: 3600, forgedSessionId: victimId);

        (await attacker.MeAsync()).SessionIds.ShouldHaveSingleItem().ShouldNotBe(victimId);
    }

    [Fact]
    public async Task CircuitCallWithTheSessionsPrincipal_FindsThatSessionsTokens()
    {
        using var host = CookieSessionHost.Create();
        var first = host.NewBrowser();
        var second = host.NewBrowser();
        await first.LoginAsync("user-1", "A", expiresIn: 3600);
        await second.LoginAsync("user-1", "B", expiresIn: 3600);
        var firstPrincipal = await first.PrincipalAsync();
        var secondPrincipal = await second.PrincipalAsync();

        (await CircuitTokenAsync(host, firstPrincipal)).ShouldBe("access-A");
        (await CircuitTokenAsync(host, secondPrincipal)).ShouldBe("access-B");
    }

    [Fact]
    public async Task PreUpgradeCookie_KeepsTheUserKeyThroughATokenRefresh()
    {
        using var host = CookieSessionHost.Create();
        var browser = host.NewBrowser();
        await browser.PreUpgradeLoginAsync("user-1", "A", expiresIn: 30);

        var refreshing = await browser.MeAsync();
        var afterRefresh = await browser.MeAsync();

        refreshing.Token.ShouldBe("access-A+");
        (await browser.CookieTokensAsync()).RefreshToken.ShouldBe("refresh-A+");
        afterRefresh.CacheKey.ShouldBe("user:user-1");
        afterRefresh.SessionIds.ShouldBeEmpty();
        host.Logs.LibraryWarnings.ShouldBeEmpty();
    }

    [Fact]
    public async Task PreUpgradeCircuitOpenAcrossACookieRefresh_NeverMakesARotatedRefreshTokenBeReplayed()
    {
        using var host = CookieSessionHost.Create();
        host.IdentityProvider.RotatesRefreshTokens = true;
        host.IdentityProvider.ExpiresInSeconds = 30; // every issued token is already due for refresh
        var browser = host.NewBrowser();
        await browser.PreUpgradeLoginAsync("user-1", "A", expiresIn: 30);

        // This circuit's principal predates the cookie being re-signed by the same request's refresh.
        var circuitPrincipal = await browser.PrincipalAsync();
        (await CircuitTokenAsync(host, circuitPrincipal)).ShouldBe("access-A++");

        // The browser's next request still holds refresh-A+, which the circuit has now redeemed.
        await browser.MeAsync();

        host.IdentityProvider.Presented.ShouldBeUnique();
    }

    [Fact]
    public async Task SessionWithNothingCached_NeverFallsBackToThePerUserOrAnotherSessionsEntry()
    {
        using var host = CookieSessionHost.Create();
        var first = host.NewBrowser();
        var second = host.NewBrowser();
        await first.LoginAsync("user-1", "A", expiresIn: 3600);
        await second.LoginAsync("user-1", "B", expiresIn: 3600);
        var firstPrincipal = await first.PrincipalAsync();
        var firstKey = (await first.MeAsync()).CacheKey!;
        await second.MeAsync();
        var cache = host.Services.GetRequiredService<IServerTokenCache>();
        await cache.SetAsync(
            "user:user-1",
            new ServerTokenCacheEntry("access-shared", "refresh-shared", null, DateTimeOffset.UtcNow.AddHours(1)),
            TestContext.Current.CancellationToken);
        await cache.RemoveAsync(firstKey, TestContext.Current.CancellationToken);

        (await CircuitTokenAsync(host, firstPrincipal)).ShouldBeNull();
        host.IdentityProvider.Presented.ShouldBeEmpty();
    }

    private static async Task<string?> CircuitTokenAsync(CookieSessionHost host, ClaimsPrincipal principal)
    {
        // A fresh DI scope with no HttpContext, which is what an interactive circuit looks like.
        using var scope = host.Services.CreateScope();
        var authenticationState = scope.ServiceProvider.GetRequiredService<AuthenticationStateProvider>();
        authenticationState.GetAuthenticationStateAsync().Returns(new AuthenticationState(principal));
        return await scope.ServiceProvider.GetRequiredService<IUserAccessTokenProvider>()
            .GetAccessTokenAsync(TestContext.Current.CancellationToken);
    }
}
