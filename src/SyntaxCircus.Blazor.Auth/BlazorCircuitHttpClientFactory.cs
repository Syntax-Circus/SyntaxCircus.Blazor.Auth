using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Http;

namespace SyntaxCircus.Blazor.Auth;

internal sealed class BlazorCircuitHttpClientFactory(
    AuthenticationStateProvider authenticationStateProvider,
    IUserTokenCacheKeyProvider cacheKeyProvider,
    IHttpContextAccessor httpContextAccessor,
    IHttpMessageHandlerFactory handlerFactory,
    IOptionsMonitor<HttpClientFactoryOptions> options,
    ILogger<BlazorCircuitHttpClientFactory> logger) : IBlazorCircuitHttpClientFactory, IDisposable
{
    private readonly object gate = new();
    private readonly List<HttpClient> clients = [];
    private bool disposed;

    public HttpClient CreateClient(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        lock (gate) ObjectDisposedException.ThrowIf(disposed, this);
        var client = new HttpClient(new CircuitUserTokenContextHandler(authenticationStateProvider, cacheKeyProvider, httpContextAccessor, logger) { InnerHandler = handlerFactory.CreateHandler(name) }, disposeHandler: true);
        foreach (var action in options.Get(name).HttpClientActions) action(client);
        lock (gate)
        {
            if (disposed) { client.Dispose(); throw new ObjectDisposedException(nameof(BlazorCircuitHttpClientFactory)); }
            clients.Add(client);
        }
        return client;
    }

    public void Dispose()
    {
        HttpClient[] snapshot;
        lock (gate) { if (disposed) return; disposed = true; snapshot = [.. clients]; clients.Clear(); }
        foreach (var client in snapshot) client.Dispose();
    }
}

internal sealed class CircuitUserTokenContextHandler(AuthenticationStateProvider authenticationStateProvider, IUserTokenCacheKeyProvider cacheKeyProvider, IHttpContextAccessor httpContextAccessor, ILogger logger) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (httpContextAccessor.HttpContext is null)
        {
            try
            {
                var state = await authenticationStateProvider.GetAuthenticationStateAsync().ConfigureAwait(false);
                if (state.User.Identity?.IsAuthenticated == true)
                {
                    var key = cacheKeyProvider.GetCacheKey(state.User);
                    if (!string.IsNullOrWhiteSpace(key)) request.Options.Set(BlazorTokenRequestOptions.CacheKey, key.Trim());
                }
            }
            catch (InvalidOperationException ex) { logger.LogWarning(ex, "Failed to resolve authentication state from the calling circuit."); }
        }
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
