using System.Collections.Concurrent;

namespace SyntaxCircus.Blazor.Auth.Tests;

public class SessionExpiryBrokerTests
{
    [Fact]
    public void Publish_ThrowingSubscriber_ContinuesDeliveringAndLogsFailure()
    {
        var logger = new RecordingLogger<SessionExpiryBroker>();
        var broker = new SessionExpiryBroker(logger);
        var delivered = 0;
        using var throwingSubscription = broker.Subscribe("user:one", () => throw new InvalidOperationException("boom"));
        using var receivingSubscription = broker.Subscribe("user:one", () => delivered++);

        broker.Publish("user:one");

        delivered.ShouldBe(1);
        logger.Entries.ShouldContain(entry => entry.Level == LogLevel.Error && entry.Exception is InvalidOperationException);
    }

    [Fact]
    public void Subscription_DisposeIsIdempotentAndRemovedSubscriberReceivesNothing()
    {
        var broker = new SessionExpiryBroker();
        var delivered = 0;
        var subscription = broker.Subscribe("user:one", () => delivered++);

        subscription.Dispose();
        subscription.Dispose();
        broker.Publish("user:one");

        delivered.ShouldBe(0);
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public ConcurrentQueue<(LogLevel Level, Exception? Exception)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Enqueue((logLevel, exception));
    }
}
