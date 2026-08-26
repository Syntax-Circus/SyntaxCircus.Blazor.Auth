namespace SyntaxCircus.Blazor.Auth.Tests;

public class SessionStateServiceTests
{
    [Fact]
    public void Observe_SameNormalizedKey_IsIdempotent()
    {
        var source = new ControllableExpirySource();
        using var service = new SessionStateService(source);

        service.Observe(" user:one ");
        service.Observe("user:one");

        source.SubscribeCalls.ShouldBe(1);
    }

    [Fact]
    public void Observe_KeySwitch_ClearsStateAndRejectsLateOldKeyCallback()
    {
        var source = new ControllableExpirySource();
        using var service = new SessionStateService(source);
        service.Observe("user:old");
        source.Publish("user:old");
        service.IsSessionExpired.ShouldBeTrue();
        var lateOldCallback = source.Snapshot("user:old");

        service.Observe("user:new");
        service.IsSessionExpired.ShouldBeFalse();
        lateOldCallback();

        service.IsSessionExpired.ShouldBeFalse();
        source.Publish("user:new");
        service.IsSessionExpired.ShouldBeTrue();
    }

    [Fact]
    public void Dispose_PreventsSnapshottedLateCallbackFromChangingState()
    {
        var source = new ControllableExpirySource();
        var service = new SessionStateService(source);
        var changes = 0;
        service.OnSessionChanged += () => changes++;
        service.Observe("user:one");
        var lateCallback = source.Snapshot("user:one");

        service.Dispose();
        lateCallback();

        service.IsSessionExpired.ShouldBeFalse();
        changes.ShouldBe(0);
    }

    [Fact]
    public void IsSessionExpired_InitiallyFalse()
        => new SessionStateService().IsSessionExpired.ShouldBeFalse();

    [Fact]
    public void MarkExpired_SetsIsSessionExpiredTrue()
    {
        var service = new SessionStateService();

        service.MarkExpired();

        service.IsSessionExpired.ShouldBeTrue();
    }

    [Fact]
    public void MarkExpired_RaisesOnSessionChanged()
    {
        var service = new SessionStateService();
        var raised = false;
        service.OnSessionChanged += () => raised = true;

        service.MarkExpired();

        raised.ShouldBeTrue();
    }

    [Fact]
    public void MarkExpired_AlreadyExpired_DoesNotRaiseAgain()
    {
        var service = new SessionStateService();
        service.MarkExpired();
        var raiseCount = 0;
        service.OnSessionChanged += () => raiseCount++;

        service.MarkExpired();

        raiseCount.ShouldBe(0);
    }

    [Fact]
    public void Clear_ResetsToNotExpired()
    {
        var service = new SessionStateService();
        service.MarkExpired();

        service.Clear();

        service.IsSessionExpired.ShouldBeFalse();
    }

    [Fact]
    public void Clear_RaisesOnSessionChangedWhenTransitioning()
    {
        var service = new SessionStateService();
        service.MarkExpired();
        var raised = false;
        service.OnSessionChanged += () => raised = true;

        service.Clear();

        raised.ShouldBeTrue();
    }

    [Fact]
    public void Clear_AlreadyNotExpired_DoesNotRaise()
    {
        var service = new SessionStateService();
        var raiseCount = 0;
        service.OnSessionChanged += () => raiseCount++;

        service.Clear();

        raiseCount.ShouldBe(0);
    }

    private sealed class ControllableExpirySource : ISessionExpirySubscriptionSource
    {
        private readonly Dictionary<string, Action> callbacks = new(StringComparer.Ordinal);

        public int SubscribeCalls { get; private set; }

        public IDisposable Subscribe(string cacheKey, Action callback)
        {
            SubscribeCalls++;
            callbacks[cacheKey] = callback;
            return new CallbackRemoval(callbacks, cacheKey, callback);
        }

        public void Publish(string cacheKey)
        {
            if (callbacks.TryGetValue(cacheKey, out var callback))
            {
                callback();
            }
        }

        public Action Snapshot(string cacheKey)
        {
            callbacks.TryGetValue(cacheKey, out var callback).ShouldBeTrue();
            return callback;
        }

        private sealed class CallbackRemoval(Dictionary<string, Action> callbacks, string cacheKey, Action callback) : IDisposable
        {
            public void Dispose()
            {
                if (callbacks.TryGetValue(cacheKey, out var current) && current == callback)
                {
                    callbacks.Remove(cacheKey);
                }
            }
        }
    }
}
