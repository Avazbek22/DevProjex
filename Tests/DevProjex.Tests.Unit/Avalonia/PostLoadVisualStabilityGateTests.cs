using Avalonia.Threading;

namespace DevProjex.Tests.Unit.Avalonia;

public sealed class PostLoadVisualStabilityGateTests
{
    private static readonly TimeSpan CompletionTimeout =
        TimeSpan.FromSeconds(5);

    [Fact]
    public async Task WaitAsync_DoesNotSettleBeforeVisualTransitionCompletes()
    {
        var visualTransition = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var settleStepCount = 0;

        var waitTask = PostLoadVisualStabilityGate.WaitAsync(
            visualTransition.Task,
            (_, _) =>
            {
                Interlocked.Increment(ref settleStepCount);
                return Task.CompletedTask;
            },
            (_, _) =>
            {
                Interlocked.Increment(ref settleStepCount);
                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);

        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Equal(0, Volatile.Read(ref settleStepCount));
        Assert.False(waitTask.IsCompleted);

        visualTransition.SetResult();
        await waitTask.WaitAsync(
            CompletionTimeout,
            TestContext.Current.CancellationToken);

        Assert.Equal(4, Volatile.Read(ref settleStepCount));
    }

    [Fact]
    public async Task WaitAsync_DrainsFinalFramesAroundQuietPeriod()
    {
        var visualTransition = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var delayEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDelay = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var sequence = new List<string>();

        var waitTask = PostLoadVisualStabilityGate.WaitAsync(
            visualTransition.Task,
            (priority, _) =>
            {
                sequence.Add(priority == DispatcherPriority.Render
                    ? "render"
                    : "background");
                return Task.CompletedTask;
            },
            (delay, cancellationToken) =>
            {
                Assert.Equal(
                    UiTimingProfile.Scale(
                        PostLoadVisualStabilityGate.QuietPeriod),
                    delay);
                sequence.Add("quiet");
                delayEntered.SetResult();
                return releaseDelay.Task.WaitAsync(cancellationToken);
            },
            TestContext.Current.CancellationToken);

        visualTransition.SetResult();
        await delayEntered.Task.WaitAsync(
            CompletionTimeout,
            TestContext.Current.CancellationToken);

        Assert.Equal(["render", "quiet"], sequence);
        Assert.False(waitTask.IsCompleted);

        releaseDelay.SetResult();
        await waitTask.WaitAsync(
            CompletionTimeout,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            ["render", "quiet", "render", "background"],
            sequence);
    }

    [Fact]
    public async Task WaitAsync_CancellationStopsRemainingSettleWork()
    {
        var visualTransition = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var settleStepCount = 0;

        var waitTask = PostLoadVisualStabilityGate.WaitAsync(
            visualTransition.Task,
            (_, _) =>
            {
                Interlocked.Increment(ref settleStepCount);
                return Task.CompletedTask;
            },
            (_, _) =>
            {
                Interlocked.Increment(ref settleStepCount);
                return Task.CompletedTask;
            },
            cancellation.Token);

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await waitTask);
        Assert.Equal(0, Volatile.Read(ref settleStepCount));
    }
}
