using System.Web;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace SyntaxCircus.Blazor.Auth.Tests;

public class OidcTokenRefreshServiceTests
{
    private static (OidcTokenRefreshService Service, StubHttpMessageHandler Handler) CreateService(
        Func<HttpRequestMessage, HttpResponseMessage> responder,
        OpenIdConnectOptions options)
    {
        var handler = new StubHttpMessageHandler(responder);
        var httpClient = new HttpClient(handler);
        var optionsMonitor = Substitute.For<IOptionsMonitor<OpenIdConnectOptions>>();
        optionsMonitor.Get(OpenIdConnectDefaults.AuthenticationScheme).Returns(options);

        var service = new OidcTokenRefreshService(httpClient, optionsMonitor, NullLogger<OidcTokenRefreshService>.Instance);
        return (service, handler);
    }

    private static Dictionary<string, string> ParseForm(string? body)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(body))
        {
            return result;
        }

        foreach (var pair in body.Split('&'))
        {
            var parts = pair.Split('=', 2);
            result[HttpUtility.UrlDecode(parts[0])] = parts.Length > 1 ? HttpUtility.UrlDecode(parts[1]) : string.Empty;
        }

        return result;
    }

    private static HttpResponseMessage JsonResponse(object payload)
        => new(HttpStatusCode.OK) { Content = JsonContent.Create(payload) };

    [Fact]
    public async Task RefreshAsync_TokenEndpointFromConfiguration_PostsToThatEndpoint()
    {
        var options = new OpenIdConnectOptions
        {
            ClientId = "client1",
            Configuration = new OpenIdConnectConfiguration { TokenEndpoint = "https://auth.example.com/token" },
        };
        var (service, handler) = CreateService(_ => JsonResponse(new { access_token = "new-access", expires_in = 3600 }), options);

        var result = await service.RefreshAsync("old-refresh", TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        handler.LastRequest!.RequestUri!.ToString().ShouldBe("https://auth.example.com/token");
    }

    [Fact]
    public async Task RefreshAsync_NoConfigurationAndNoConfigurationManager_ReturnsNullWithoutHttpCall()
    {
        var options = new OpenIdConnectOptions { ClientId = "client1" };
        var (service, handler) = CreateService(_ => JsonResponse(new { access_token = "should-not-be-used" }), options);

        var result = await service.RefreshAsync("old-refresh", TestContext.Current.CancellationToken);

        result.ShouldBeNull();
        handler.LastRequest.ShouldBeNull();
    }

    [Fact]
    public async Task RefreshAsync_ResolvesEndpointViaConfigurationManager_WhenConfigurationMissing()
    {
        var configurationManager = Substitute.For<IConfigurationManager<OpenIdConnectConfiguration>>();
        configurationManager.GetConfigurationAsync(Arg.Any<CancellationToken>())
            .Returns(new OpenIdConnectConfiguration { TokenEndpoint = "https://discovered.example.com/token" });
        var options = new OpenIdConnectOptions { ClientId = "client1", ConfigurationManager = configurationManager };
        var (service, handler) = CreateService(_ => JsonResponse(new { access_token = "new-access", expires_in = 3600 }), options);

        var result = await service.RefreshAsync("old-refresh", TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        handler.LastRequest!.RequestUri!.ToString().ShouldBe("https://discovered.example.com/token");
    }

    [Fact]
    public async Task RefreshAsync_SendsExpectedFormFields()
    {
        var options = new OpenIdConnectOptions
        {
            ClientId = "client1",
            ClientSecret = "secret1",
            Configuration = new OpenIdConnectConfiguration { TokenEndpoint = "https://auth.example.com/token" },
        };
        var (service, handler) = CreateService(_ => JsonResponse(new { access_token = "new-access", expires_in = 3600 }), options);

        await service.RefreshAsync("old-refresh", TestContext.Current.CancellationToken);

        var form = ParseForm(handler.LastRequest!.Body);
        form["grant_type"].ShouldBe("refresh_token");
        form["refresh_token"].ShouldBe("old-refresh");
        form["client_id"].ShouldBe("client1");
        form["client_secret"].ShouldBe("secret1");
    }

    [Fact]
    public async Task RefreshAsync_NoClientSecretConfigured_OmitsClientSecretField()
    {
        var options = new OpenIdConnectOptions
        {
            ClientId = "client1",
            Configuration = new OpenIdConnectConfiguration { TokenEndpoint = "https://auth.example.com/token" },
        };
        var (service, handler) = CreateService(_ => JsonResponse(new { access_token = "new-access", expires_in = 3600 }), options);

        await service.RefreshAsync("old-refresh", TestContext.Current.CancellationToken);

        var form = ParseForm(handler.LastRequest!.Body);
        form.ShouldNotContainKey("client_secret");
    }

    [Fact]
    public async Task RefreshAsync_NonSuccessResponse_ReturnsNull()
    {
        var options = new OpenIdConnectOptions { Configuration = new OpenIdConnectConfiguration { TokenEndpoint = "https://auth.example.com/token" } };
        var (service, _) = CreateService(_ => new HttpResponseMessage(HttpStatusCode.BadRequest), options);

        var result = await service.RefreshAsync("old-refresh", TestContext.Current.CancellationToken);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task RefreshAsync_ResponseMissingAccessToken_ReturnsNull()
    {
        var options = new OpenIdConnectOptions { Configuration = new OpenIdConnectConfiguration { TokenEndpoint = "https://auth.example.com/token" } };
        var (service, _) = CreateService(_ => JsonResponse(new { expires_in = 3600 }), options);

        var result = await service.RefreshAsync("old-refresh", TestContext.Current.CancellationToken);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task RefreshAsync_ResponseOmitsRefreshToken_FallsBackToOriginalRefreshToken()
    {
        var options = new OpenIdConnectOptions { Configuration = new OpenIdConnectConfiguration { TokenEndpoint = "https://auth.example.com/token" } };
        var (service, _) = CreateService(_ => JsonResponse(new { access_token = "new-access", expires_in = 3600 }), options);

        var result = await service.RefreshAsync("old-refresh", TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        result!.RefreshToken.ShouldBe("old-refresh");
    }

    [Fact]
    public async Task RefreshAsync_ResponseIncludesNewRefreshToken_UsesNewOne()
    {
        var options = new OpenIdConnectOptions { Configuration = new OpenIdConnectConfiguration { TokenEndpoint = "https://auth.example.com/token" } };
        var (service, _) = CreateService(_ => JsonResponse(new { access_token = "new-access", refresh_token = "new-refresh", expires_in = 3600 }), options);

        var result = await service.RefreshAsync("old-refresh", TestContext.Current.CancellationToken);

        result!.RefreshToken.ShouldBe("new-refresh");
    }

    [Fact]
    public async Task RefreshAsync_SuccessResponse_ResolvesExpiryFromExpiresIn()
    {
        var options = new OpenIdConnectOptions { Configuration = new OpenIdConnectConfiguration { TokenEndpoint = "https://auth.example.com/token" } };
        var (service, _) = CreateService(_ => JsonResponse(new { access_token = "new-access", expires_in = 3600 }), options);

        var before = DateTimeOffset.UtcNow;
        var result = await service.RefreshAsync("old-refresh", TestContext.Current.CancellationToken);

        result!.ExpiresAt.ShouldBeInRange(before.AddSeconds(3590), before.AddSeconds(3610));
    }
}
