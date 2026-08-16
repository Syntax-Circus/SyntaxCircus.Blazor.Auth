namespace SyntaxCircus.Blazor.Auth.Tests.Infrastructure;

/// <summary>
/// Builds a <see cref="DefaultHttpContext"/> wired to a substituted <see cref="IAuthenticationService"/>
/// so <c>HttpContext.AuthenticateAsync</c>/<c>GetTokenAsync</c>/<c>SignInAsync</c> extension methods
/// (which all resolve <see cref="IAuthenticationService"/> from <c>RequestServices</c>) behave as if
/// backed by a real cookie-authentication handler, without spinning up a TestServer.
/// </summary>
internal static class FakeAuthenticationContext
{
    public static (DefaultHttpContext Context, IAuthenticationService AuthService) CreateUnauthenticated()
    {
        var authService = Substitute.For<IAuthenticationService>();
        var context = BuildContext(authService);
        context.User = new ClaimsPrincipal(new ClaimsIdentity());

        authService.AuthenticateAsync(context, CookieAuthenticationDefaults.AuthenticationScheme)
            .Returns(AuthenticateResult.NoResult());

        return (context, authService);
    }

    public static (DefaultHttpContext Context, IAuthenticationService AuthService) CreateAuthenticated(
        string subject,
        Dictionary<string, string>? tokens = null)
    {
        var authService = Substitute.For<IAuthenticationService>();
        var context = BuildContext(authService);

        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", subject)], "TestAuth"));
        context.User = principal;

        var properties = new AuthenticationProperties();
        if (tokens is { Count: > 0 })
        {
            properties.StoreTokens(tokens.Select(kv => new AuthenticationToken { Name = kv.Key, Value = kv.Value }));
        }

        var ticket = new AuthenticationTicket(principal, properties, CookieAuthenticationDefaults.AuthenticationScheme);
        authService.AuthenticateAsync(context, CookieAuthenticationDefaults.AuthenticationScheme)
            .Returns(AuthenticateResult.Success(ticket));

        return (context, authService);
    }

    private static DefaultHttpContext BuildContext(IAuthenticationService authService)
    {
        var services = new ServiceCollection();
        services.AddSingleton(authService);
        return new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
    }
}
