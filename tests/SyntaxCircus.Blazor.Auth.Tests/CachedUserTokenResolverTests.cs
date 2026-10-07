using Microsoft.Extensions.Time.Testing;

namespace SyntaxCircus.Blazor.Auth.Tests;

public class CachedUserTokenResolverTests
{
    private const string Key = "user:user-1";

    private static HttpResponseMessage Refreshed(string accessToken = "refreshed-access", string refreshToken = "refresh-next")
        => new(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { access_token = accessToken, refresh_token = refreshToken, expires_in = 3600 }),
        };

    private static CachedUserTokenResolver CreateResolver(ServerTokenCache cache, TimeProvider? clock = null, int skewSeconds = 60, ILogger<CachedUserTokenResolver>? logger = null)
        => new(cache, Options.Create(new AuthOptions { TokenCache = { RefreshSkewSeconds = skewSeconds } }), clock, logger);

    private sealed class CapturingLogger : ILogger<CachedUserTokenResolver>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }

    private static Task Seed(ServerTokenCache cache, string access, string? refresh, DateTimeOffset expiresAt)
        => cache.SetAsync(Key, new ServerTokenCacheEntry(access, refresh, null, expiresAt), TestContext.Current.CancellationToken);

    [Fact]
    public async Task ResolveAsync_CachedTokenOutsideSkewWindow_ReturnsItWithoutRefreshing()
    {
        var cache = new ServerTokenCache();
        await Seed(cache, "cached-access", "refresh-1", DateTimeOffset.UtcNow.AddHours(1));
        var (refresh, handler) = RefreshServiceFactory.Create(_ => Refreshed());

        var token = await CreateResolver(cache).ResolveAsync(Key, refresh, _ => { }, TestContext.Current.CancellationToken);

        token.ShouldBe("cached-access");
        handler.CallCount.ShouldBe(0);
    }

    [Fact]
    public async Task ResolveAsync_ClockAdvancesIntoSkewWindow_RefreshesThroughTimeProvider()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var cache = new ServerTokenCache();
        await Seed(cache, "cached-access", "refresh-1", clock.GetUtcNow().AddMinutes(10));
        var (refresh, handler) = RefreshServiceFactory.Create(_ => Refreshed());
        var resolver = CreateResolver(cache, clock);

        (await resolver.ResolveAsync(Key, refresh, _ => { }, TestContext.Current.CancellationToken)).ShouldBe("cached-access");
        handler.CallCount.ShouldBe(0);

        // The cache's own expiry check uses the wall clock and still sees a live entry, so only the
        // skew check on the injected clock can trigger this refresh.
        clock.Advance(TimeSpan.FromMinutes(9) + TimeSpan.FromSeconds(30));
        (await resolver.ResolveAsync(Key, refresh, _ => { }, TestContext.Current.CancellationToken)).ShouldBe("refreshed-access");

        handler.CallCount.ShouldBe(1);
    }

    [Fact]
    public async Task ResolveAsync_ZeroSkewConfigured_ForwardsTokenUntilItActuallyExpires()
    {
        var cache = new ServerTokenCache();
        await Seed(cache, "cached-access", "refresh-1", DateTimeOffset.UtcNow.AddSeconds(30));
        var (refresh, handler) = RefreshServiceFactory.Create(_ => Refreshed());

        var token = await CreateResolver(cache, skewSeconds: 0).ResolveAsync(Key, refresh, _ => { }, TestContext.Current.CancellationToken);

        token.ShouldBe("cached-access");
        handler.CallCount.ShouldBe(0);
    }

    [Fact]
    public async Task ResolveAsync_InsideSkewWindowWithoutRefreshToken_ReturnsStillValidTokenWithoutPublishing()
    {
        var cache = new ServerTokenCache();
        await Seed(cache, "near-expiry-access", null, DateTimeOffset.UtcNow.AddSeconds(30));
        var (refresh, handler) = RefreshServiceFactory.Create(_ => Refreshed());
        var published = new List<string>();

        var token = await CreateResolver(cache).ResolveAsync(Key, refresh, published.Add, TestContext.Current.CancellationToken);

        token.ShouldBe("near-expiry-access");
        handler.CallCount.ShouldBe(0);
        published.ShouldBeEmpty();
    }

    [Fact]
    public async Task ResolveAsync_RefreshFailsInsideSkewWindow_ReturnsStillValidTokenWithoutPublishing()
    {
        var cache = new ServerTokenCache();
        await Seed(cache, "near-expiry-access", "refresh-1", DateTimeOffset.UtcNow.AddSeconds(30));
        var (refresh, handler) = RefreshServiceFactory.Create(_ => new HttpResponseMessage(HttpStatusCode.BadRequest));
        var published = new List<string>();

        var token = await CreateResolver(cache).ResolveAsync(Key, refresh, published.Add, TestContext.Current.CancellationToken);

        token.ShouldBe("near-expiry-access");
        handler.CallCount.ShouldBe(1);
        published.ShouldBeEmpty();
    }

    [Fact]
    public async Task ResolveAsync_ExpiredTokenRefreshFails_ReturnsNullAndPublishesExpiryForKey()
    {
        var cache = new ServerTokenCache();
        await Seed(cache, "expired-access", "refresh-1", DateTimeOffset.UtcNow.AddMinutes(-10));
        var (refresh, _) = RefreshServiceFactory.Create(_ => new HttpResponseMessage(HttpStatusCode.BadRequest));
        var published = new List<string>();

        var token = await CreateResolver(cache).ResolveAsync(Key, refresh, published.Add, TestContext.Current.CancellationToken);

        token.ShouldBeNull();
        published.ShouldBe([Key]);
    }

    [Fact]
    public async Task ResolveAsync_NothingCached_ReturnsNullWithoutRefreshingOrPublishing()
    {
        var (refresh, handler) = RefreshServiceFactory.Create(_ => Refreshed());
        var published = new List<string>();

        var token = await CreateResolver(new ServerTokenCache()).ResolveAsync(Key, refresh, published.Add, TestContext.Current.CancellationToken);

        token.ShouldBeNull();
        handler.CallCount.ShouldBe(0);
        published.ShouldBeEmpty();
    }

    [Fact]
    public async Task ResolveAsync_ConcurrentCallersWithExpiredToken_MakeExactlyOneRefreshCall()
    {
        var cache = new ServerTokenCache();
        await Seed(cache, "expired-access", "refresh-1", DateTimeOffset.UtcNow.AddMinutes(-10));
        var (refresh, handler) = RefreshServiceFactory.Create(_ => Refreshed());
        var resolver = CreateResolver(cache);

        var tokens = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ =>
            Task.Run(() => resolver.ResolveAsync(Key, refresh, __ => { }, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken)));

        tokens.ShouldAllBe(t => t == "refreshed-access");
        handler.CallCount.ShouldBe(1);
    }

    [Fact]
    public async Task ResolveAsync_RefreshThrowsHttpRequestExceptionInsideSkewWindow_ReturnsStillValidTokenWithoutPublishing()
    {
        var cache = new ServerTokenCache();
        await Seed(cache, "near-expiry-access", "refresh-1", DateTimeOffset.UtcNow.AddSeconds(30));
        var (refresh, handler) = RefreshServiceFactory.Create(_ => throw new HttpRequestException("idp unreachable"));
        var published = new List<string>();

        var token = await CreateResolver(cache).ResolveAsync(Key, refresh, published.Add, TestContext.Current.CancellationToken);

        token.ShouldBe("near-expiry-access");
        handler.CallCount.ShouldBe(1);
        published.ShouldBeEmpty();
    }

    [Fact]
    public async Task ResolveAsync_RefreshTimesOutInsideSkewWindow_ReturnsStillValidTokenWithoutPublishing()
    {
        var cache = new ServerTokenCache();
        await Seed(cache, "near-expiry-access", "refresh-1", DateTimeOffset.UtcNow.AddSeconds(30));
        var (refresh, _) = RefreshServiceFactory.Create(_ => throw new TaskCanceledException("timeout"));
        var published = new List<string>();

        var token = await CreateResolver(cache).ResolveAsync(Key, refresh, published.Add, TestContext.Current.CancellationToken);

        token.ShouldBe("near-expiry-access");
        published.ShouldBeEmpty();
    }

    [Fact]
    public async Task ResolveAsync_RefreshFailsTransientlyAfterTokenExpiredDuringIt_Throws()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var cache = new ServerTokenCache();
        await Seed(cache, "near-expiry-access", "refresh-1", clock.GetUtcNow().AddSeconds(30));
        var (refresh, _) = RefreshServiceFactory.Create(_ =>
        {
            clock.Advance(TimeSpan.FromSeconds(100));
            throw new HttpRequestException("idp slow then down");
        });
        var published = new List<string>();

        await Should.ThrowAsync<HttpRequestException>(
            () => CreateResolver(cache, clock).ResolveAsync(Key, refresh, published.Add, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ResolveAsync_RefreshRejectedAfterTokenExpiredDuringIt_ReturnsNullAndPublishesExpiry()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var cache = new ServerTokenCache();
        await Seed(cache, "near-expiry-access", "refresh-1", clock.GetUtcNow().AddSeconds(30));
        var (refresh, _) = RefreshServiceFactory.Create(_ =>
        {
            clock.Advance(TimeSpan.FromSeconds(100));
            return new HttpResponseMessage(HttpStatusCode.BadRequest);
        });
        var published = new List<string>();

        var token = await CreateResolver(cache, clock).ResolveAsync(Key, refresh, published.Add, TestContext.Current.CancellationToken);

        token.ShouldBeNull();
        published.ShouldBe([Key]);
    }

    [Fact]
    public async Task ResolveAsync_TransientFailureInsideSkewWindow_LogsWarningWithoutTheToken()
    {
        var cache = new ServerTokenCache();
        await Seed(cache, "near-expiry-secret-access", "refresh-secret-1", DateTimeOffset.UtcNow.AddSeconds(30));
        var (refresh, _) = RefreshServiceFactory.Create(_ => throw new HttpRequestException("idp down"));
        var log = new CapturingLogger();

        await CreateResolver(cache, logger: log).ResolveAsync(Key, refresh, _ => { }, TestContext.Current.CancellationToken);

        var warning = log.Entries.ShouldHaveSingleItem();
        warning.Level.ShouldBe(LogLevel.Warning);
        warning.Message.ShouldNotContain("secret");
    }

    [Fact]
    public async Task ResolveAsync_CallerCancelsDuringRefreshInsideSkewWindow_Rethrows()
    {
        var cache = new ServerTokenCache();
        await Seed(cache, "near-expiry-access", "refresh-1", DateTimeOffset.UtcNow.AddSeconds(30));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var (refresh, _) = RefreshServiceFactory.Create(_ =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });

        await Should.ThrowAsync<OperationCanceledException>(
            () => CreateResolver(cache).ResolveAsync(Key, refresh, _ => { }, cts.Token));
    }

    [Fact]
    public async Task ResolveAsync_RefreshThrowsForReallyExpiredToken_Rethrows()
    {
        var cache = new ServerTokenCache();
        await Seed(cache, "expired-access", "refresh-1", DateTimeOffset.UtcNow.AddMinutes(-10));
        var (refresh, _) = RefreshServiceFactory.Create(_ => throw new HttpRequestException("idp unreachable"));

        await Should.ThrowAsync<HttpRequestException>(
            () => CreateResolver(cache).ResolveAsync(Key, refresh, _ => { }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ResolveAsync_CacheReturnsEntryPastExpiryAndRefreshFails_ReturnsNullAndPublishesExpiry()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var cache = new NonFilteringTokenCache(new ServerTokenCacheEntry("stale-access", "refresh-1", null, clock.GetUtcNow().AddMinutes(1)));
        clock.Advance(TimeSpan.FromMinutes(2));
        var (refresh, _) = RefreshServiceFactory.Create(_ => new HttpResponseMessage(HttpStatusCode.BadRequest));
        var published = new List<string>();
        var resolver = new CachedUserTokenResolver(cache, Options.Create(new AuthOptions()), clock);

        var token = await resolver.ResolveAsync(Key, refresh, published.Add, TestContext.Current.CancellationToken);

        token.ShouldBeNull();
        published.ShouldBe([Key]);
    }

    /// <summary>A cache that, unlike the in-process one, hands back entries whatever their expiry.</summary>
    private sealed class NonFilteringTokenCache(ServerTokenCacheEntry entry) : IServerTokenCache
    {
        public Task<ServerTokenCacheEntry?> GetAsync(string cacheKey, CancellationToken cancellationToken = default) => Task.FromResult<ServerTokenCacheEntry?>(entry);

        public Task<string?> GetRefreshTokenAsync(string cacheKey, CancellationToken cancellationToken = default) => Task.FromResult(entry.RefreshToken);

        public Task SetAsync(string cacheKey, ServerTokenCacheEntry value, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RemoveAsync(string cacheKey, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<T> WithRefreshLockAsync<T>(string cacheKey, Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken = default) => action(cancellationToken);
    }
}
