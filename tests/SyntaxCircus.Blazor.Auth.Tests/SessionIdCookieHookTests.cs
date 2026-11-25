namespace SyntaxCircus.Blazor.Auth.Tests;

/// <summary>When a cookie sign-in continues a session, and when it must start a new one.</summary>
public class SessionIdCookieHookTests
{
    private const string RecordedIdItem = ".SyntaxCircus.Blazor.Auth.SessionId";

    [Fact]
    public async Task RebuiltPrincipalSignedWithTheCookiesOwnProperties_KeepsTheSessionId()
    {
        using var host = CookieSessionHost.Create();
        var browser = host.NewBrowser();
        await browser.LoginAsync("user-1", "A", expiresIn: 3600);
        var before = await browser.MeAsync();

        await browser.GetAsync("/resign-rebuilt");

        (await browser.MeAsync()).CacheKey.ShouldBe(before.CacheKey);
    }

    [Fact]
    public async Task CircuitOpenAcrossAnAppReSignWithARebuiltPrincipal_NeverMakesARotatedRefreshTokenBeReplayed()
    {
        using var host = CookieSessionHost.Create();
        host.IdentityProvider.RotatesRefreshTokens = true;
        host.IdentityProvider.ExpiresInSeconds = 30; // every issued token is already due for refresh
        var browser = host.NewBrowser();
        await browser.LoginAsync("user-1", "A", expiresIn: 30);
        var circuitPrincipal = await browser.PrincipalAsync();

        await browser.GetAsync("/resign-rebuilt");
        await CircuitTokenAsync(host, circuitPrincipal);
        var view = await browser.MeAsync();

        host.IdentityProvider.Presented.ShouldBeUnique();
        view.Token.ShouldNotBeNull();
    }

    [Fact]
    public async Task ReSignWithTheCookiesPropertiesForAnotherSubject_StartsANewSessionId()
    {
        using var host = CookieSessionHost.Create();
        var browser = host.NewBrowser();
        await browser.LoginAsync("user-1", "A", expiresIn: 3600);
        var before = (await browser.MeAsync()).SessionIds.ShouldHaveSingleItem();

        await browser.GetAsync("/resign-rebuilt?sub=user-2");

        (await browser.MeAsync()).SessionIds.ShouldHaveSingleItem().ShouldNotBe(before);
    }

    [Fact]
    public async Task ItemsCopiedIntoOtherAuthenticationProperties_DoNotCarryTheSessionIdOver()
    {
        using var host = CookieSessionHost.Create();
        var browser = host.NewBrowser();
        await browser.LoginAsync("user-1", "A", expiresIn: 3600);
        var before = (await browser.MeAsync()).SessionIds.ShouldHaveSingleItem();

        await browser.GetAsync("/resign-rebuilt?copyProperties=true");

        (await browser.MeAsync()).SessionIds.ShouldHaveSingleItem().ShouldNotBe(before);
    }

    [Fact]
    public async Task ReSignFromInsideOnValidatePrincipalWithARebuiltPrincipal_KeepsTheSessionId()
    {
        var reSign = false;
        using var host = CookieSessionHost.Create(
            configureBefore: services => services.Configure<CookieAuthenticationOptions>(
                CookieAuthenticationDefaults.AuthenticationScheme,
                options => options.Events.OnValidatePrincipal = async context =>
                {
                    if (reSign)
                    {
                        reSign = false;
                        await context.HttpContext.SignInAsync(
                            context.Scheme.Name,
                            new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "user-1")], "rebuilt")),
                            context.Properties);
                    }
                }));
        var browser = host.NewBrowser();
        await browser.LoginAsync("user-1", "A", expiresIn: 3600);
        var before = await browser.MeAsync();
        reSign = true;

        await browser.GetAsync("/cookie-tokens");

        (await browser.MeAsync()).CacheKey.ShouldBe(before.CacheKey);
    }

    [Fact]
    public async Task SigningAnotherSchemeInWithTheCookiesProperties_StartsANewSessionId()
    {
        var signInOtherScheme = false;
        string? otherId = null;
        using var host = CookieSessionHost.Create(
            configureBefore: services =>
            {
                services.AddAuthentication().AddCookie("AppCookie", options =>
                    options.Events.OnSignedIn = context =>
                    {
                        otherId = context.Principal!.FindFirstValue(CookieSessionHost.SessionIdClaimType);
                        return Task.CompletedTask;
                    });
                services.Configure<CookieAuthenticationOptions>(
                    CookieAuthenticationDefaults.AuthenticationScheme,
                    options => options.Events.OnValidatePrincipal = async context =>
                    {
                        if (signInOtherScheme)
                        {
                            signInOtherScheme = false;
                            await context.HttpContext.SignInAsync(
                                "AppCookie",
                                new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "user-1")], "rebuilt")),
                                context.Properties);
                        }
                    });
            });
        var browser = host.NewBrowser();
        await browser.LoginAsync("user-1", "A", expiresIn: 3600);
        var id = (await browser.MeAsync()).SessionIds.ShouldHaveSingleItem();
        signInOtherScheme = true;

        await browser.GetAsync("/cookie-tokens");

        otherId.ShouldNotBeNull().ShouldNotBe(id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NewOpenIdConnectSignInStartedWithTheOldCookiesProperties_GetsANewSessionId(bool callbackReadsTheOldCookie)
    {
        using var provider = new FakeOpenIdProvider();
        using var host = CookieSessionHost.Create(
            configureBefore: services => provider.Register(services, async context =>
            {
                if (callbackReadsTheOldCookie)
                {
                    // The old cookie is read in the very request that completes the new sign-in.
                    (await context.HttpContext.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme))
                        .Succeeded.ShouldBe(context.Request.Cookies.ContainsKey(".AspNetCore.Cookies"));
                }
            }));
        var browser = host.NewBrowser();
        await FakeOpenIdProvider.SignInAsync(browser, "user-1", "A");
        var oldId = (await browser.MeAsync()).SessionIds.ShouldHaveSingleItem();
        var copiedOldCookie = browser.Copy();

        await FakeOpenIdProvider.SignInAsync(browser, "user-1", "B", challengePath: "/challenge-with-cookie-properties");

        // The premise: the old id really did travel through the OpenID Connect state.
        provider.ValidatedProperties[^1][RecordedIdItem].ShouldBe(oldId);
        var newView = await browser.MeAsync();
        newView.Token.ShouldBe("access-B");
        newView.SessionIds.ShouldHaveSingleItem().ShouldNotBe(oldId);
        (await copiedOldCookie.MeAsync()).CacheKey.ShouldNotBe(newView.CacheKey);
    }

    [Fact]
    public async Task AppReSigningAPreUpgradeCookie_GivesItASessionId()
    {
        // Documented in the README: unlike the package's own refresh re-sign, an app's SignInAsync does
        // assign an id to a cookie issued before the upgrade.
        using var host = CookieSessionHost.Create();
        var browser = host.NewBrowser();
        await browser.PreUpgradeLoginAsync("user-1", "A", expiresIn: 3600);
        (await browser.MeAsync()).CacheKey.ShouldBe("user:user-1");

        await browser.GetAsync("/resign-rebuilt");

        (await browser.MeAsync()).SessionIds.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task CircuitOpenAcrossAnAppReSignOfAPreUpgradeCookie_CanReplayARotatedRefreshToken()
    {
        // Pins the documented limitation, so that a change in it is noticed and the README updated.
        using var host = CookieSessionHost.Create();
        host.IdentityProvider.RotatesRefreshTokens = true;
        host.IdentityProvider.ExpiresInSeconds = 30;
        var browser = host.NewBrowser();
        await browser.PreUpgradeLoginAsync("user-1", "A", expiresIn: 30);
        var circuitPrincipal = await browser.PrincipalAsync();

        await browser.GetAsync("/resign");
        await CircuitTokenAsync(host, circuitPrincipal);
        await browser.MeAsync();

        host.IdentityProvider.Presented.GroupBy(token => token).ShouldContain(group => group.Count() > 1);
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
