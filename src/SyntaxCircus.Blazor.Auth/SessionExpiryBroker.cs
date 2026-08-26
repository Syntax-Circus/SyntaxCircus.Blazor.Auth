using Microsoft.Extensions.Logging.Abstractions;

namespace SyntaxCircus.Blazor.Auth;

/// <summary>Publishes user-scoped token-expiry events to active Blazor circuits.</summary>
public sealed class SessionExpiryBroker : ISessionExpirySubscriptionSource
{
    private readonly object gate = new();
    private readonly Dictionary<string, Dictionary<Guid, Action>> subscribers = new(StringComparer.Ordinal);
    private readonly ILogger<SessionExpiryBroker> logger;

    /// <summary>Creates a broker that suppresses callback failure logs.</summary>
    public SessionExpiryBroker()
        : this(NullLogger<SessionExpiryBroker>.Instance)
    {
    }

    /// <summary>Creates a broker that logs isolated subscriber callback failures.</summary>
    public SessionExpiryBroker(ILogger<SessionExpiryBroker> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        this.logger = logger;
    }

    /// <summary>Subscribes an active circuit to expiry events for one user cache key.</summary>
    public IDisposable Subscribe(string cacheKey, Action callback)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheKey);
        ArgumentNullException.ThrowIfNull(callback);

        cacheKey = cacheKey.Trim();
        var id = Guid.NewGuid();
        lock (gate)
        {
            if (!subscribers.TryGetValue(cacheKey, out var userSubscribers))
            {
                userSubscribers = [];
                subscribers.Add(cacheKey, userSubscribers);
            }

            userSubscribers.Add(id, callback);
        }

        return new Subscription(this, cacheKey, id);
    }

    /// <summary>Publishes expiry to active circuits for the specified user only.</summary>
    public void Publish(string cacheKey)
    {
        if (string.IsNullOrWhiteSpace(cacheKey))
        {
            return;
        }

        Action[] callbacks;
        cacheKey = cacheKey.Trim();
        lock (gate)
        {
            if (!subscribers.TryGetValue(cacheKey, out var userSubscribers))
            {
                return;
            }

            callbacks = [.. userSubscribers.Values];
        }

        foreach (var callback in callbacks)
        {
            try
            {
                callback();
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "A session-expiry subscriber failed for cache key {CacheKey}.", cacheKey);
            }
        }
    }

    private void Unsubscribe(string cacheKey, Guid id)
    {
        lock (gate)
        {
            if (!subscribers.TryGetValue(cacheKey, out var userSubscribers))
            {
                return;
            }

            userSubscribers.Remove(id);
            if (userSubscribers.Count == 0)
            {
                subscribers.Remove(cacheKey);
            }
        }
    }

    private sealed class Subscription(SessionExpiryBroker broker, string cacheKey, Guid id) : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                broker.Unsubscribe(cacheKey, id);
            }
        }
    }
}

internal interface ISessionExpirySubscriptionSource
{
    IDisposable Subscribe(string cacheKey, Action callback);
}
