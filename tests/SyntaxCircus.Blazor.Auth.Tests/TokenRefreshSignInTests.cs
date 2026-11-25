namespace SyntaxCircus.Blazor.Auth.Tests;

public class TokenRefreshSignInTests
{
    [Fact]
    public async Task AnotherSignInMadeDuringTheRefreshReSign_IsStillTreatedAsASignIn()
    {
        var refreshing = false;
        ClaimsPrincipal? otherPrincipal = null;
        using var host = CookieSessionHost.Create(
            configureBefore: services =>
            {
                services.AddAuthentication().AddCookie("AppCookie", options =>
                    options.Events.OnSignedIn = context =>
                    {
                        otherPrincipal = context.Principal;
                        return Task.CompletedTask;
                    });
                services.Configure<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme, options =>
                    options.Events.OnSigningIn = async context =>
                    {
                        // The host signs a second cookie in while the package re-signs the first one.
                        if (refreshing)
                        {
                            refreshing = false;
                            await context.HttpContext.SignInAsync(
                                "AppCookie",
                                new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "user-1")], "other")),
                                new AuthenticationProperties());
                        }
                    });
            });
        var browser = host.NewBrowser();
        await browser.LoginAsync("user-1", "A", expiresIn: 30);
        refreshing = true;

        await browser.MeAsync();

        otherPrincipal.ShouldNotBeNull().FindAll(CookieSessionHost.SessionIdClaimType).ShouldHaveSingleItem();
        host.Logs.LibraryWarnings.ShouldBeEmpty();
    }
}
