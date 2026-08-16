using System.Text;
using System.Text.Json;
using SyntaxCircus.Blazor.Auth.Diagnostics;

namespace SyntaxCircus.Blazor.Auth.Tests;

public class AuthDebugEndpointsTests
{
    private static string EncodeBase64Url(string json)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string BuildJwt(object payload)
        => $"{EncodeBase64Url("{\"alg\":\"none\"}")}.{EncodeBase64Url(JsonSerializer.Serialize(payload))}.signature";

    private static TestServer CreateServer() => TestServerFactory.Create(
        services =>
        {
            services.AddAuthorization();
            services
                .AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, configureOptions: null);
        },
        app =>
        {
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapAuthDebugEndpoints();
        });

    [Fact]
    public async Task Claims_Unauthenticated_ReturnsUnauthorized()
    {
        using var server = CreateServer();
        using var client = server.CreateClient();

        var response = await client.GetAsync(new Uri("/debug/claims", UriKind.Relative), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Claims_Authenticated_ReturnsPrincipalClaims()
    {
        using var server = CreateServer();
        using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Authenticated", "true");

        var response = await client.GetAsync(new Uri("/debug/claims", UriKind.Relative), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(body);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        document.RootElement.GetProperty("authenticated").GetBoolean().ShouldBeTrue();
        document.RootElement.GetProperty("name").GetString().ShouldBe("test-user");
        document.RootElement.GetProperty("claims").GetArrayLength().ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Token_Unauthenticated_ReturnsUnauthorized()
    {
        using var server = CreateServer();
        using var client = server.CreateClient();

        var response = await client.GetAsync(new Uri("/debug/token", UriKind.Relative), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Token_NoTokensStored_ReturnsHasAccessTokenFalse()
    {
        using var server = CreateServer();
        using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Authenticated", "true");

        var response = await client.GetAsync(new Uri("/debug/token?includeRaw=false", UriKind.Relative), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(body);

        document.RootElement.GetProperty("hasAccessToken").GetBoolean().ShouldBeFalse();
        document.RootElement.GetProperty("accessToken").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Token_AccessTokenIsJwt_DecodesClaimsWithoutRawValueByDefault()
    {
        var jwt = BuildJwt(new { iss = "https://issuer.example.com", sub = "user-1", exp = 9999999999L });
        using var server = CreateServer();
        using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Authenticated", "true");
        client.DefaultRequestHeaders.Add("X-Test-Token-access_token", jwt);

        var response = await client.GetAsync(new Uri("/debug/token?includeRaw=false", UriKind.Relative), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(body);
        var accessToken = document.RootElement.GetProperty("accessToken");

        document.RootElement.GetProperty("hasAccessToken").GetBoolean().ShouldBeTrue();
        accessToken.GetProperty("issuer").GetString().ShouldBe("https://issuer.example.com");
        accessToken.GetProperty("subject").GetString().ShouldBe("user-1");
        accessToken.GetProperty("raw").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Token_IncludeRawTrue_ReturnsFullTokenValue()
    {
        var jwt = BuildJwt(new { sub = "user-1" });
        using var server = CreateServer();
        using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Authenticated", "true");
        client.DefaultRequestHeaders.Add("X-Test-Token-access_token", jwt);

        var response = await client.GetAsync(new Uri("/debug/token?includeRaw=true", UriKind.Relative), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(body);

        document.RootElement.GetProperty("accessToken").GetProperty("raw").GetString().ShouldBe(jwt);
    }

    [Fact]
    public async Task Token_RefreshTokenIncludeRawFalse_ReturnsPreviewNotRawValue()
    {
        using var server = CreateServer();
        using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Authenticated", "true");
        client.DefaultRequestHeaders.Add("X-Test-Token-refresh_token", "a-very-long-refresh-token-value-1234567890");

        var response = await client.GetAsync(new Uri("/debug/token?includeRaw=false", UriKind.Relative), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(body);

        document.RootElement.GetProperty("hasRefreshToken").GetBoolean().ShouldBeTrue();
        document.RootElement.GetProperty("refreshToken").GetString().ShouldNotBe("a-very-long-refresh-token-value-1234567890");
    }

    [Fact]
    public async Task Token_ExpiresAtHeader_EchoedInResponse()
    {
        using var server = CreateServer();
        using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Authenticated", "true");
        client.DefaultRequestHeaders.Add("X-Test-Token-expires_at", "2026-03-15T12:30:00Z");

        var response = await client.GetAsync(new Uri("/debug/token?includeRaw=false", UriKind.Relative), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(body);

        document.RootElement.GetProperty("expiresAt").GetString().ShouldBe("2026-03-15T12:30:00Z");
    }

    [Fact]
    public void MapAuthDebugEndpoints_NullEndpoints_ThrowsArgumentNullException()
        => Should.Throw<ArgumentNullException>(() => AuthDebugEndpoints.MapAuthDebugEndpoints(null!));
}
