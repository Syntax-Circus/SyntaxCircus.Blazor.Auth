using System.Collections.Concurrent;

namespace SyntaxCircus.Blazor.Auth;

/// <summary>In-process singleton implementation of <see cref="IServerTokenCache"/>.</summary>
public sealed class ServerTokenCache : IServerTokenCache
{
    private readonly ConcurrentDictionary<string, ServerTokenCacheEntry> _entries = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _refreshLocks = new(StringComparer.Ordinal);

    public Task<ServerTokenCacheEntry?> GetAsync(string cacheKey, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = NormalizeKey(cacheKey);

        if (!_entries.TryGetValue(key, out var entry))
        {
            return Task.FromResult<ServerTokenCacheEntry?>(null);
        }

        if (entry.ExpiresAtUtc <= DateTimeOffset.UtcNow)
        {
            // Keep the entry alive if a refresh token is stored so GetRefreshTokenAsync can still
            // find it; drop it entirely once there's nothing left to renew from.
            if (string.IsNullOrWhiteSpace(entry.RefreshToken))
            {
                _entries.TryRemove(key, out _);
            }

            return Task.FromResult<ServerTokenCacheEntry?>(null);
        }

        return Task.FromResult<ServerTokenCacheEntry?>(entry);
    }

    public Task<string?> GetRefreshTokenAsync(string cacheKey, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = NormalizeKey(cacheKey);

        return Task.FromResult(_entries.TryGetValue(key, out var entry) ? entry.RefreshToken : null);
    }

    public Task SetAsync(string cacheKey, ServerTokenCacheEntry entry, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(entry);
        var key = NormalizeKey(cacheKey);

        if (entry.ExpiresAtUtc <= DateTimeOffset.UtcNow && string.IsNullOrWhiteSpace(entry.RefreshToken))
        {
            _entries.TryRemove(key, out _);
            return Task.CompletedTask;
        }

        _entries[key] = entry;
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string cacheKey, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _entries.TryRemove(NormalizeKey(cacheKey), out _);
        return Task.CompletedTask;
    }

    public async Task<T> WithRefreshLockAsync<T>(
        string cacheKey,
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        var refreshLock = _refreshLocks.GetOrAdd(NormalizeKey(cacheKey), _ => new SemaphoreSlim(1, 1));

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

    private static string NormalizeKey(string cacheKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheKey);
        return cacheKey.Trim();
    }
}
