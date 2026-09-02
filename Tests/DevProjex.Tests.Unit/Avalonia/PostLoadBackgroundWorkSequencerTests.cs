using System.Collections.Concurrent;
using DevProjex.Avalonia.Services;

namespace DevProjex.Tests.Unit.Avalonia;

public sealed class PostLoadBackgroundWorkSequencerTests
{
    [Theory]
    [InlineData(StatusOperationType.LoadProject)]
    [InlineData(StatusOperationType.RefreshProject)]
    [InlineData(StatusOperationType.GitPullUpdates)]
    [InlineData(StatusOperationType.GitSwitchBranch)]
    public void ResolveStatusPresentation_ProjectLifecycleOperation_IsImmediate(
        StatusOperationType sourceOperation)
    {
        Assert.Equal(
            StatusOperationPresentation.Immediate,
            PostLoadBackgroundWorkSequencer.ResolveStatusPresentation(sourceOperation));
    }

    [Theory]
    [InlineData(StatusOperationType.None)]
    [InlineData(StatusOperationType.SelectionRefresh)]
    [InlineData(StatusOperationType.ApplySettings)]
    [InlineData(StatusOperationType.CompressionPreparation)]
	[InlineData(StatusOperationType.SecretAnalysis)]
    public void ResolveStatusPresentation_InteractiveOrUnscopedWork_KeepsDelay(
        StatusOperationType sourceOperation)
    {
        Assert.Equal(
            StatusOperationPresentation.ExtendedDelay,
            PostLoadBackgroundWorkSequencer.ResolveStatusPresentation(sourceOperation));
    }

    [Fact]
    public async Task RunAsync_WaitsForVisualReadinessAndRunsPhasesInOrder()
    {
        var visualReady = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCompression = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseMetrics = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var sequence = new ConcurrentQueue<string>();

        var runTask = PostLoadBackgroundWorkSequencer.RunAsync(
            visualReady.Task,
            async cancellationToken =>
            {
                sequence.Enqueue("compression-start");
                await releaseCompression.Task.WaitAsync(cancellationToken);
                sequence.Enqueue("compression-end");
            },
            async cancellationToken =>
            {
                sequence.Enqueue("metrics-start");
                await releaseMetrics.Task.WaitAsync(cancellationToken);
                sequence.Enqueue("metrics-end");
            },
            () => sequence.Enqueue("secrets"),
            cleanupAfterCompletion: null,
            scheduleMemoryCleanup: null,
            TestContext.Current.CancellationToken);

        await Task.Delay(25, TestContext.Current.CancellationToken);
        Assert.Empty(sequence);

        visualReady.SetResult();
        await WaitForAsync(() => sequence.Count == 1);
        Assert.Equal(["compression-start"], sequence.ToArray());

        releaseCompression.SetResult();
        await WaitForAsync(() => sequence.Count == 3);
        Assert.Equal(
            ["compression-start", "compression-end", "metrics-start"],
            sequence.ToArray());

        releaseMetrics.SetResult();
        await runTask.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(
            ["compression-start", "compression-end", "metrics-start", "metrics-end", "secrets"],
            sequence.ToArray());
    }

    [Fact]
    public async Task RunAsync_CancellationBeforeVisualReadinessStartsNoBackgroundPhase()
    {
        var visualReady = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var phaseCount = 0;

        var runTask = PostLoadBackgroundWorkSequencer.RunAsync(
            visualReady.Task,
            _ =>
            {
                Interlocked.Increment(ref phaseCount);
                return Task.CompletedTask;
            },
            _ =>
            {
                Interlocked.Increment(ref phaseCount);
                return Task.CompletedTask;
            },
            () => Interlocked.Increment(ref phaseCount),
            cleanupAfterCompletion: null,
            scheduleMemoryCleanup: null,
            cancellation.Token);

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await runTask);
        Assert.Equal(0, Volatile.Read(ref phaseCount));
    }

    [Fact]
    public async Task RunAsync_CancellationDuringCompressionDoesNotStartLaterPhases()
    {
        using var cancellation = new CancellationTokenSource();
        var compressionStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var laterPhaseCount = 0;
        var cleanupCount = 0;

        var runTask = PostLoadBackgroundWorkSequencer.RunAsync(
            Task.CompletedTask,
            async cancellationToken =>
            {
                compressionStarted.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            },
            _ =>
            {
                Interlocked.Increment(ref laterPhaseCount);
                return Task.CompletedTask;
            },
            () => Interlocked.Increment(ref laterPhaseCount),
            MemoryCleanupReason.ApplySettingsWorkCompleted,
            _ => Interlocked.Increment(ref cleanupCount),
            cancellation.Token);

        await compressionStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await runTask);
        Assert.Equal(0, Volatile.Read(ref laterPhaseCount));
        Assert.Equal(0, Volatile.Read(ref cleanupCount));
    }

    [Fact]
    public async Task RunAsync_SettingsApplySchedulesCleanupAfterEveryPhaseCompletes()
    {
        var sequence = new List<string>();

        await PostLoadBackgroundWorkSequencer.RunAsync(
            Task.CompletedTask,
            _ =>
            {
                sequence.Add("compression");
                return Task.CompletedTask;
            },
            _ =>
            {
                sequence.Add("metrics");
                return Task.CompletedTask;
            },
            () => sequence.Add("secrets"),
            MemoryCleanupReason.ApplySettingsWorkCompleted,
            reason => sequence.Add($"cleanup:{reason}"),
            TestContext.Current.CancellationToken);

        Assert.Equal(
            [
                "compression",
                "metrics",
                "secrets",
                $"cleanup:{MemoryCleanupReason.ApplySettingsWorkCompleted}"
            ],
            sequence);
    }

    [Fact]
    public async Task RunAsync_ProjectLifecycleWithoutApplyReasonSchedulesNoApplyCleanup()
    {
        var scheduled = new List<MemoryCleanupReason>();

        await PostLoadBackgroundWorkSequencer.RunAsync(
            Task.CompletedTask,
            _ => Task.CompletedTask,
            _ => Task.CompletedTask,
            static () => { },
            cleanupAfterCompletion: null,
            scheduled.Add,
            TestContext.Current.CancellationToken);

        Assert.Empty(scheduled);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("The expected post-load phase did not start.");

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }
}
