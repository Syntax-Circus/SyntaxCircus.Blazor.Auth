using System.Collections.Concurrent;

namespace SyntaxCircus.Blazor.Auth;

/// <summary>
/// In-process singleton implementation of <see cref="IServerTokenCache"/>. An entry is retained until
/// 30 days after its access token expires, matching <see cref="RedisServerTokenCache"/>. Entries past
/// that are removed by a sweep that runs during a write, at most once an hour.
/// </summary>
public sealed class ServerTokenCache : IServerTokenCache
{
    private static readonly TimeSpan Retention = TimeSpan.FromDays(30);
    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<string, StoredEntry> _entries = new(StringComparer.Ordinal);
    private readonly KeyedRefreshLock _refreshLocks = new();
    private readonly TimeProvider _clock;
    private long _nextSweepUtcTicks;

    public ServerTokenCache()
        : this(TimeProvider.System)
    {
    }

    internal ServerTokenCache(TimeProvider clock)
    {
        _clock = clock;
        _nextSweepUtcTicks = (clock.GetUtcNow() + SweepInterval).UtcTicks;
    }

    internal int EntryCount => _entries.Count;

    internal int RefreshLockCount => _refreshLocks.Count;

    public Task<ServerTokenCacheEntry?> GetAsync(string cacheKey, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = NormalizeKey(cacheKey);

        if (!_entries.TryGetValue(key, out var stored))
        {
            return Task.FromResult<ServerTokenCacheEntry?>(null);
        }

        if (stored.Entry.ExpiresAtUtc <= _clock.GetUtcNow())
        {
            // Keep the entry alive if a refresh token is stored so GetRefreshTokenAsync can still
            // find it; drop it entirely once there's nothing left to renew from.
            if (string.IsNullOrWhiteSpace(stored.Entry.RefreshToken))
            {
                // Removes this entry only, not one written since it was read.
                _entries.TryRemove(KeyValuePair.Create(key, stored));
            }

            return Task.FromResult<ServerTokenCacheEntry?>(null);
        }

        return Task.FromResult<ServerTokenCacheEntry?>(stored.Entry);
    }

    public Task<string?> GetRefreshTokenAsync(string cacheKey, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = NormalizeKey(cacheKey);

        return Task.FromResult(_entries.TryGetValue(key, out var stored) ? stored.Entry.RefreshToken : null);
    }

    public Task SetAsync(string cacheKey, ServerTokenCacheEntry entry, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(entry);
        var key = NormalizeKey(cacheKey);
        var now = _clock.GetUtcNow();

        if (entry.ExpiresAtUtc <= now && string.IsNullOrWhiteSpace(entry.RefreshToken))
        {
            _entries.TryRemove(key, out _);
        }
        else
        {
            _entries[key] = new StoredEntry(entry, RetainUntil(entry.ExpiresAtUtc, now));
        }

        SweepIfDue(now);
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
        return await _refreshLocks.RunAsync(NormalizeKey(cacheKey), action, cancellationToken).ConfigureAwait(false);
    }

    private static DateTimeOffset RetainUntil(DateTimeOffset expiresAt, DateTimeOffset now)
    {
        if (expiresAt >= DateTimeOffset.MaxValue - Retention)
        {
            return DateTimeOffset.MaxValue;
        }

        var retainUntil = expiresAt + Retention;
        return retainUntil > now ? retainUntil : now + Retention;
    }

    private void SweepIfDue(DateTimeOffset now)
    {
        var due = Interlocked.Read(ref _nextSweepUtcTicks);
        if (now.UtcTicks < due
            || Interlocked.CompareExchange(ref _nextSweepUtcTicks, (now + SweepInterval).UtcTicks, due) != due)
        {
            return;
        }

        foreach (var pair in _entries)
        {
            if (pair.Value.RetainUntilUtc <= now)
            {
                // An entry whose refresh lock is in use is left for a later sweep, and one rewritten
                // since it was enumerated is not matched.
                _refreshLocks.RunIfUnused(pair.Key, () => _entries.TryRemove(pair));
            }
        }
    }

    private static string NormalizeKey(string cacheKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheKey);
        return cacheKey.Trim();
    }

    // A class, so TryRemove(pair) matches by reference.
    private sealed class StoredEntry(ServerTokenCacheEntry entry, DateTimeOffset retainUntilUtc)
    {
        public ServerTokenCacheEntry Entry { get; } = entry;

        public DateTimeOffset RetainUntilUtc { get; } = retainUntilUtc;
    }
}
