using System.Text;
using System.Text.Json;

namespace SyntaxCircus.Blazor.Auth.Tests;

public class RedisServerTokenCacheTests
{
    private const string InstanceName = "Test:OidcTokenCache:";

    private static (RedisServerTokenCache Cache, IDistributedCache DistributedCache) CreateCache()
    {
        var distributedCache = Substitute.For<IDistributedCache>();
        var options = Options.Create(new AuthOptions
        {
            TokenCache = new AuthOptions.TokenCacheOptions
            {
                Redis = new AuthOptions.RedisTokenCacheOptions { InstanceName = InstanceName },
            },
        });
        return (new RedisServerTokenCache(distributedCache, options), distributedCache);
    }

    private static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task GetAsync_NoStoredValue_ReturnsNull()
    {
        var (cache, distributedCache) = CreateCache();
        distributedCache.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((byte[]?)null);

        var result = await cache.GetAsync("user:1", TestContext.Current.CancellationToken);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task GetAsync_UnexpiredEntryStored_ReturnsEntry()
    {
        var (cache, distributedCache) = CreateCache();
        var entry = new ServerTokenCacheEntry("access", "refresh", "id", DateTimeOffset.UtcNow.AddMinutes(10));
        distributedCache.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entry, JsonOptions)));

        var result = await cache.GetAsync("user:1", TestContext.Current.CancellationToken);

        result.ShouldBe(entry);
    }

    [Fact]
    public async Task GetAsync_ExpiredEntryStored_ReturnsNull()
    {
        var (cache, distributedCache) = CreateCache();
        var entry = new ServerTokenCacheEntry("access", "refresh", "id", DateTimeOffset.UtcNow.AddMinutes(-10));
        distributedCache.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entry, JsonOptions)));

        var result = await cache.GetAsync("user:1", TestContext.Current.CancellationToken);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task GetAsync_MalformedJson_RemovesEntryAndReturnsNull()
    {
        var (cache, distributedCache) = CreateCache();
        distributedCache.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Encoding.UTF8.GetBytes("not json"));

        var result = await cache.GetAsync("user:1", TestContext.Current.CancellationToken);

        result.ShouldBeNull();
        await distributedCache.Received(1).RemoveAsync($"{InstanceName}user:1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetRefreshTokenAsync_ExpiredEntry_StillReturnsRefreshToken()
    {
        var (cache, distributedCache) = CreateCache();
        var entry = new ServerTokenCacheEntry("access", "refresh-token", "id", DateTimeOffset.UtcNow.AddMinutes(-10));
        distributedCache.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entry, JsonOptions)));

        var result = await cache.GetRefreshTokenAsync("user:1", TestContext.Current.CancellationToken);

        result.ShouldBe("refresh-token");
    }

    [Fact]
    public async Task SetAsync_NullEntry_ThrowsArgumentNullException()
    {
        var (cache, _) = CreateCache();

        await Should.ThrowAsync<ArgumentNullException>(() => cache.SetAsync("user:1", null!, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SetAsync_ExpiredWithNoRefreshToken_RemovesInsteadOfStoring()
    {
        var (cache, distributedCache) = CreateCache();
        var entry = new ServerTokenCacheEntry("access", null, null, DateTimeOffset.UtcNow.AddMinutes(-1));

        await cache.SetAsync("user:1", entry, TestContext.Current.CancellationToken);

        await distributedCache.Received(1).RemoveAsync($"{InstanceName}user:1", Arg.Any<CancellationToken>());
        await distributedCache.DidNotReceiveWithAnyArgs().SetAsync(default!, default!, default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task SetAsync_ValidEntry_StoresWithBuiltKey()
    {
        var (cache, distributedCache) = CreateCache();
        var entry = new ServerTokenCacheEntry("access", "refresh", "id", DateTimeOffset.UtcNow.AddMinutes(10));

        await cache.SetAsync("user:1", entry, TestContext.Current.CancellationToken);

        await distributedCache.Received(1).SetAsync(
            $"{InstanceName}user:1",
            Arg.Any<byte[]>(),
            Arg.Any<DistributedCacheEntryOptions>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetAsync_VeryStaleExpiredEntry_ClampsTtlToThirtyDays()
    {
        var (cache, distributedCache) = CreateCache();
        var entry = new ServerTokenCacheEntry("access", "refresh", "id", DateTimeOffset.UtcNow.AddDays(-60));
        DistributedCacheEntryOptions? capturedOptions = null;
        await distributedCache.SetAsync(
            Arg.Any<string>(),
            Arg.Any<byte[]>(),
            Arg.Do<DistributedCacheEntryOptions>(o => capturedOptions = o),
            Arg.Any<CancellationToken>());

        await cache.SetAsync("user:1", entry, TestContext.Current.CancellationToken);

        capturedOptions.ShouldNotBeNull();
        capturedOptions.AbsoluteExpirationRelativeToNow.ShouldBe(TimeSpan.FromDays(30));
    }

    [Fact]
    public async Task RemoveAsync_DelegatesToDistributedCacheWithBuiltKey()
    {
        var (cache, distributedCache) = CreateCache();

        await cache.RemoveAsync("user:1", TestContext.Current.CancellationToken);

        await distributedCache.Received(1).RemoveAsync($"{InstanceName}user:1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WithRefreshLockAsync_ReturnsActionResult()
    {
        var (cache, _) = CreateCache();

        var result = await cache.WithRefreshLockAsync("user:1", _ => Task.FromResult(42), TestContext.Current.CancellationToken);

        result.ShouldBe(42);
    }

    [Fact]
    public async Task WithRefreshLockAsync_NullAction_ThrowsArgumentNullException()
    {
        var (cache, _) = CreateCache();

        await Should.ThrowAsync<ArgumentNullException>(() =>
            cache.WithRefreshLockAsync<string>("user:1", null!, TestContext.Current.CancellationToken));
    }
}
