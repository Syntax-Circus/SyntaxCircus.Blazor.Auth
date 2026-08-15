namespace SyntaxCircus.Blazor.Auth;

/// <summary>Scoped signal telling the Blazor UI that the current session's tokens have expired.</summary>
public sealed class SessionStateService
{
    private readonly object _gate = new();
    private bool _isSessionExpired;

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

    private void SetExpired(bool isExpired)
    {
        bool changed;
        lock (_gate)
        {
            changed = _isSessionExpired != isExpired;
            _isSessionExpired = isExpired;
        }

        if (changed)
        {
            OnSessionChanged?.Invoke();
        }
    }
}
