namespace SyntaxCircus.Blazor.Auth;

/// <summary>
/// One in-process lock per cache key, kept only while somebody holds or awaits it. Cache keys are per
/// sign-in session, so a lock that lived for the life of the process would accumulate without bound.
/// </summary>
internal sealed class KeyedRefreshLock
{
    private readonly object gate = new();
    private readonly Dictionary<string, Slot> slots = new(StringComparer.Ordinal);

    internal int Count
    {
        get
        {
            lock (gate)
            {
                return slots.Count;
            }
        }
    }

    public async Task<T> RunAsync<T>(string key, Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        // Counted before waiting, so the slot cannot be dropped while anyone is queued on it.
        var slot = Rent(key);
        try
        {
            await slot.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await action(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                slot.Semaphore.Release();
            }
        }
        finally
        {
            Return(key, slot);
        }
    }

    /// <summary>
    /// Runs <paramref name="action"/> only if nobody holds or awaits the lock for <paramref name="key"/>,
    /// and keeps anyone from taking it until the action returns. The action must be short.
    /// </summary>
    public void RunIfUnused(string key, Action action)
    {
        lock (gate)
        {
            if (!slots.ContainsKey(key))
            {
                action();
            }
        }
    }

    private Slot Rent(string key)
    {
        lock (gate)
        {
            if (!slots.TryGetValue(key, out var slot))
            {
                slot = new Slot();
                slots.Add(key, slot);
            }

            slot.Users++;
            return slot;
        }
    }

    private void Return(string key, Slot slot)
    {
        lock (gate)
        {
            if (--slot.Users == 0)
            {
                slots.Remove(key);
                slot.Semaphore.Dispose();
            }
        }
    }

    private sealed class Slot
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        public int Users { get; set; }
    }
}
