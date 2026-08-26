using System.Collections.Concurrent;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Logging.Abstractions;

namespace SyntaxCircus.Blazor.Auth.Tests;

public sealed class BlazorCircuitHttpClientFactoryTests
{
    [Fact]
    public async Task ConcurrentRenderedCircuits_KeepEachUsersBearerOnItsOwnRequestUri()
    {
        var downstream = new BarrierRecordingHandler();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<MutableIdentitySlot>();
        services.AddBlazorTokenForwarding(BuildConfiguration([]));
        services.AddScoped<AuthenticationStateProvider>(sp => new SlotAuthenticationStateProvider(sp.GetRequiredService<MutableIdentitySlot>()));
        services.AddHttpClient("Concurrent", client => client.BaseAddress = new Uri("https://api.example.com/"))
            .AddHttpMessageHandler<ApiAuthHandler>()
            .ConfigurePrimaryHttpMessageHandler(() => downstream);
        await using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<IServerTokenCache>();
        await cache.SetAsync("user:one", new ServerTokenCacheEntry("token-one", "refresh", null, DateTimeOffset.UtcNow.AddHours(1)), TestContext.Current.CancellationToken);
        await cache.SetAsync("user:two", new ServerTokenCacheEntry("token-two", "refresh", null, DateTimeOffset.UtcNow.AddHours(1)), TestContext.Current.CancellationToken);
        using var oneScope = provider.CreateScope();
        using var twoScope = provider.CreateScope();
        oneScope.ServiceProvider.GetRequiredService<MutableIdentitySlot>().Principal = Authenticated("one");
        twoScope.ServiceProvider.GetRequiredService<MutableIdentitySlot>().Principal = Authenticated("two");
        var oneRenderer = new HtmlRenderer(oneScope.ServiceProvider, NullLoggerFactory.Instance);
        var twoRenderer = new HtmlRenderer(twoScope.ServiceProvider, NullLoggerFactory.Instance);

        var oneRender = oneRenderer.Dispatcher.InvokeAsync(() => oneRenderer.RenderComponentAsync<CircuitOneProbe>());
        var twoRender = twoRenderer.Dispatcher.InvokeAsync(() => twoRenderer.RenderComponentAsync<CircuitTwoProbe>());
        await downstream.BothArrived.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        downstream.Release.TrySetResult();
        await Task.WhenAll(oneRender, twoRender);
        await downstream.Completed.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        downstream.Bearers["one"].ShouldBe("Bearer token-one");
        downstream.Bearers["two"].ShouldBe("Bearer token-two");
        await oneRenderer.DisposeAsync();
        await twoRenderer.DisposeAsync();
    }
    [Fact]
    public void ScopedFactory_AppliesNamedConfigurationAndRejectsCreateAfterScopeDisposal()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<MutableIdentitySlot>();
        services.AddBlazorTokenForwarding(BuildConfiguration([]));
        services.AddScoped<AuthenticationStateProvider>(sp => new SlotAuthenticationStateProvider(sp.GetRequiredService<MutableIdentitySlot>()));
        services.AddHttpClient("Configured", client =>
        {
            client.BaseAddress = new Uri("https://configured.example.com/root/");
            client.Timeout = TimeSpan.FromSeconds(12);
            client.DefaultRequestHeaders.Add("X-Configured", "once");
        });
        using var provider = services.BuildServiceProvider();
        IBlazorCircuitHttpClientFactory factory;
        using (var scope = provider.CreateScope())
        {
            factory = scope.ServiceProvider.GetRequiredService<IBlazorCircuitHttpClientFactory>();
            using var client = factory.CreateClient("Configured");
            client.BaseAddress.ShouldBe(new Uri("https://configured.example.com/root/"));
            client.Timeout.ShouldBe(TimeSpan.FromSeconds(12));
            client.DefaultRequestHeaders.GetValues("X-Configured").Single().ShouldBe("once");
        }

        Should.Throw<ObjectDisposedException>(() => factory.CreateClient("Configured"));
    }

    [Fact]
    public async Task RenderedCircuit_NamedFactoryPipelineUsesRenderedUsersExactCacheKey()
    {
        var downstream = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var result = new CircuitRequestResult();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<MutableIdentitySlot>();
        services.AddBlazorTokenForwarding(BuildConfiguration([]));
        services.AddScoped<AuthenticationStateProvider>(sp => new SlotAuthenticationStateProvider(
            sp.GetRequiredService<MutableIdentitySlot>()));
        services.AddSingleton(result);
        services.AddHttpClient("RenderedApi", client =>
        {
            client.BaseAddress = new Uri("https://api.example.com/base/");
            client.DefaultRequestHeaders.Add("X-Named-Client", "preserved");
        })
            .AddHttpMessageHandler<ApiAuthHandler>()
            .ConfigurePrimaryHttpMessageHandler(() => downstream);

        await using var provider = services.BuildServiceProvider();
        var tokenCache = provider.GetRequiredService<IServerTokenCache>();
        await tokenCache.SetAsync(
            "user:rendered-user",
            new ServerTokenCacheEntry("rendered-access-token", "refresh", null, DateTimeOffset.UtcNow.AddHours(1)),
            TestContext.Current.CancellationToken);
        using var circuitScope = provider.CreateScope();
        circuitScope.ServiceProvider.GetRequiredService<MutableIdentitySlot>().Principal = Authenticated("rendered-user");
        var handlerScopeSlot = provider.GetRequiredService<IServiceScopeFactory>().CreateScope();
        handlerScopeSlot.ServiceProvider.GetRequiredService<MutableIdentitySlot>().Principal.Identity!.IsAuthenticated.ShouldBeFalse();
        handlerScopeSlot.Dispose();
        var renderer = new HtmlRenderer(circuitScope.ServiceProvider, NullLoggerFactory.Instance);

        await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<CircuitClientProbe>());
        await result.Completed.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        downstream.LastRequest!.HeaderValue("Authorization").ShouldBe("Bearer rendered-access-token");
        downstream.LastRequest.HeaderValue("X-Named-Client").ShouldBe("preserved");
        downstream.LastRequest.RequestUri.ShouldBe(new Uri("https://api.example.com/base/things"));
        await renderer.DisposeAsync();
    }

    private static IConfiguration BuildConfiguration(Dictionary<string, string?> values)
        => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static ClaimsPrincipal Authenticated(string subject)
        => new(new ClaimsIdentity([new Claim("sub", subject)], "test"));

    private sealed class MutableIdentitySlot
    {
        public ClaimsPrincipal Principal { get; set; } = new(new ClaimsIdentity());
    }

    private sealed class SlotAuthenticationStateProvider(MutableIdentitySlot slot) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync()
            => Task.FromResult(new AuthenticationState(slot.Principal));
    }

    private sealed class CircuitRequestResult
    {
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class CircuitClientProbe : ComponentBase
    {
        [Inject] private IBlazorCircuitHttpClientFactory ClientFactory { get; set; } = null!;
        [Inject] private CircuitRequestResult Result { get; set; } = null!;

        protected override async Task OnInitializedAsync()
        {
            using var client = ClientFactory.CreateClient("RenderedApi");
            await client.GetAsync("things", TestContext.Current.CancellationToken);
            Result.Completed.TrySetResult();
        }
    }

    private sealed class CircuitOneProbe : CircuitClientProbeBase { protected override string Path => "one"; }
    private sealed class CircuitTwoProbe : CircuitClientProbeBase { protected override string Path => "two"; }

    private abstract class CircuitClientProbeBase : ComponentBase
    {
        [Inject] private IBlazorCircuitHttpClientFactory ClientFactory { get; set; } = null!;
        protected abstract string Path { get; }
        protected override async Task OnInitializedAsync()
        {
            using var client = ClientFactory.CreateClient("Concurrent");
            await client.GetAsync(Path, TestContext.Current.CancellationToken);
        }
    }

    private sealed class BarrierRecordingHandler : HttpMessageHandler
    {
        private int arrived;
        private int completed;
        public TaskCompletionSource BothArrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentDictionary<string, string?> Bearers { get; } = new(StringComparer.Ordinal);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bearers[request.RequestUri!.AbsolutePath.Trim('/')] = request.Headers.Authorization?.ToString();
            if (Interlocked.Increment(ref arrived) == 2) BothArrived.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            if (Interlocked.Increment(ref completed) == 2) Completed.TrySetResult();
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
