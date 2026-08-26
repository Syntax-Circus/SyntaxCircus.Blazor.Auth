namespace SyntaxCircus.Blazor.Auth;

/// <summary>Scoped signal telling the Blazor UI that the current session's tokens have expired.</summary>
public sealed class SessionStateService : IDisposable
{
    private readonly object _gate = new();
    private readonly ISessionExpirySubscriptionSource? expirySource;
    private IDisposable? subscription;
    private string? observedKey;
    private bool disposed;
    private bool _isSessionExpired;

    /// <summary>Creates an isolated session state signal.</summary>
    public SessionStateService()
    {
    }

    /// <summary>Creates a circuit-visible session state signal backed by an expiry broker.</summary>
    public SessionStateService(SessionExpiryBroker broker)
        : this((ISessionExpirySubscriptionSource)broker)
    {
    }

    internal SessionStateService(ISessionExpirySubscriptionSource expirySource)
    {
        ArgumentNullException.ThrowIfNull(expirySource);
        this.expirySource = expirySource;
    }

    public bool IsSessionExpired
    {
        get
        {
            lock (_gate)
            {
                return _isSessionExpired;
            }
        }
    }

    public event Action? OnSessionChanged;

    public void MarkExpired() => SetExpired(true);

    public void Clear() => SetExpired(false);

    /// <summary>Observes expiry events for the active user's cache key.</summary>
    public void Observe(string cacheKey)
    {
        if (expirySource is null || string.IsNullOrWhiteSpace(cacheKey))
        {
            return;
        }

        cacheKey = cacheKey.Trim();
        Action? changedHandlers = null;
        lock (_gate)
        {
            if (disposed || string.Equals(observedKey, cacheKey, StringComparison.Ordinal))
            {
                return;
            }

            var newSubscription = expirySource.Subscribe(cacheKey, () => MarkExpiredFor(cacheKey));
            subscription?.Dispose();
            subscription = newSubscription;
            observedKey = cacheKey;
            if (_isSessionExpired)
            {
                _isSessionExpired = false;
                changedHandlers = OnSessionChanged;
            }
        }

        changedHandlers?.Invoke();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            subscription?.Dispose();
            subscription = null;
            observedKey = null;
        }
    }

    private void MarkExpiredFor(string cacheKey)
    {
        Action? changedHandlers = null;
        lock (_gate)
        {
            if (disposed || !string.Equals(observedKey, cacheKey, StringComparison.Ordinal) || _isSessionExpired)
            {
                return;
            }

            _isSessionExpired = true;
            changedHandlers = OnSessionChanged;
        }

        changedHandlers?.Invoke();
    }

    private void SetExpired(bool isExpired)
    {
        Action? changedHandlers = null;
        lock (_gate)
        {
            if (disposed || _isSessionExpired == isExpired)
            {
                return;
            }

            _isSessionExpired = isExpired;
            changedHandlers = OnSessionChanged;
        }

        changedHandlers?.Invoke();
    }
}
