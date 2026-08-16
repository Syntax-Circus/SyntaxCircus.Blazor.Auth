using System.Web;

namespace SyntaxCircus.Blazor.Auth.Tests;

public class ApiClientCredentialsTokenProviderTests
{
    private static (ApiClientCredentialsTokenProvider Provider, StubHttpMessageHandler Handler) CreateProvider(
        Func<HttpRequestMessage, HttpResponseMessage> responder,
        ApiClientCredentialsOptions options)
    {
        var handler = new StubHttpMessageHandler(responder);
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(ApiClientCredentialsTokenProvider.HttpClientName).Returns(new HttpClient(handler));

        var optionsMonitor = Substitute.For<IOptionsMonitor<ApiClientCredentialsOptions>>();
        optionsMonitor.CurrentValue.Returns(options);

        var provider = new ApiClientCredentialsTokenProvider(httpClientFactory, optionsMonitor);
        return (provider, handler);
    }

    private static ApiClientCredentialsOptions ConfiguredOptions() => new()
    {
        TokenEndpoint = "https://auth.example.com/token",
        ClientId = "client1",
        ClientSecret = "secret1",
    };

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
    public void IsConfigured_ReflectsOptionsIsConfigured()
    {
        var (provider, _) = CreateProvider(_ => JsonResponse(new { }), ConfiguredOptions());

        provider.IsConfigured.ShouldBeTrue();
    }

    [Fact]
    public void IsConfigured_NotConfigured_ReturnsFalse()
    {
        var (provider, _) = CreateProvider(_ => JsonResponse(new { }), new ApiClientCredentialsOptions());

        provider.IsConfigured.ShouldBeFalse();
    }

    [Fact]
    public async Task GetAccessTokenAsync_NotConfigured_ThrowsInvalidOperationException()
    {
        var (provider, handler) = CreateProvider(_ => JsonResponse(new { }), new ApiClientCredentialsOptions());

        await Should.ThrowAsync<InvalidOperationException>(() => provider.GetAccessTokenAsync(TestContext.Current.CancellationToken));

        handler.LastRequest.ShouldBeNull();
    }

    [Fact]
    public async Task GetAccessTokenAsync_Success_ReturnsAccessToken()
    {
        var (provider, _) = CreateProvider(_ => JsonResponse(new { access_token = "m2m-token", expires_in = 3600 }), ConfiguredOptions());

        var token = await provider.GetAccessTokenAsync(TestContext.Current.CancellationToken);

        token.ShouldBe("m2m-token");
    }

    [Fact]
    public async Task GetAccessTokenAsync_SendsExpectedGrantTypeAndCredentials()
    {
        var (provider, handler) = CreateProvider(_ => JsonResponse(new { access_token = "m2m-token", expires_in = 3600 }), ConfiguredOptions());

        await provider.GetAccessTokenAsync(TestContext.Current.CancellationToken);

        var form = ParseForm(handler.LastRequest!.Body);
        form["grant_type"].ShouldBe("client_credentials");
        form["client_id"].ShouldBe("client1");
        form["client_secret"].ShouldBe("secret1");
    }

    [Fact]
    public async Task GetAccessTokenAsync_AudienceAndScopeConfigured_IncludedInForm()
    {
        var options = ConfiguredOptions();
        options.Audience = "https://api.example.com";
        options.Scope = "read write";
        var (provider, handler) = CreateProvider(_ => JsonResponse(new { access_token = "m2m-token", expires_in = 3600 }), options);

        await provider.GetAccessTokenAsync(TestContext.Current.CancellationToken);

        var form = ParseForm(handler.LastRequest!.Body);
        form["audience"].ShouldBe("https://api.example.com");
        form["scope"].ShouldBe("read write");
    }

    [Fact]
    public async Task GetAccessTokenAsync_NoAudienceOrScope_OmittedFromForm()
    {
        var (provider, handler) = CreateProvider(_ => JsonResponse(new { access_token = "m2m-token", expires_in = 3600 }), ConfiguredOptions());

        await provider.GetAccessTokenAsync(TestContext.Current.CancellationToken);

        var form = ParseForm(handler.LastRequest!.Body);
        form.ShouldNotContainKey("audience");
        form.ShouldNotContainKey("scope");
    }

    [Fact]
    public async Task GetAccessTokenAsync_CachesTokenAcrossCalls()
    {
        var (provider, handler) = CreateProvider(_ => JsonResponse(new { access_token = "m2m-token", expires_in = 3600 }), ConfiguredOptions());

        await provider.GetAccessTokenAsync(TestContext.Current.CancellationToken);
        await provider.GetAccessTokenAsync(TestContext.Current.CancellationToken);

        handler.CallCount.ShouldBe(1);
    }

    [Fact]
    public async Task GetAccessTokenAsync_TokenNearExpiry_Refreshes()
    {
        var (provider, handler) = CreateProvider(_ => JsonResponse(new { access_token = "m2m-token", expires_in = 90 }), ConfiguredOptions());

        await provider.GetAccessTokenAsync(TestContext.Current.CancellationToken);
        await provider.GetAccessTokenAsync(TestContext.Current.CancellationToken);

        handler.CallCount.ShouldBe(2);
    }

    [Fact]
    public async Task GetAccessTokenAsync_ResponseMissingAccessToken_ThrowsInvalidOperationException()
    {
        var (provider, _) = CreateProvider(_ => JsonResponse(new { expires_in = 3600 }), ConfiguredOptions());

        await Should.ThrowAsync<InvalidOperationException>(() => provider.GetAccessTokenAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetAccessTokenAsync_NonSuccessResponse_ThrowsHttpRequestException()
    {
        var (provider, _) = CreateProvider(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized), ConfiguredOptions());

        await Should.ThrowAsync<HttpRequestException>(() => provider.GetAccessTokenAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetAccessTokenAsync_ConcurrentCallsWhileRefreshing_OnlyOneHttpCallMade()
    {
        var callCount = 0;
        var (provider, _) = CreateProvider(_ =>
        {
            Interlocked.Increment(ref callCount);
            Thread.Sleep(50);
            return JsonResponse(new { access_token = "m2m-token", expires_in = 3600 });
        }, ConfiguredOptions());

        await Task.WhenAll(
            provider.GetAccessTokenAsync(TestContext.Current.CancellationToken),
            provider.GetAccessTokenAsync(TestContext.Current.CancellationToken),
            provider.GetAccessTokenAsync(TestContext.Current.CancellationToken));

        callCount.ShouldBe(1);
    }

    [Fact]
    public void Dispose_DoesNotThrow()
    {
        var (provider, _) = CreateProvider(_ => JsonResponse(new { }), ConfiguredOptions());

        Should.NotThrow(provider.Dispose);
    }
}
