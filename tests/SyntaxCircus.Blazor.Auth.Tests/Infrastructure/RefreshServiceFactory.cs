using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace SyntaxCircus.Blazor.Auth.Tests.Infrastructure;

/// <summary>Builds a real <see cref="OidcTokenRefreshService"/> (sealed, no interface) against a stubbed HTTP boundary.</summary>
internal static class RefreshServiceFactory
{
    public static (OidcTokenRefreshService Service, StubHttpMessageHandler Handler) Create(
        Func<HttpRequestMessage, HttpResponseMessage> responder,
        string tokenEndpoint = "https://auth.example.com/token")
    {
        var handler = new StubHttpMessageHandler(responder);
        var httpClient = new HttpClient(handler);
        var options = new OpenIdConnectOptions
        {
            ClientId = "client1",
            Configuration = new OpenIdConnectConfiguration { TokenEndpoint = tokenEndpoint },
        };
        var optionsMonitor = Substitute.For<IOptionsMonitor<OpenIdConnectOptions>>();
        optionsMonitor.Get(OpenIdConnectDefaults.AuthenticationScheme).Returns(options);

        var service = new OidcTokenRefreshService(httpClient, optionsMonitor, NullLogger<OidcTokenRefreshService>.Instance);
        return (service, handler);
    }
}
