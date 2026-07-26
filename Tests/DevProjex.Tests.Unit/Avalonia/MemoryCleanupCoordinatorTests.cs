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
        using var coordinator = CreateCoordinator(
            captureMemorySnapshot: static () => EmptySnapshot(),
            collect: mode =>
            {
                modes.Enqueue(mode);
                completion.TrySetResult(mode);
            });

        coordinator.Schedule(MemoryCleanupReason.PreviewRebuildCompleted);
        await Task.Delay(50);
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
    public async Task Schedule_ReusedSearchHeapWithSevereFragmentation_RunsAggressiveCleanupAfterBackground()
    {
        var backgroundCompleted = 0;
        var modes = new ConcurrentQueue<MemoryCleanupCollectionMode>();
        var aggressiveCleanupCompleted = NewCompletionSource();
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
                if (mode == MemoryCleanupCollectionMode.Aggressive)
                    aggressiveCleanupCompleted.TrySetResult(mode);
            });

        coordinator.Schedule(MemoryCleanupReason.PreviewRebuildCompleted);

        var mode = await aggressiveCleanupCompleted.Task.WaitAsync(
            CompletionTimeout);
        await WaitUntilIdleAsync(coordinator);

        Assert.Equal(MemoryCleanupCollectionMode.Aggressive, mode);
        Assert.Equal(
            [
                MemoryCleanupCollectionMode.Background,
                MemoryCleanupCollectionMode.Aggressive
            ],
            modes.ToArray());
    }

    [AvaloniaFact]
    public async Task SchedulePreview_SeverePostCollectionFragmentation_RunsAggressiveCleanupAfterBackground()
    {
        var backgroundCompleted = 0;
        var modes = new ConcurrentQueue<MemoryCleanupCollectionMode>();
        var compactionCompleted = NewCompletionSource();
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
                if (mode == MemoryCleanupCollectionMode.Aggressive)
                    compactionCompleted.TrySetResult(mode);
            });

        coordinator.SchedulePreview(
            MemoryCleanupReason.PreviewRebuildCompleted);

        var mode = await compactionCompleted.Task.WaitAsync(CompletionTimeout);
        await WaitUntilIdleAsync(coordinator);

        Assert.Equal(MemoryCleanupCollectionMode.Aggressive, mode);
        Assert.Equal(
            [
                MemoryCleanupCollectionMode.Background,
                MemoryCleanupCollectionMode.Aggressive
            ],
            modes.ToArray());
    }

    [AvaloniaFact]
    public async Task CancelAll_AfterBackgroundCollection_CancelsPendingAggressiveCleanup()
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
    public async Task CancelAll_DuringFinalSnapshot_CancelsAggressiveCleanupAtLastGate()
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
    public async Task SchedulePreview_CollectionIndexTimeout_DoesNotRunAggressiveFollowUp()
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

    private static MemoryCleanupCoordinator CreateCoordinator(
        Func<MemoryCleanupSnapshot> captureMemorySnapshot,
        Action<MemoryCleanupCollectionMode> collect,
        Func<bool>? uiReady = null,
        TimeSpan? uiReadinessTimeout = null,
        TimeSpan? uiReadinessPollInterval = null,
        int uiReadinessMaximumAttempts = 24,
        Action? trimWorkingSet = null) =>
        new(
            SessionMetricsRecorder.Disabled,
            uiReady ?? (static () => true),
            animationDuration: TimeSpan.Zero,
            captureMemorySnapshot,
            collect,
            uiReadinessTimeout,
            uiReadinessPollInterval,
            uiReadinessMaximumAttempts,
            trimWorkingSet ?? (static () => { }));

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
