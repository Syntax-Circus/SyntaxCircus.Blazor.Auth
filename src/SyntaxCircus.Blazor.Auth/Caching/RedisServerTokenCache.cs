using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;

namespace SyntaxCircus.Blazor.Auth;

/// <summary>
/// <see cref="IServerTokenCache"/> implementation backed by <see cref="IDistributedCache"/>, for
/// multi-instance deployments. Registered instead of <see cref="ServerTokenCache"/> when
/// <c>Authentication:Oidc:TokenCache:Redis:Enabled</c> is set — see
/// <see cref="BlazorTokenForwardingExtensions.AddBlazorTokenForwarding"/>. Refresh locks stay
/// in-process; a brief cross-instance race producing two refresh attempts is harmless since the
/// latest token simply wins. When <c>Authentication:Oidc:TokenCache:Redis:Protection:Enabled</c> is
/// set, <paramref name="dataProtectionProvider"/> is used to encrypt payloads at rest.
/// </summary>
public sealed class RedisServerTokenCache(
    IDistributedCache cache,
    IOptions<AuthOptions> options,
    IDataProtectionProvider? dataProtectionProvider = null) : IServerTokenCache
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _refreshLocks = new(StringComparer.Ordinal);

    private readonly Lazy<IDataProtector?> _protector = new(() =>
    {
        var protection = options.Value.TokenCache.Redis.Protection;
        if (!protection.Enabled)
        {
            return null;
        }

        if (dataProtectionProvider is null)
        {
            throw new InvalidOperationException(
                "Authentication:Oidc:TokenCache:Redis:Protection:Enabled is set but no IDataProtectionProvider " +
                "was supplied to RedisServerTokenCache. Register the cache via AddBlazorTokenForwarding, which " +
                "wires this up automatically, or pass a provider explicitly.");
        }

        return dataProtectionProvider.CreateProtector(protection.Purpose);
    });

    public async Task<ServerTokenCacheEntry?> GetAsync(string cacheKey, CancellationToken cancellationToken = default)
    {
        var entry = await ReadAsync(cacheKey, cancellationToken).ConfigureAwait(false);
        return entry is not null && entry.ExpiresAtUtc > DateTimeOffset.UtcNow ? entry : null;
    }

    public async Task<string?> GetRefreshTokenAsync(string cacheKey, CancellationToken cancellationToken = default)
    {
        var entry = await ReadAsync(cacheKey, cancellationToken).ConfigureAwait(false);
        return entry?.RefreshToken;
    }

    public async Task SetAsync(string cacheKey, ServerTokenCacheEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (entry.ExpiresAtUtc <= DateTimeOffset.UtcNow && string.IsNullOrWhiteSpace(entry.RefreshToken))
        {
            await cache.RemoveAsync(BuildKey(cacheKey), cancellationToken).ConfigureAwait(false);
            return;
        }

        var payload = JsonSerializer.Serialize(entry, JsonOptions);
        if (_protector.Value is { } protector)
        {
            payload = protector.Protect(payload);
        }

        var ttl = entry.ExpiresAtUtc - DateTimeOffset.UtcNow + TimeSpan.FromDays(30);
        if (ttl <= TimeSpan.Zero)
        {
            ttl = TimeSpan.FromDays(30);
        }

        await cache.SetStringAsync(
            BuildKey(cacheKey),
            payload,
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl },
            cancellationToken).ConfigureAwait(false);
    }

    public Task RemoveAsync(string cacheKey, CancellationToken cancellationToken = default)
        => cache.RemoveAsync(BuildKey(cacheKey), cancellationToken);

    public async Task<T> WithRefreshLockAsync<T>(
        string cacheKey,
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        var refreshLock = _refreshLocks.GetOrAdd(BuildKey(cacheKey), _ => new SemaphoreSlim(1, 1));

        await refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await action(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            refreshLock.Release();
        }
    }

    private async Task<ServerTokenCacheEntry?> ReadAsync(string cacheKey, CancellationToken cancellationToken)
    {
        var key = BuildKey(cacheKey);
        var payload = await cache.GetStringAsync(key, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        if (_protector.Value is { } protector)
        {
            try
            {
                payload = protector.Unprotect(payload);
            }
            catch (CryptographicException)
            {
                await cache.RemoveAsync(key, cancellationToken).ConfigureAwait(false);
                return null;
            }
        }

        try
        {
            return JsonSerializer.Deserialize<ServerTokenCacheEntry>(payload, JsonOptions);
        }
        catch (JsonException)
        {
            await cache.RemoveAsync(key, cancellationToken).ConfigureAwait(false);
            return null;
        }
    }

    private string BuildKey(string cacheKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheKey);
        return $"{options.Value.TokenCache.Redis.InstanceName}{cacheKey.Trim()}";
    }
}
