namespace SyntaxCircus.Blazor.Auth.Tests;

public class KeyedRefreshLockTests
{
    private readonly KeyedRefreshLock locks = new();

    [Fact]
    public async Task LockStaysShared_WhileAnyCallerStillHoldsOrAwaitsIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var releaseFirst = NewSignal();
        var secondEntered = NewSignal();
        var releaseSecond = NewSignal();
        var thirdEntered = false;
        var first = locks.RunAsync("k", async _ => { await releaseFirst.Task; return 1; }, ct);
        var second = locks.RunAsync("k", async _ => { secondEntered.SetResult(); await releaseSecond.Task; return 2; }, ct);

        releaseFirst.SetResult();
        await first;
        await secondEntered.Task.WaitAsync(ct);
        // Arrives after the first holder left: must queue behind the second, not get a lock of its own.
        var third = locks.RunAsync("k", _ => { thirdEntered = true; return Task.FromResult(3); }, ct);
        await Task.Delay(30, ct);
        thirdEntered.ShouldBeFalse();
        locks.Count.ShouldBe(1);

        releaseSecond.SetResult();
        (await second).ShouldBe(2);
        (await third).ShouldBe(3);
        locks.Count.ShouldBe(0);
    }

    [Fact]
    public async Task CancelledWaiter_LeavesTheHoldersLockInPlace()
    {
        var ct = TestContext.Current.CancellationToken;
        var release = NewSignal();
        var holder = locks.RunAsync("k", async _ => { await release.Task; return 1; }, ct);
        using var cancellation = new CancellationTokenSource();
        var waiter = locks.RunAsync("k", _ => Task.FromResult(2), cancellation.Token);

        await cancellation.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => waiter);

        locks.Count.ShouldBe(1);
        var removed = false;
        locks.RunIfUnused("k", () => removed = true);
        removed.ShouldBeFalse();
        release.SetResult();
        await holder;
        locks.Count.ShouldBe(0);
        locks.RunIfUnused("k", () => removed = true);
        removed.ShouldBeTrue();
    }

    [Fact]
    public async Task ManyConcurrentCallersOnFewKeys_NeverOverlapOnAKey_AndLeaveNothingBehind()
    {
        var ct = TestContext.Current.CancellationToken;
        var active = new int[3];
        var overlapped = false;

        await Task.WhenAll(Enumerable.Range(0, 600).Select(i => Task.Run(
            () => locks.RunAsync(
                $"k{i % 3}",
                async _ =>
                {
                    if (Interlocked.Increment(ref active[i % 3]) != 1)
                    {
                        overlapped = true;
                    }

                    await Task.Yield();
                    Interlocked.Decrement(ref active[i % 3]);
                    return 0;
                },
                ct),
            ct)));

        overlapped.ShouldBeFalse();
        locks.Count.ShouldBe(0);
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
