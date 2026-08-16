using System.Text.Encodings.Web;

namespace SyntaxCircus.Blazor.Auth.Tests.Infrastructure;

/// <summary>
/// Minimal authentication handler for TestServer-backed tests: authenticates when the
/// <c>X-Test-Authenticated</c> header is <c>true</c>, carrying any tokens supplied via
/// <c>X-Test-Token-{name}</c> request headers so <c>HttpContext.GetTokenAsync</c> has something to
/// read. Header-driven (not ambient state) so it's safe regardless of how TestServer's in-memory
/// transport schedules the request.
/// </summary>
internal sealed class TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";
    private const string TokenHeaderPrefix = "X-Test-Token-";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (Request.Headers["X-Test-Authenticated"] != "true")
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "test-user"), new Claim("sub", "user-1")], SchemeName);
        var principal = new ClaimsPrincipal(identity);

        var properties = new AuthenticationProperties();
        var tokens = Request.Headers
            .Where(h => h.Key.StartsWith(TokenHeaderPrefix, StringComparison.Ordinal))
            .Select(h => new AuthenticationToken { Name = h.Key[TokenHeaderPrefix.Length..], Value = h.Value.ToString() })
            .ToList();
        if (tokens.Count > 0)
        {
            properties.StoreTokens(tokens);
        }

        var ticket = new AuthenticationTicket(principal, properties, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
