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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SigningAnotherSchemeInWithTheCookiesProperties_StartsANewSessionId(bool preUpgradeCookie)
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
        await (preUpgradeCookie
            ? browser.PreUpgradeLoginAsync("user-1", "A", expiresIn: 3600)
            : browser.LoginAsync("user-1", "A", expiresIn: 3600));
        var ids = (await browser.MeAsync()).SessionIds;
        ids.Length.ShouldBe(preUpgradeCookie ? 0 : 1);
        signInOtherScheme = true;

        await browser.GetAsync("/cookie-tokens");

        ids.ShouldNotContain(otherId.ShouldNotBeNull());
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

    [Theory]
    [InlineData("/resign")]
    [InlineData("/resign-rebuilt")]
    public async Task AppReSigningAPreUpgradeCookieWithItsOwnProperties_KeepsThePerUserKey(string reSign)
    {
        using var host = CookieSessionHost.Create();
        var browser = host.NewBrowser();
        await browser.PreUpgradeLoginAsync("user-1", "A", expiresIn: 3600);
        var cookie = browser.Cookies.Values.Single();

        await browser.GetAsync(reSign);

        browser.Cookies.Values.Single().ShouldNotBe(cookie);
        var view = await browser.MeAsync();
        view.CacheKey.ShouldBe("user:user-1");
        view.SessionIds.ShouldBeEmpty();
        host.Logs.LibraryWarnings.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("/resign")]
    [InlineData("/resign-rebuilt")]
    public async Task CircuitOpenAcrossAnAppReSignOfAPreUpgradeCookie_NeverMakesARotatedRefreshTokenBeReplayed(string reSign)
    {
        using var host = CookieSessionHost.Create();
        host.IdentityProvider.RotatesRefreshTokens = true;
        host.IdentityProvider.ExpiresInSeconds = 30; // every issued token is already due for refresh
        var browser = host.NewBrowser();
        await browser.PreUpgradeLoginAsync("user-1", "A", expiresIn: 30);
        var circuitPrincipal = await browser.PrincipalAsync();

        await browser.GetAsync(reSign);
        await CircuitTokenAsync(host, circuitPrincipal);
        var view = await browser.MeAsync();

        host.IdentityProvider.Presented.ShouldBeUnique();
        view.Token.ShouldNotBeNull();
    }

    [Theory]
    [InlineData("/resign-rebuilt?copyProperties=true")]
    [InlineData("/resign-rebuilt?cloneProperties=true")]
    [InlineData("/resign-rebuilt?sub=user-2")]
    public async Task ReSigningAPreUpgradeCookieAsAnythingButItsOwnContinuation_GetsASessionId(string reSign)
    {
        using var host = CookieSessionHost.Create();
        var browser = host.NewBrowser();
        await browser.PreUpgradeLoginAsync("user-1", "A", expiresIn: 3600);

        await browser.GetAsync(reSign);

        var view = await browser.MeAsync();
        view.SessionIds.ShouldHaveSingleItem();
        view.CacheKey.ShouldNotBeNull().ShouldStartWith("user-session:");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NewOpenIdConnectSignInOverAPreUpgradeCookie_GetsASessionId(bool callbackReadsTheOldCookie)
    {
        using var provider = new FakeOpenIdProvider();
        var callbackReadAnIdlessCookie = false;
        using var host = CookieSessionHost.Create(
            configureBefore: services => provider.Register(services, async context =>
            {
                if (callbackReadsTheOldCookie)
                {
                    // All the evidence of a continuation but one: the sign-in's items are not the ones read.
                    var old = await context.HttpContext.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                    callbackReadAnIdlessCookie = old.Succeeded
                        && old.Principal!.FindFirst(CookieSessionHost.SessionIdClaimType) is null
                        && old.Principal.FindFirstValue("sub") == context.Principal!.FindFirstValue("sub");
                }
            }));
        var browser = host.NewBrowser();
        await browser.PreUpgradeLoginAsync("user-1", "A", expiresIn: 3600);
        var copiedPreUpgradeCookie = browser.Copy();

        await FakeOpenIdProvider.SignInAsync(browser, "user-1", "B", challengePath: "/challenge-with-cookie-properties");

        callbackReadAnIdlessCookie.ShouldBe(callbackReadsTheOldCookie);
        var view = await browser.MeAsync();
        view.Token.ShouldBe("access-B");
        view.SessionIds.ShouldHaveSingleItem();
        view.CacheKey.ShouldNotBeNull().ShouldStartWith("user-session:");
        (await copiedPreUpgradeCookie.MeAsync()).CacheKey.ShouldBe("user:user-1");
    }

    public static TheoryData<string> PathsThatLeaveAPreUpgradeSessionWithoutAnId => ["package refresh", "same principal", "rebuilt principal"];

    [Theory]
    [MemberData(nameof(PathsThatLeaveAPreUpgradeSessionWithoutAnId))]
    public async Task ProviderSuppliedSessionIdClaim_IsRemovedFromASessionThatStaysWithoutAnId(string path)
    {
        // Well formed, so that only its issuer tells it from one of the package's.
        var forged = new string('a', 42) + "A";
        using var host = CookieSessionHost.Create();
        var browser = host.NewBrowser();
        if (path == "rebuilt principal")
        {
            await browser.PreUpgradeLoginAsync("user-1", "A", expiresIn: 3600);
            await browser.GetAsync($"/resign-rebuilt?forgedSessionId={forged}");
        }
        else
        {
            await browser.PreUpgradeLoginAsync("user-1", "A", expiresIn: path == "package refresh" ? 30 : 3600, forgedSessionId: forged);
            // For the package refresh this request is the one that re-signs the cookie.
            await browser.GetAsync(path == "same principal" ? "/resign" : "/me");
        }

        var view = await browser.MeAsync();

        view.SessionIds.ShouldBeEmpty();
        view.CacheKey.ShouldBe("user:user-1");
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
