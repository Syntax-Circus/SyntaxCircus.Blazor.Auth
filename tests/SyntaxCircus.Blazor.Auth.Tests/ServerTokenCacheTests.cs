using Microsoft.Extensions.Time.Testing;

namespace SyntaxCircus.Blazor.Auth.Tests;

public class ServerTokenCacheTests
{
    private readonly ServerTokenCache _cache = new();

    private static ServerTokenCacheEntry EntryExpiringAt(DateTimeOffset expiresAt)
        => new("access", "refresh", null, expiresAt);

    /// <summary>Runs <see cref="OnNextRead"/> the next time the clock is read, to interleave a write deterministically.</summary>
    private sealed class InterleavingClock : TimeProvider
    {
        public Action? OnNextRead { get; set; }

        public override DateTimeOffset GetUtcNow()
        {
            var action = OnNextRead;
            OnNextRead = null;
            action?.Invoke();
            return DateTimeOffset.UtcNow;
        }
    }

    [Fact]
    public async Task GetAsync_DroppingAnExpiredEntry_LeavesOneWrittenSinceItWasRead()
    {
        var clock = new InterleavingClock();
        var cache = new ServerTokenCache(clock);
        await cache.SetAsync(
            "user:1",
            new ServerTokenCacheEntry("short-lived", null, null, DateTimeOffset.UtcNow.AddMilliseconds(30)),
            TestContext.Current.CancellationToken);
        await Task.Delay(60, TestContext.Current.CancellationToken);
        var replacement = EntryExpiringAt(DateTimeOffset.UtcNow.AddHours(1));
        // GetAsync reads the clock between reading the expired entry and removing it.
        clock.OnNextRead = () => _ = cache.SetAsync("user:1", replacement, CancellationToken.None);

        (await cache.GetAsync("user:1", TestContext.Current.CancellationToken)).ShouldBeNull();

        (await cache.GetAsync("user:1", TestContext.Current.CancellationToken)).ShouldBe(replacement);
    }

    [Fact]
    public async Task SetAsync_EvictsEntriesThirtyDaysPastAccessTokenExpiry()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var cache = new ServerTokenCache(clock);
        await cache.SetAsync("stale", EntryExpiringAt(clock.GetUtcNow().AddHours(1)), TestContext.Current.CancellationToken);

        clock.Advance(TimeSpan.FromDays(30));
        await cache.SetAsync("other", EntryExpiringAt(clock.GetUtcNow().AddHours(1)), TestContext.Current.CancellationToken);
        (await cache.GetRefreshTokenAsync("stale", TestContext.Current.CancellationToken)).ShouldBe("refresh");

        clock.Advance(TimeSpan.FromHours(2));
        await cache.SetAsync("other", EntryExpiringAt(clock.GetUtcNow().AddHours(1)), TestContext.Current.CancellationToken);

        (await cache.GetRefreshTokenAsync("stale", TestContext.Current.CancellationToken)).ShouldBeNull();
        cache.EntryCount.ShouldBe(1);
    }

    [Fact]
    public async Task SetAsync_AlreadyLongExpiredEntry_IsKeptForThirtyDaysFromTheWrite()
    {
        // The same retention RedisServerTokenCache gives such a write.
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var cache = new ServerTokenCache(clock);
        await cache.SetAsync("old", EntryExpiringAt(clock.GetUtcNow().AddDays(-60)), TestContext.Current.CancellationToken);

        clock.Advance(TimeSpan.FromDays(29));
        await cache.SetAsync("other", EntryExpiringAt(clock.GetUtcNow().AddHours(1)), TestContext.Current.CancellationToken);
        (await cache.GetRefreshTokenAsync("old", TestContext.Current.CancellationToken)).ShouldBe("refresh");

        clock.Advance(TimeSpan.FromDays(2));
        await cache.SetAsync("other", EntryExpiringAt(clock.GetUtcNow().AddHours(1)), TestContext.Current.CancellationToken);
        (await cache.GetRefreshTokenAsync("old", TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task SetAsync_RewrittenEntry_RestartsItsRetention()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var cache = new ServerTokenCache(clock);
        await cache.SetAsync("active", EntryExpiringAt(clock.GetUtcNow().AddHours(1)), TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromDays(20));
        await cache.SetAsync("active", EntryExpiringAt(clock.GetUtcNow().AddHours(1)), TestContext.Current.CancellationToken);

        clock.Advance(TimeSpan.FromDays(20));
        await cache.SetAsync("other", EntryExpiringAt(clock.GetUtcNow().AddHours(1)), TestContext.Current.CancellationToken);

        (await cache.GetRefreshTokenAsync("active", TestContext.Current.CancellationToken)).ShouldBe("refresh");
    }

    [Fact]
    public async Task SetAsync_MaximumExpiry_DoesNotOverflow()
        => await Should.NotThrowAsync(() => _cache.SetAsync("user:1", EntryExpiringAt(DateTimeOffset.MaxValue), TestContext.Current.CancellationToken));

    [Fact]
    public async Task Eviction_SkipsAnEntryWhoseRefreshLockIsHeld()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var cache = new ServerTokenCache(clock);
        await cache.SetAsync("busy", EntryExpiringAt(clock.GetUtcNow().AddHours(1)), TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromDays(40));

        await cache.WithRefreshLockAsync(
            "busy",
            async ct =>
            {
                await cache.SetAsync("other", EntryExpiringAt(clock.GetUtcNow().AddHours(1)), ct);
                (await cache.GetRefreshTokenAsync("busy", ct)).ShouldBe("refresh");
                return 0;
            },
            TestContext.Current.CancellationToken);

        clock.Advance(TimeSpan.FromHours(2));
        await cache.SetAsync("other", EntryExpiringAt(clock.GetUtcNow().AddHours(1)), TestContext.Current.CancellationToken);
        (await cache.GetRefreshTokenAsync("busy", TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task WithRefreshLockAsync_ReleasesTheLockOnceNobodyUsesTheKey()
    {
        for (var i = 0; i < 50; i++)
        {
            await _cache.WithRefreshLockAsync($"user:{i}", _ => Task.FromResult(i), TestContext.Current.CancellationToken);
        }

        await Should.ThrowAsync<InvalidOperationException>(() => _cache.WithRefreshLockAsync<int>(
            "user:failing", _ => throw new InvalidOperationException(), TestContext.Current.CancellationToken));

        _cache.RefreshLockCount.ShouldBe(0);
    }

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
