namespace SyntaxCircus.Blazor.Auth.Tests;

public class ServerTokenCacheTests
{
    private readonly ServerTokenCache _cache = new();

    [Fact]
    public async Task GetAsync_NoEntry_ReturnsNull()
        => (await _cache.GetAsync("user:1", TestContext.Current.CancellationToken)).ShouldBeNull();

    [Fact]
    public async Task SetAsync_ThenGetAsync_RoundTrips()
    {
        var entry = new ServerTokenCacheEntry("access", "refresh", "id", DateTimeOffset.UtcNow.AddMinutes(10));

        await _cache.SetAsync("user:1", entry, TestContext.Current.CancellationToken);
        var result = await _cache.GetAsync("user:1", TestContext.Current.CancellationToken);

        result.ShouldBe(entry);
    }

    [Fact]
    public async Task SetAsync_NullEntry_ThrowsArgumentNullException()
        => await Should.ThrowAsync<ArgumentNullException>(() =>
            _cache.SetAsync("user:1", null!, TestContext.Current.CancellationToken));

    [Fact]
    public async Task SetAsync_ExpiredWithNoRefreshToken_DoesNotStore()
    {
        var entry = new ServerTokenCacheEntry("access", null, null, DateTimeOffset.UtcNow.AddMinutes(-1));

        await _cache.SetAsync("user:1", entry, TestContext.Current.CancellationToken);

        (await _cache.GetAsync("user:1", TestContext.Current.CancellationToken)).ShouldBeNull();
        (await _cache.GetRefreshTokenAsync("user:1", TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task SetAsync_ExpiredWithRefreshToken_StillStoresForRefreshTokenRetrieval()
    {
        var entry = new ServerTokenCacheEntry("access", "refresh-token", null, DateTimeOffset.UtcNow.AddMinutes(-1));

        await _cache.SetAsync("user:1", entry, TestContext.Current.CancellationToken);

        (await _cache.GetAsync("user:1", TestContext.Current.CancellationToken)).ShouldBeNull();
        (await _cache.GetRefreshTokenAsync("user:1", TestContext.Current.CancellationToken)).ShouldBe("refresh-token");
    }

    [Fact]
    public async Task GetAsync_EntryExpiresAfterBeingStoredValid_ReturnsNullButKeepsRefreshToken()
    {
        var entry = new ServerTokenCacheEntry("access", "refresh-token", null, DateTimeOffset.UtcNow.AddMilliseconds(50));
        await _cache.SetAsync("user:1", entry, TestContext.Current.CancellationToken);

        await Task.Delay(100, TestContext.Current.CancellationToken);

        (await _cache.GetAsync("user:1", TestContext.Current.CancellationToken)).ShouldBeNull();
        (await _cache.GetRefreshTokenAsync("user:1", TestContext.Current.CancellationToken)).ShouldBe("refresh-token");
    }

    [Fact]
    public async Task GetRefreshTokenAsync_NoEntry_ReturnsNull()
        => (await _cache.GetRefreshTokenAsync("user:1", TestContext.Current.CancellationToken)).ShouldBeNull();

    [Fact]
    public async Task RemoveAsync_RemovesStoredEntry()
    {
        var entry = new ServerTokenCacheEntry("access", "refresh", null, DateTimeOffset.UtcNow.AddMinutes(10));
        await _cache.SetAsync("user:1", entry, TestContext.Current.CancellationToken);

        await _cache.RemoveAsync("user:1", TestContext.Current.CancellationToken);

        (await _cache.GetAsync("user:1", TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task RemoveAsync_NonExistentEntry_DoesNotThrow()
        => await Should.NotThrowAsync(() => _cache.RemoveAsync("user:missing", TestContext.Current.CancellationToken));

    [Fact]
    public async Task CacheKey_IsTrimmedAndNormalized()
    {
        var entry = new ServerTokenCacheEntry("access", "refresh", null, DateTimeOffset.UtcNow.AddMinutes(10));

        await _cache.SetAsync("  user:1  ", entry, TestContext.Current.CancellationToken);
        var result = await _cache.GetAsync("user:1", TestContext.Current.CancellationToken);

        result.ShouldBe(entry);
    }

    [Fact]
    public async Task SetAsync_EmptyCacheKey_ThrowsArgumentException()
    {
        var entry = new ServerTokenCacheEntry("access", "refresh", null, DateTimeOffset.UtcNow.AddMinutes(10));

        await Should.ThrowAsync<ArgumentException>(() => _cache.SetAsync(string.Empty, entry, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetAsync_CancelledToken_ThrowsOperationCanceledException()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => _cache.GetAsync("user:1", cts.Token));
    }

    [Fact]
    public async Task WithRefreshLockAsync_NullAction_ThrowsArgumentNullException()
        => await Should.ThrowAsync<ArgumentNullException>(() =>
            _cache.WithRefreshLockAsync<string>("user:1", null!, TestContext.Current.CancellationToken));

    [Fact]
    public async Task WithRefreshLockAsync_ReturnsActionResult()
    {
        var result = await _cache.WithRefreshLockAsync("user:1", _ => Task.FromResult("done"), TestContext.Current.CancellationToken);

        result.ShouldBe("done");
    }

    [Fact]
    public async Task WithRefreshLockAsync_SameKey_SerializesConcurrentCalls()
    {
        var concurrentCount = 0;
        var maxConcurrent = 0;
        var gate = new object();

        async Task<int> Action(CancellationToken ct)
        {
            lock (gate)
            {
                concurrentCount++;
                maxConcurrent = Math.Max(maxConcurrent, concurrentCount);
            }

            await Task.Delay(50, ct).ConfigureAwait(false);

            lock (gate)
            {
                concurrentCount--;
            }

            return 1;
        }

        await Task.WhenAll(
            _cache.WithRefreshLockAsync("user:1", Action, TestContext.Current.CancellationToken),
            _cache.WithRefreshLockAsync("user:1", Action, TestContext.Current.CancellationToken),
            _cache.WithRefreshLockAsync("user:1", Action, TestContext.Current.CancellationToken));

        maxConcurrent.ShouldBe(1);
    }
}
