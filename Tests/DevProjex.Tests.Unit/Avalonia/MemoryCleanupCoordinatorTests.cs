using System.Collections.Concurrent;
using System.Diagnostics;
using DevProjex.Avalonia.Services;

namespace DevProjex.Tests.Unit.Avalonia;

[Collection("AvaloniaUI")]
public sealed class MemoryCleanupCoordinatorTests
{
    private static readonly TimeSpan CompletionTimeout =
        TimeSpan.FromSeconds(5);

    [AvaloniaFact]
    public async Task SchedulePreview_PreviewCloseRunsForDetachedGraphAtAnyHeapSize()
    {
        var completion = NewCompletionSource();
        using var coordinator = CreateCoordinator(
            captureMemorySnapshot: static () => EmptySnapshot(),
            collect: mode => completion.TrySetResult(mode));

        coordinator.SchedulePreview(
            MemoryCleanupReason.PreviewClose);
        var mode = await completion.Task.WaitAsync(CompletionTimeout);
        await WaitUntilIdleAsync(coordinator);

        Assert.Equal(MemoryCleanupCollectionMode.Aggressive, mode);
        Assert.False(coordinator.IsCleanupPendingOrRunning);
    }

    [AvaloniaTheory]
    [InlineData(
        (int)MemoryCleanupReason.PreviewClose,
        (int)MemoryCleanupCollectionMode.Aggressive)]
    [InlineData(
        (int)MemoryCleanupReason.PreviewRebuildCompleted,
        (int)MemoryCleanupCollectionMode.Background)]
    public async Task SchedulePreview_UsesReasonSpecificCollector(
        int reasonRaw,
        int expectedModeRaw)
    {
        var completion = NewCompletionSource();
        using var coordinator = CreateCoordinator(
            captureMemorySnapshot: static () => HighHeapSnapshot(),
            collect: mode => completion.TrySetResult(mode));

        coordinator.SchedulePreview(
            (MemoryCleanupReason)reasonRaw);

        var mode = await completion.Task.WaitAsync(CompletionTimeout);
        await WaitUntilIdleAsync(coordinator);

        Assert.Equal((MemoryCleanupCollectionMode)expectedModeRaw, mode);
        Assert.False(coordinator.IsCleanupPendingOrRunning);
    }

    [AvaloniaFact]
    public async Task Schedule_SearchCloseRunsForDetachedGraphAtAnyHeapSize()
    {
        var completion = NewCompletionSource();
        using var coordinator = CreateCoordinator(
            captureMemorySnapshot: static () => EmptySnapshot(),
            collect: mode => completion.TrySetResult(mode));

        coordinator.Schedule(MemoryCleanupReason.SearchClose);
        var mode = await completion.Task.WaitAsync(CompletionTimeout);
        await WaitUntilIdleAsync(coordinator);

        Assert.Equal(MemoryCleanupCollectionMode.Aggressive, mode);
        Assert.False(coordinator.IsCleanupPendingOrRunning);
    }

    [AvaloniaFact]
    public async Task Schedule_SearchCloseHighHeapInvokesAggressiveCollector()
    {
        var completion = NewCompletionSource();
        using var coordinator = CreateCoordinator(
            captureMemorySnapshot: static () => HighHeapSnapshot(),
            collect: mode => completion.TrySetResult(mode));

        coordinator.Schedule(MemoryCleanupReason.SearchClose);

        var mode = await completion.Task.WaitAsync(CompletionTimeout);
        await WaitUntilIdleAsync(coordinator);

        Assert.Equal(MemoryCleanupCollectionMode.Aggressive, mode);
        Assert.False(coordinator.IsCleanupPendingOrRunning);
    }

    [AvaloniaFact]
    public async Task CancelBackground_CancelsDeferredCollection()
    {
        var collectionCount = 0;
        using var coordinator = CreateCoordinator(
            captureMemorySnapshot: static () => HighPressureSnapshot(),
            collect: _ => Interlocked.Increment(ref collectionCount));

        coordinator.Schedule(MemoryCleanupReason.PreviewClose);
        Assert.True(coordinator.IsCleanupPendingOrRunning);

        coordinator.CancelBackground();
        await WaitUntilIdleAsync(coordinator);
        await Task.Delay(500);

        Assert.Equal(0, Volatile.Read(ref collectionCount));
        Assert.False(coordinator.IsCleanupPendingOrRunning);
    }

    [AvaloniaFact]
    public async Task SchedulePreview_ImmediateCancelDoesNotRearmBackgroundCleanup()
    {
        var collectionCount = 0;
        using var coordinator = CreateCoordinator(
            captureMemorySnapshot: static () => HighPressureSnapshot(),
            collect: _ => Interlocked.Increment(ref collectionCount));

        coordinator.SchedulePreview(
            MemoryCleanupReason.PreviewClose);
        coordinator.CancelAll();
        await WaitUntilIdleAsync(coordinator);
        await Task.Delay(200);

        Assert.Equal(0, Volatile.Read(ref collectionCount));
        Assert.False(coordinator.IsCleanupPendingOrRunning);
    }

    [AvaloniaFact]
    public async Task Schedule_NewerRequestCoalescesOlderDeferredCollection()
    {
        var modes = new ConcurrentQueue<MemoryCleanupCollectionMode>();
        var completion = NewCompletionSource();
        using var coordinator = CreateCoordinator(
            captureMemorySnapshot: static () => HighPressureSnapshot(),
            collect: mode =>
            {
                modes.Enqueue(mode);
                completion.TrySetResult(mode);
            });

        coordinator.Schedule(MemoryCleanupReason.PreviewRebuildCompleted);
        coordinator.Schedule(MemoryCleanupReason.PreviewRebuildCompleted);

        var mode = await completion.Task.WaitAsync(CompletionTimeout);
        await WaitUntilIdleAsync(coordinator);
        await Task.Delay(200);

        Assert.Equal(MemoryCleanupCollectionMode.Background, mode);
        Assert.Equal(
            [MemoryCleanupCollectionMode.Background],
            modes.ToArray());
    }

    [AvaloniaFact]
    public async Task SchedulePreview_BackgroundRequestCannotReplacePendingAggressiveCleanup()
    {
        var modes = new ConcurrentQueue<MemoryCleanupCollectionMode>();
        var completion = NewCompletionSource();
        using var coordinator = CreateCoordinator(
            captureMemorySnapshot: static () => EmptySnapshot(),
            collect: mode =>
            {
                modes.Enqueue(mode);
                completion.TrySetResult(mode);
            });

        coordinator.Schedule(MemoryCleanupReason.SearchClose);
        coordinator.SchedulePreview(
            MemoryCleanupReason.PreviewRebuildCompleted);

        var mode = await completion.Task.WaitAsync(CompletionTimeout);
        await WaitUntilIdleAsync(coordinator);
        await Task.Delay(200);

        Assert.Equal(MemoryCleanupCollectionMode.Aggressive, mode);
        Assert.Equal(
            [MemoryCleanupCollectionMode.Aggressive],
            modes.ToArray());
    }

    [AvaloniaFact]
    public async Task Schedule_AggressiveRequestUpgradesPendingBackgroundCleanup()
    {
        var modes = new ConcurrentQueue<MemoryCleanupCollectionMode>();
        var completion = NewCompletionSource();
        var backgroundDelayEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingBackgroundDelay = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var deferInvocationCount = 0;
        using var coordinator = CreateCoordinator(
            captureMemorySnapshot: static () => EmptySnapshot(),
            collect: mode =>
            {
                modes.Enqueue(mode);
                completion.TrySetResult(mode);
            },
            deferCleanup: (_, cancellationToken) =>
            {
                if (Interlocked.Increment(ref deferInvocationCount) != 1)
                    return Task.CompletedTask;

                backgroundDelayEntered.TrySetResult();
                return pendingBackgroundDelay.Task.WaitAsync(cancellationToken);
            });

        coordinator.Schedule(MemoryCleanupReason.PreviewRebuildCompleted);
        await backgroundDelayEntered.Task.WaitAsync(CompletionTimeout);
        coordinator.Schedule(MemoryCleanupReason.SearchClose);

        var mode = await completion.Task.WaitAsync(CompletionTimeout);
        await WaitUntilIdleAsync(coordinator);
        await Task.Delay(200);

        Assert.Equal(MemoryCleanupCollectionMode.Aggressive, mode);
        Assert.Equal(
            [MemoryCleanupCollectionMode.Aggressive],
            modes.ToArray());
    }

    [AvaloniaFact]
    public async Task SchedulePreview_PostCollectionBelowAbsoluteFragmentationThreshold_StopsAfterBackground()
    {
        var backgroundCompleted = 0;
        var modes = new ConcurrentQueue<MemoryCleanupCollectionMode>();
        using var coordinator = CreateCoordinator(
            captureMemorySnapshot: () =>
                Volatile.Read(ref backgroundCompleted) == 0
                    ? PreCollectionSnapshot()
                    : PostCollectionSnapshot(
                        collectionIndex: 11,
                        heapSizeMegabytes: 200,
                        fragmentedMegabytes: 31),
            collect: mode =>
            {
                modes.Enqueue(mode);
                if (mode == MemoryCleanupCollectionMode.Background)
                    Volatile.Write(ref backgroundCompleted, 1);
            });

        coordinator.SchedulePreview(
            MemoryCleanupReason.PreviewRebuildCompleted);
        await WaitUntilIdleAsync(coordinator);

        Assert.Equal(
            [MemoryCleanupCollectionMode.Background],
            modes.ToArray());
    }

    [AvaloniaFact]
    public async Task Schedule_InteractiveHeapWithSevereFragmentation_RunsCompactingCleanupAfterBackground()
    {
        var backgroundCompleted = 0;
        var modes = new ConcurrentQueue<MemoryCleanupCollectionMode>();
        var compactingCleanupCompleted = NewCompletionSource();
        using var coordinator = CreateCoordinator(
            captureMemorySnapshot: () =>
                Volatile.Read(ref backgroundCompleted) == 0
                    ? PreCollectionSnapshot()
                    : PostCollectionSnapshot(
                        collectionIndex: 11,
                        heapSizeMegabytes: 466,
                        fragmentedMegabytes: 339,
                        managedMegabytes: 127),
            collect: mode =>
            {
                modes.Enqueue(mode);
                if (mode == MemoryCleanupCollectionMode.Background)
                    Volatile.Write(ref backgroundCompleted, 1);
                if (mode == MemoryCleanupCollectionMode.Compacting)
                    compactingCleanupCompleted.TrySetResult(mode);
            });

        coordinator.Schedule(MemoryCleanupReason.PreviewRebuildCompleted);

        var mode = await compactingCleanupCompleted.Task.WaitAsync(
            CompletionTimeout);
        await WaitUntilIdleAsync(coordinator);

        Assert.Equal(MemoryCleanupCollectionMode.Compacting, mode);
        Assert.Equal(
            [
                MemoryCleanupCollectionMode.Background,
                MemoryCleanupCollectionMode.Compacting
            ],
            modes.ToArray());
    }

    [AvaloniaFact]
    public async Task SchedulePreview_SeverePostCollectionFragmentation_RunsCompactingCleanupWithoutTrim()
    {
        var backgroundCompleted = 0;
        var modes = new ConcurrentQueue<MemoryCleanupCollectionMode>();
        var compactionCompleted = NewCompletionSource();
        var trimCount = 0;
		var traceLines = new ConcurrentQueue<string>();
        using var coordinator = CreateCoordinator(
            captureMemorySnapshot: () =>
                Volatile.Read(ref backgroundCompleted) == 0
                    ? PreCollectionSnapshot()
                    : PostCollectionSnapshot(
                        collectionIndex: 11,
                        heapSizeMegabytes: 640,
                        fragmentedMegabytes: 256),
            collect: mode =>
            {
                modes.Enqueue(mode);
                if (mode == MemoryCleanupCollectionMode.Background)
                    Volatile.Write(ref backgroundCompleted, 1);
                if (mode == MemoryCleanupCollectionMode.Compacting)
                    compactionCompleted.TrySetResult(mode);
            },
            trimWorkingSet: () => Interlocked.Increment(ref trimCount),
			memoryCleanupTrace: new MemoryCleanupTrace(
				static () => new MemoryCleanupRetentionSnapshot(123, 456),
				traceLines.Enqueue));

        coordinator.SchedulePreview(
            MemoryCleanupReason.PreviewRebuildCompleted);

        var mode = await compactionCompleted.Task.WaitAsync(CompletionTimeout);
        await WaitUntilIdleAsync(coordinator);

        Assert.Equal(MemoryCleanupCollectionMode.Compacting, mode);
        Assert.Equal(
            [
                MemoryCleanupCollectionMode.Background,
                MemoryCleanupCollectionMode.Compacting
            ],
            modes.ToArray());
        Assert.Equal(0, Volatile.Read(ref trimCount));
		Assert.Collection(
			traceLines,
			line =>
			{
				Assert.Contains("reason=PreviewRebuildCompleted", line, StringComparison.Ordinal);
				Assert.Contains("stage=Background", line, StringComparison.Ordinal);
				Assert.Contains("planCache=123->123", line, StringComparison.Ordinal);
				Assert.Contains("readFacts=456->456", line, StringComparison.Ordinal);
			},
			line =>
			{
				Assert.Contains("reason=PreviewRebuildCompleted", line, StringComparison.Ordinal);
				Assert.Contains("stage=Compacting", line, StringComparison.Ordinal);
			});
    }

    [AvaloniaFact]
    public async Task Schedule_BackgroundPlanWithAggressiveEscalation_RunsAggressiveCleanupAndTrim()
    {
        var backgroundCompleted = 0;
        var modes = new ConcurrentQueue<MemoryCleanupCollectionMode>();
        var trimCompleted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var coordinator = CreateCoordinator(
            captureMemorySnapshot: () =>
                Volatile.Read(ref backgroundCompleted) == 0
                    ? PreCollectionSnapshot()
                    : PostCollectionSnapshot(
                        collectionIndex: 11,
                        heapSizeMegabytes: 640,
                        fragmentedMegabytes: 256),
            collect: mode =>
            {
                modes.Enqueue(mode);
                if (mode == MemoryCleanupCollectionMode.Background)
                    Volatile.Write(ref backgroundCompleted, 1);
            },
            trimWorkingSet: () => trimCompleted.TrySetResult());

        coordinator.Schedule(
            MemoryCleanupReason.InitialProjectLoad,
            new MemoryCleanupPlan(
                Delay: TimeSpan.Zero,
                WaitForUiSettled: false,
                CollectionMode: MemoryCleanupCollectionMode.Background,
                EscalationMode: MemoryCleanupEscalationMode.Aggressive));

        await trimCompleted.Task.WaitAsync(CompletionTimeout);
        await WaitUntilIdleAsync(coordinator);

        Assert.Equal(
            [
                MemoryCleanupCollectionMode.Background,
                MemoryCleanupCollectionMode.Aggressive
            ],
            modes.ToArray());
    }

    [AvaloniaFact]
    public async Task Schedule_BackgroundPlanWithoutEscalation_StopsAfterBackground()
    {
        var modes = new ConcurrentQueue<MemoryCleanupCollectionMode>();
        using var coordinator = CreateCoordinator(
            captureMemorySnapshot: static () => PreCollectionSnapshot(),
            collect: modes.Enqueue);

        coordinator.Schedule(
            MemoryCleanupReason.ApplySettingsWorkCompleted,
            new MemoryCleanupPlan(
                Delay: TimeSpan.Zero,
                WaitForUiSettled: false,
                CollectionMode: MemoryCleanupCollectionMode.Background,
                EscalationMode: MemoryCleanupEscalationMode.None));

        await WaitUntilIdleAsync(coordinator);

        Assert.Equal(
            [MemoryCleanupCollectionMode.Background],
            modes.ToArray());
    }

    [AvaloniaFact]
    public async Task CancelAll_AfterBackgroundCollection_CancelsPendingCompactingCleanup()
    {
        var backgroundCompleted = 0;
        var modes = new ConcurrentQueue<MemoryCleanupCollectionMode>();
        var postCollectionObserved =
            new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        using var releasePostCollectionSnapshot = new ManualResetEventSlim();
        using var coordinator = CreateCoordinator(
            captureMemorySnapshot: () =>
            {
                if (Volatile.Read(ref backgroundCompleted) == 0)
                    return PreCollectionSnapshot();

                postCollectionObserved.TrySetResult(true);
                releasePostCollectionSnapshot.Wait(CompletionTimeout);
                return PostCollectionSnapshot(
                    collectionIndex: 11,
                    heapSizeMegabytes: 640,
                    fragmentedMegabytes: 256);
            },
            collect: mode =>
            {
                modes.Enqueue(mode);
                if (mode == MemoryCleanupCollectionMode.Background)
                    Volatile.Write(ref backgroundCompleted, 1);
            });

        coordinator.SchedulePreview(
            MemoryCleanupReason.PreviewRebuildCompleted);
        await postCollectionObserved.Task.WaitAsync(CompletionTimeout);

        coordinator.CancelAll();
        releasePostCollectionSnapshot.Set();
        await WaitUntilIdleAsync(coordinator);

        Assert.Equal(
            [MemoryCleanupCollectionMode.Background],
            modes.ToArray());
    }

    [AvaloniaFact]
    public async Task CancelAll_DuringFinalSnapshot_CancelsCompactingCleanupAtLastGate()
    {
        var backgroundCompleted = 0;
        var postCollectionCaptureCount = 0;
        var modes = new ConcurrentQueue<MemoryCleanupCollectionMode>();
        var finalSnapshotStarted = NewCompletionSource();
        using var releaseFinalSnapshot = new ManualResetEventSlim();
        using var coordinator = CreateCoordinator(
            captureMemorySnapshot: () =>
            {
                if (Volatile.Read(ref backgroundCompleted) == 0)
                    return PreCollectionSnapshot();

                if (Interlocked.Increment(ref postCollectionCaptureCount) == 2)
                {
                    finalSnapshotStarted.TrySetResult(
                        MemoryCleanupCollectionMode.Background);
                    releaseFinalSnapshot.Wait(CompletionTimeout);
                }

                return PostCollectionSnapshot(
                    collectionIndex: 11,
                    heapSizeMegabytes: 640,
                    fragmentedMegabytes: 256);
            },
            collect: mode =>
            {
                modes.Enqueue(mode);
                if (mode == MemoryCleanupCollectionMode.Background)
                    Volatile.Write(ref backgroundCompleted, 1);
            });

        coordinator.SchedulePreview(
            MemoryCleanupReason.PreviewRebuildCompleted);
        await finalSnapshotStarted.Task.WaitAsync(CompletionTimeout);

        coordinator.CancelAll();
        releaseFinalSnapshot.Set();
        await WaitUntilIdleAsync(coordinator);

        Assert.Equal(
            [MemoryCleanupCollectionMode.Background],
            modes.ToArray());
    }

    [AvaloniaFact]
    public async Task SchedulePreview_CollectionIndexTimeout_DoesNotRunCompactingFollowUp()
    {
        var backgroundCompleted = 0;
        var modes = new ConcurrentQueue<MemoryCleanupCollectionMode>();
        using var coordinator = CreateCoordinator(
            captureMemorySnapshot: () =>
                Volatile.Read(ref backgroundCompleted) == 0
                    ? PreCollectionSnapshot()
                    : PostCollectionSnapshot(
                        collectionIndex: 10,
                        heapSizeMegabytes: 640,
                        fragmentedMegabytes: 256),
            collect: mode =>
            {
                modes.Enqueue(mode);
                if (mode == MemoryCleanupCollectionMode.Background)
                    Volatile.Write(ref backgroundCompleted, 1);
            });

        coordinator.SchedulePreview(
            MemoryCleanupReason.PreviewRebuildCompleted);
        await WaitUntilIdleAsync(coordinator);

        Assert.Equal(
            [MemoryCleanupCollectionMode.Background],
            modes.ToArray());
    }

    [AvaloniaFact]
    public async Task SchedulePreview_UiNeverSettles_StopsAtDeadlineWithoutCollection()
    {
        var readinessChecks = 0;
        var collectionCount = 0;
        using var coordinator = CreateCoordinator(
            captureMemorySnapshot: static () => HighPressureSnapshot(),
            collect: _ => Interlocked.Increment(ref collectionCount),
            uiReady: () =>
            {
                Interlocked.Increment(ref readinessChecks);
                return false;
            },
            uiReadinessTimeout: TimeSpan.FromMilliseconds(80),
            uiReadinessPollInterval: TimeSpan.FromMilliseconds(10),
            uiReadinessMaximumAttempts: 100);

        coordinator.SchedulePreview(
            MemoryCleanupReason.PreviewRebuildCompleted);
        await WaitUntilIdleAsync(coordinator);

        Assert.InRange(Volatile.Read(ref readinessChecks), 1, 99);
        Assert.Equal(0, Volatile.Read(ref collectionCount));
    }

    [AvaloniaFact]
    public async Task SchedulePreview_UiKeepsChanging_StopsAtAttemptBudgetWithoutCollection()
    {
        var readinessChecks = 0;
        var collectionCount = 0;
        using var coordinator = CreateCoordinator(
            captureMemorySnapshot: static () => HighPressureSnapshot(),
            collect: _ => Interlocked.Increment(ref collectionCount),
            uiReady: () =>
                Interlocked.Increment(ref readinessChecks) % 2 == 0,
            uiReadinessTimeout: TimeSpan.FromSeconds(2),
            uiReadinessPollInterval: TimeSpan.FromMilliseconds(1),
            uiReadinessMaximumAttempts: 6);

        coordinator.SchedulePreview(
            MemoryCleanupReason.PreviewRebuildCompleted);
        await WaitUntilIdleAsync(coordinator);

        Assert.Equal(6, Volatile.Read(ref readinessChecks));
        Assert.Equal(0, Volatile.Read(ref collectionCount));
    }

    [AvaloniaFact]
    public async Task SchedulePreview_UiSettlesWithinBudget_ContinuesCleanup()
    {
        var readinessChecks = 0;
        var completion = NewCompletionSource();
        using var coordinator = CreateCoordinator(
            captureMemorySnapshot: static () => EmptySnapshot(),
            collect: mode => completion.TrySetResult(mode),
            uiReady: () => Interlocked.Increment(ref readinessChecks) > 1,
            uiReadinessTimeout: TimeSpan.FromSeconds(1),
            uiReadinessPollInterval: TimeSpan.FromMilliseconds(1),
            uiReadinessMaximumAttempts: 6);

        coordinator.SchedulePreview(
            MemoryCleanupReason.PreviewRebuildCompleted);

        var mode = await completion.Task.WaitAsync(CompletionTimeout);
        await WaitUntilIdleAsync(coordinator);

        Assert.Equal(MemoryCleanupCollectionMode.Background, mode);
        Assert.Equal(4, Volatile.Read(ref readinessChecks));
    }

    [Fact]
    public void RunImmediate_MapsRequestedCompactionToAggressiveCollectorMode()
    {
        var modes = new List<MemoryCleanupCollectionMode>();
        var trimCount = 0;
        using var coordinator = CreateCoordinator(
            captureMemorySnapshot: static () => EmptySnapshot(),
            collect: modes.Add,
            trimWorkingSet: () => trimCount++);

        coordinator.RunImmediate(compactLargeObjectHeap: false);
        coordinator.RunImmediate(compactLargeObjectHeap: true);

        Assert.Equal(
            [
                MemoryCleanupCollectionMode.Background,
                MemoryCleanupCollectionMode.Aggressive
            ],
            modes);
        Assert.Equal(1, trimCount);
    }

    [AvaloniaFact]
    public async Task Schedule_AggressiveCleanupTrimsWorkingSetAfterCollection()
    {
        var sequence = new ConcurrentQueue<string>();
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var coordinator = CreateCoordinator(
            captureMemorySnapshot: static () => EmptySnapshot(),
            collect: mode => sequence.Enqueue($"collect:{mode}"),
            trimWorkingSet: () =>
            {
                sequence.Enqueue("trim");
                completion.TrySetResult();
            });

        coordinator.Schedule(MemoryCleanupReason.ProjectSwitchPostLoad);
        await completion.Task.WaitAsync(CompletionTimeout);
        await WaitUntilIdleAsync(coordinator);

        Assert.Equal(
            ["collect:Aggressive", "trim"],
            sequence.ToArray());
    }

    [AvaloniaFact]
    public async Task Schedule_PostLoadCleanupWaitsForVisualReadyAndRechecksUiAfterDelay()
    {
        var visualReady = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var delayEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDelay = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var readinessObserved = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var collection = NewCompletionSource();
        var readinessChecks = 0;
        var uiReady = 1;
        using var coordinator = CreateCoordinator(
            captureMemorySnapshot: static () => EmptySnapshot(),
            collect: mode => collection.TrySetResult(mode),
            uiReady: () =>
            {
                Interlocked.Increment(ref readinessChecks);
                readinessObserved.TrySetResult();
                return Volatile.Read(ref uiReady) != 0;
            },
            // The production deadline guards a stalled dispatcher. This test drives the
            // readiness transition explicitly, so CI scheduling must not consume that deadline.
            uiReadinessTimeout: TimeSpan.FromMinutes(1),
            uiReadinessPollInterval: TimeSpan.FromMilliseconds(10),
            deferCleanup: (_, cancellationToken) =>
            {
                delayEntered.TrySetResult();
                return releaseDelay.Task.WaitAsync(cancellationToken);
            });

        coordinator.Schedule(
            MemoryCleanupReason.InitialProjectLoad,
            visualReady.Task);

        await delayEntered.Task.WaitAsync(CompletionTimeout);
        Assert.Equal(0, Volatile.Read(ref readinessChecks));

        Volatile.Write(ref uiReady, 0);
        releaseDelay.TrySetResult();
        await Task.Delay(50);
        Assert.Equal(0, Volatile.Read(ref readinessChecks));
        Assert.False(collection.Task.IsCompleted);

        visualReady.TrySetResult();
        await readinessObserved.Task.WaitAsync(CompletionTimeout);
        Assert.True(Volatile.Read(ref readinessChecks) > 0);
        Assert.False(collection.Task.IsCompleted);

        Volatile.Write(ref uiReady, 1);
        var mode = await collection.Task.WaitAsync(CompletionTimeout);
        await WaitUntilIdleAsync(coordinator);

        Assert.Equal(MemoryCleanupCollectionMode.Aggressive, mode);
    }

    [AvaloniaFact]
    public async Task Schedule_PostLoadCleanupCanceledBeforeVisualReadyDoesNotCollect()
    {
        var visualReady = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var collectionCount = 0;
        using var coordinator = CreateCoordinator(
            captureMemorySnapshot: static () => EmptySnapshot(),
            collect: _ => Interlocked.Increment(ref collectionCount));

        coordinator.Schedule(
            MemoryCleanupReason.InitialProjectLoad,
            visualReady.Task);
        Assert.True(coordinator.IsCleanupPendingOrRunning);

        coordinator.CancelBackground();
        await WaitUntilIdleAsync(coordinator);
        visualReady.TrySetResult();
        await Task.Delay(100);

        Assert.Equal(0, Volatile.Read(ref collectionCount));
    }

    [AvaloniaFact]
    public async Task Schedule_BackgroundCleanupDoesNotTrimWorkingSet()
    {
        var trimCount = 0;
        var completion = NewCompletionSource();
        using var coordinator = CreateCoordinator(
            captureMemorySnapshot: static () => EmptySnapshot(),
            collect: mode => completion.TrySetResult(mode),
            trimWorkingSet: () => Interlocked.Increment(ref trimCount));

        coordinator.Schedule(MemoryCleanupReason.PreviewRebuildCompleted);
        var mode = await completion.Task.WaitAsync(CompletionTimeout);
        await WaitUntilIdleAsync(coordinator);

        Assert.Equal(MemoryCleanupCollectionMode.Background, mode);
        Assert.Equal(0, Volatile.Read(ref trimCount));
    }

	[AvaloniaFact]
	public async Task Schedule_BackgroundFailureIsReportedByTheSharedRegistry()
	{
		var reported = new TaskCompletionSource<(string Operation, Exception Error)>(
			TaskCreationOptions.RunContinuationsAsynchronously);
		using var registry = new BackgroundTaskRegistry(
			reportFailure: (operation, error) => reported.TrySetResult((operation, error)));
		using var coordinator = CreateCoordinator(
			captureMemorySnapshot: static () => EmptySnapshot(),
			collect: static _ => { },
			deferCleanup: static (_, _) => Task.FromException(new InvalidOperationException("cleanup failed")),
			backgroundTasks: registry);

		coordinator.Schedule(MemoryCleanupReason.PreviewRebuildCompleted);
		var failure = await reported.Task.WaitAsync(CompletionTimeout);
		await WaitUntilIdleAsync(coordinator);

		Assert.Equal("MemoryCleanup.ScheduleCore", failure.Operation);
		Assert.IsType<InvalidOperationException>(failure.Error);
	}

	[AvaloniaFact]
	public async Task SchedulePreview_RenderFailureIsReportedByTheSharedRegistry()
	{
		var reported = new TaskCompletionSource<(string Operation, Exception Error)>(
			TaskCreationOptions.RunContinuationsAsynchronously);
		using var registry = new BackgroundTaskRegistry(
			reportFailure: (operation, error) => reported.TrySetResult((operation, error)));
		using var coordinator = CreateCoordinator(
			captureMemorySnapshot: static () => EmptySnapshot(),
			collect: static _ => { },
			waitForRenderPasses: static _ => Task.FromException(new InvalidOperationException("render failed")),
			backgroundTasks: registry);

		coordinator.SchedulePreview(MemoryCleanupReason.PreviewClose);
		var failure = await reported.Task.WaitAsync(CompletionTimeout);
		await WaitUntilIdleAsync(coordinator);

		Assert.Equal("MemoryCleanup.SchedulePreview", failure.Operation);
		Assert.IsType<InvalidOperationException>(failure.Error);
	}

    private static MemoryCleanupCoordinator CreateCoordinator(
        Func<MemoryCleanupSnapshot> captureMemorySnapshot,
        Action<MemoryCleanupCollectionMode> collect,
        Func<bool>? uiReady = null,
        TimeSpan? uiReadinessTimeout = null,
        TimeSpan? uiReadinessPollInterval = null,
        int uiReadinessMaximumAttempts = 24,
        Action? trimWorkingSet = null,
        Func<TimeSpan, CancellationToken, Task>? deferCleanup = null,
		MemoryCleanupTrace? memoryCleanupTrace = null,
		Func<CancellationToken, Task>? waitForRenderPasses = null,
		BackgroundTaskRegistry? backgroundTasks = null)
    {
        var readinessProbe = uiReady ?? (static () => true);
        return new MemoryCleanupCoordinator(
            SessionMetricsRecorder.Disabled,
            readinessProbe,
            animationDuration: TimeSpan.Zero,
            captureMemorySnapshot,
            collect,
            uiReadinessTimeout,
            uiReadinessPollInterval,
            uiReadinessMaximumAttempts,
            trimWorkingSet ?? (static () => { }),
            deferCleanup,
            waitForRenderPasses: waitForRenderPasses ?? (static _ => Task.CompletedTask),
            queryUiReadiness: _ => Task.FromResult(readinessProbe()),
			memoryCleanupTrace,
			backgroundTasks);
    }

    private static async Task WaitUntilIdleAsync(
        MemoryCleanupCoordinator coordinator)
    {
        var stopwatch = Stopwatch.StartNew();
        while (coordinator.IsCleanupPendingOrRunning &&
               stopwatch.Elapsed < CompletionTimeout)
        {
            await Task.Delay(20);
        }

        Assert.False(
            coordinator.IsCleanupPendingOrRunning,
            "Memory cleanup did not reach an idle state before the timeout.");
    }

    private static TaskCompletionSource<MemoryCleanupCollectionMode>
        NewCompletionSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static MemoryCleanupSnapshot EmptySnapshot() =>
        new(
            ManagedHeapBytes: 0,
            HeapSizeBytes: 0,
            FragmentedBytes: 0,
            MemoryLoadBytes: 0,
            HighMemoryLoadThresholdBytes: 0);

    private static MemoryCleanupSnapshot HighHeapSnapshot() =>
        new(
            ManagedHeapBytes: 512L * 1024 * 1024,
            HeapSizeBytes: 512L * 1024 * 1024,
            FragmentedBytes: 0,
            MemoryLoadBytes: 0,
            HighMemoryLoadThresholdBytes: 0);

    private static MemoryCleanupSnapshot HighPressureSnapshot() =>
        new(
            ManagedHeapBytes: 256L * 1024 * 1024,
            HeapSizeBytes: 256L * 1024 * 1024,
            FragmentedBytes: 0,
            MemoryLoadBytes: 900,
            HighMemoryLoadThresholdBytes: 1_000);

    private static MemoryCleanupSnapshot PreCollectionSnapshot() =>
        PostCollectionSnapshot(
            collectionIndex: 10,
            heapSizeMegabytes: 640,
            fragmentedMegabytes: 320);

    private static MemoryCleanupSnapshot PostCollectionSnapshot(
        long collectionIndex,
        long heapSizeMegabytes,
        long fragmentedMegabytes,
        long? managedMegabytes = null) =>
        new(
            ManagedHeapBytes:
                (managedMegabytes ?? heapSizeMegabytes) * 1024 * 1024,
            HeapSizeBytes: heapSizeMegabytes * 1024 * 1024,
            FragmentedBytes: fragmentedMegabytes * 1024 * 1024,
            MemoryLoadBytes: 0,
            HighMemoryLoadThresholdBytes: 0,
            CollectionIndex: collectionIndex);
}
