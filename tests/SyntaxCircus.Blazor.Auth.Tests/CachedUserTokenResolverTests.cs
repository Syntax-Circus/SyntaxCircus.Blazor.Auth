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

    private static CachedUserTokenResolver CreateResolver(ServerTokenCache cache, TimeProvider? clock = null, int skewSeconds = 60)
        => new(cache, Options.Create(new AuthOptions { TokenCache = { RefreshSkewSeconds = skewSeconds } }), clock);

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
}
