using System.Runtime;
using DevProjex.Avalonia.Services;

namespace DevProjex.Avalonia.Coordinators;

internal sealed class MemoryCleanupCoordinator(
    SessionMetricsRecorder sessionMetrics,
    Func<bool> uiReady,
    TimeSpan animationDuration)
    : IDisposable
{
    private static readonly TimeSpan BackgroundCollectionObservationTimeout =
        TimeSpan.FromSeconds(2);
    private static readonly TimeSpan BackgroundCollectionPollInterval =
        TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan AggressiveCleanupIdleDelay =
        TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan UiReadinessTimeout =
        TimeSpan.FromSeconds(3);
    private static readonly TimeSpan UiReadinessPollInterval =
        TimeSpan.FromMilliseconds(120);
    private const int UiReadinessRequiredStableSamples = 3;
    private const int UiReadinessMaximumAttempts = 24;

    private readonly Func<MemoryCleanupSnapshot> _captureMemorySnapshot =
        MemoryCleanupSnapshot.Capture;
    private readonly Action<MemoryCleanupCollectionMode> _collect =
        CollectMemory;
    private readonly Action _trimWorkingSet =
        ProcessWorkingSetTrimmer.TrimCurrentProcess;
    private readonly Func<TimeSpan, CancellationToken, Task> _deferCleanup =
        Task.Delay;
    private readonly TimeSpan _uiReadinessTimeout = UiReadinessTimeout;
    private readonly TimeSpan _uiReadinessPollInterval =
        UiReadinessPollInterval;
    private readonly int _uiReadinessMaximumAttempts =
        UiReadinessMaximumAttempts;
    private readonly object _backgroundCleanupGate = new();
    private CancellationTokenSource? _backgroundCleanupCts;
    private MemoryCleanupCollectionMode _backgroundCleanupMode;
    private CancellationTokenSource? _previewCleanupCts;
    private int _previewCleanupVersion;
    private int _cleanupInProgress;
    private int _disposed;

    internal bool IsCleanupPendingOrRunning =>
        Volatile.Read(ref _cleanupInProgress) != 0 ||
        Volatile.Read(ref _backgroundCleanupCts) is not null ||
        Volatile.Read(ref _previewCleanupCts) is not null;

    internal MemoryCleanupCoordinator(
        SessionMetricsRecorder sessionMetrics,
        Func<bool> uiReady,
        TimeSpan animationDuration,
        Func<MemoryCleanupSnapshot> captureMemorySnapshot,
        Action<MemoryCleanupCollectionMode> collect,
        TimeSpan? uiReadinessTimeout = null,
        TimeSpan? uiReadinessPollInterval = null,
        int uiReadinessMaximumAttempts = UiReadinessMaximumAttempts,
        Action? trimWorkingSet = null,
        Func<TimeSpan, CancellationToken, Task>? deferCleanup = null)
        : this(sessionMetrics, uiReady, animationDuration)
    {
        _captureMemorySnapshot = captureMemorySnapshot;
        _collect = collect;
        _trimWorkingSet =
            trimWorkingSet ?? ProcessWorkingSetTrimmer.TrimCurrentProcess;
        _uiReadinessTimeout =
            uiReadinessTimeout ?? UiReadinessTimeout;
        _uiReadinessPollInterval =
            uiReadinessPollInterval ?? UiReadinessPollInterval;
        _uiReadinessMaximumAttempts = Math.Max(
            UiReadinessRequiredStableSamples,
            uiReadinessMaximumAttempts);
        _deferCleanup = deferCleanup ?? Task.Delay;
    }

    public void Schedule(
        MemoryCleanupReason reason,
        Task? visualReadyTask = null)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        var cleanupPlan =
            MemoryCleanupPolicy.CreateDeferredPlan(reason, animationDuration);
        if (cleanupPlan.CollectionMode == MemoryCleanupCollectionMode.None)
            return;

        ScheduleCore(
            reason,
            cleanupPlan,
            visualReadyTask: visualReadyTask);
    }

    public void SchedulePreview(MemoryCleanupReason reason)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        var cleanupPlan =
            MemoryCleanupPolicy.CreateDeferredPlan(reason, animationDuration);
        if (cleanupPlan.CollectionMode == MemoryCleanupCollectionMode.None)
            return;

        var cleanupCts = ReplaceCancellationSource(ref _previewCleanupCts);
        var cleanupToken = cleanupCts.Token;
        var cleanupVersion = Interlocked.Increment(ref _previewCleanupVersion);

        _ = Task.Run(async () =>
        {
            try
            {
                await WaitForRenderPassesAsync(cleanupToken);
                if (cleanupVersion != Volatile.Read(ref _previewCleanupVersion))
                    return;

                cleanupToken.ThrowIfCancellationRequested();
                ScheduleCore(
                    reason,
                    cleanupPlan,
                    cleanupToken);
            }
            catch (OperationCanceledException)
            {
                // A newer preview render superseded this cleanup request.
            }
            finally
            {
                DisposeIfCurrent(ref _previewCleanupCts, cleanupCts);
            }
        });
    }

    public void CancelBackground() =>
        CancelBackgroundCleanup();

    public void CancelPreview()
    {
        Interlocked.Increment(ref _previewCleanupVersion);
        CancelAndDispose(ref _previewCleanupCts);
    }

    public void RunImmediate(bool compactLargeObjectHeap)
    {
        CollectAndTrim(
            compactLargeObjectHeap
                ? MemoryCleanupCollectionMode.Aggressive
                : MemoryCleanupCollectionMode.Background);
    }

    public void CancelAll()
    {
        Interlocked.Increment(ref _previewCleanupVersion);
        CancelAndDispose(ref _previewCleanupCts);
        CancelBackgroundCleanup();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        CancelAll();
    }

    private void ScheduleCore(
        MemoryCleanupReason reason,
        MemoryCleanupPlan cleanupPlan,
        CancellationToken triggerCancellationToken = default,
        Task? visualReadyTask = null)
    {
        if (!TryScheduleBackgroundCleanup(
                cleanupPlan.CollectionMode,
                triggerCancellationToken,
                out var cleanupCts))
        {
            return;
        }

        sessionMetrics.RecordMemoryCleanupScheduled(reason);
        var cleanupToken = cleanupCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                var scaledDelay = UiTimingProfile.Scale(cleanupPlan.Delay);
                var deferredCleanupTask = scaledDelay > TimeSpan.Zero
                    ? _deferCleanup(scaledDelay, cleanupToken)
                    : Task.CompletedTask;
                var visualReadyWaitTask = visualReadyTask is not null
                    ? visualReadyTask.WaitAsync(cleanupToken)
                    : Task.CompletedTask;
                await Task.WhenAll(
                    deferredCleanupTask,
                    visualReadyWaitTask);

                // Keep both gates: the delay limits cleanup frequency, while visualReadyTask can
                // include compositor settling that a timer cannot infer. Running them concurrently
                // preserves the earliest safe cleanup time without weakening either condition.
                if (cleanupPlan.WaitForUiSettled &&
                    !await WaitForUiReadyAsync(cleanupToken))
                {
                    return;
                }

                cleanupToken.ThrowIfCancellationRequested();
                var stopwatch = Stopwatch.StartNew();
                Interlocked.Exchange(ref _cleanupInProgress, 1);
                try
                {
                    var snapshotBeforeCollection = _captureMemorySnapshot();
                    CollectAndTrim(cleanupPlan.CollectionMode);
                    if (cleanupPlan.CollectionMode ==
                        MemoryCleanupCollectionMode.Background)
                    {
                        await RunAggressiveCleanupIfStillMateriallyFragmentedAsync(
                            snapshotBeforeCollection.CollectionIndex,
                            cleanupPlan.WaitForUiSettled,
                            cleanupToken);
                    }

                    sessionMetrics.RecordMemoryCleanupCompleted(
                        reason,
                        stopwatch.Elapsed);
                }
                finally
                {
                    Interlocked.Exchange(ref _cleanupInProgress, 0);
                }
            }
            catch (OperationCanceledException)
            {
                // A newer interaction superseded this cleanup run.
            }
            finally
            {
                DisposeBackgroundCleanupIfCurrent(cleanupCts);
            }
        });
    }

    private bool TryScheduleBackgroundCleanup(
        MemoryCleanupCollectionMode collectionMode,
        CancellationToken triggerCancellationToken,
        out CancellationTokenSource cleanupCts)
    {
        CancellationTokenSource? previous;
        lock (_backgroundCleanupGate)
        {
            if (_backgroundCleanupCts is not null &&
                _backgroundCleanupMode > collectionMode)
            {
                cleanupCts = null!;
                return false;
            }

            cleanupCts = triggerCancellationToken.CanBeCanceled
                ? CancellationTokenSource.CreateLinkedTokenSource(
                    triggerCancellationToken)
                : new CancellationTokenSource();
            previous = _backgroundCleanupCts;
            _backgroundCleanupCts = cleanupCts;
            _backgroundCleanupMode = collectionMode;
        }

        CancelAndDispose(previous);
        return true;
    }

    private void CancelBackgroundCleanup()
    {
        CancellationTokenSource? cleanupCts;
        lock (_backgroundCleanupGate)
        {
            cleanupCts = _backgroundCleanupCts;
            _backgroundCleanupCts = null;
            _backgroundCleanupMode = MemoryCleanupCollectionMode.None;
        }

        CancelAndDispose(cleanupCts);
    }

    private void DisposeBackgroundCleanupIfCurrent(
        CancellationTokenSource cleanupCts)
    {
        lock (_backgroundCleanupGate)
        {
            if (!ReferenceEquals(_backgroundCleanupCts, cleanupCts))
                return;

            _backgroundCleanupCts = null;
            _backgroundCleanupMode = MemoryCleanupCollectionMode.None;
        }

        cleanupCts.Dispose();
    }

    private async Task RunAggressiveCleanupIfStillMateriallyFragmentedAsync(
        long collectionIndexBeforeCleanup,
        bool waitForUiSettled,
        CancellationToken cancellationToken)
    {
        if (collectionIndexBeforeCleanup <= 0)
            return;

        var snapshot = await WaitForBackgroundCollectionAsync(
            collectionIndexBeforeCleanup,
            cancellationToken);
        if (snapshot is null ||
            !MemoryCleanupPolicy.ShouldCompactAfterBackgroundCollection(
                snapshot.Value))
        {
            return;
        }

        var scaledDelay = UiTimingProfile.Scale(AggressiveCleanupIdleDelay);
        if (scaledDelay > TimeSpan.Zero)
            await Task.Delay(scaledDelay, cancellationToken);

        if (waitForUiSettled &&
            !await WaitForUiReadyAsync(cancellationToken))
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        snapshot = _captureMemorySnapshot();
        if (!MemoryCleanupPolicy.ShouldCompactAfterBackgroundCollection(
                snapshot.Value))
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Aggressive cleanup is reserved for severe fragmentation left after background
        // reclamation. Any new interaction cancels this decommit-heavy second phase.
        CollectAndTrim(MemoryCleanupCollectionMode.Aggressive);
    }

    private async Task<MemoryCleanupSnapshot?> WaitForBackgroundCollectionAsync(
        long collectionIndexBeforeCleanup,
        CancellationToken cancellationToken)
    {
        var timeout = UiTimingProfile.Scale(
            BackgroundCollectionObservationTimeout);
        var pollInterval = UiTimingProfile.Scale(
            BackgroundCollectionPollInterval);
        var stopwatch = Stopwatch.StartNew();

        while (stopwatch.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = _captureMemorySnapshot();
            if (snapshot.CollectionIndex > collectionIndexBeforeCleanup)
                return snapshot;

            await Task.Delay(pollInterval, cancellationToken);
        }

        return null;
    }

    private async Task<bool> WaitForUiReadyAsync(
        CancellationToken cancellationToken)
    {
        var timeout = UiTimingProfile.Scale(_uiReadinessTimeout);
        var pollInterval = UiTimingProfile.Scale(
            _uiReadinessPollInterval);

        // The deadline bounds an unavailable dispatcher; the attempt budget
        // bounds a responsive UI whose readiness state keeps oscillating.
        using var deadlineCts =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
        deadlineCts.CancelAfter(timeout);
        var readinessToken = deadlineCts.Token;
        var stableSamples = 0;

        try
        {
            for (var attempt = 0;
                 attempt < _uiReadinessMaximumAttempts;
                 attempt++)
            {
                readinessToken.ThrowIfCancellationRequested();
                var isUiReady = await Dispatcher.UIThread.InvokeAsync(
                    uiReady,
                    DispatcherPriority.Background,
                    readinessToken);
                stableSamples = isUiReady ? stableSamples + 1 : 0;
                if (stableSamples >= UiReadinessRequiredStableSamples)
                    return true;

                await Task.Delay(pollInterval, readinessToken);
            }
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return false;
    }

    private static async Task WaitForRenderPassesAsync(
        CancellationToken cancellationToken)
    {
        await Dispatcher.UIThread.InvokeAsync(
            static () => { },
            DispatcherPriority.Render);
        cancellationToken.ThrowIfCancellationRequested();

        await Dispatcher.UIThread.InvokeAsync(
            static () => { },
            DispatcherPriority.Render);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static void CollectMemory(MemoryCleanupCollectionMode mode)
    {
        if (mode == MemoryCleanupCollectionMode.None)
            return;

        if (mode == MemoryCleanupCollectionMode.Aggressive)
        {
            GCSettings.LargeObjectHeapCompactionMode =
                GCLargeObjectHeapCompactionMode.CompactOnce;

            // Released project/search/preview graphs are known garbage. Aggressive mode asks
            // CoreCLR to compact the LOH and decommit their empty segments instead of keeping
            // a workload-sized reservation until an unrelated future collection.
            GC.Collect(
                generation: GC.MaxGeneration,
                GCCollectionMode.Aggressive,
                blocking: true,
                compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(
                generation: GC.MaxGeneration,
                GCCollectionMode.Aggressive,
                blocking: true,
                compacting: true);
            return;
        }

        GC.Collect(
            generation: 2,
            GCCollectionMode.Forced,
            blocking: false,
            compacting: false);
    }

    private void CollectAndTrim(MemoryCleanupCollectionMode mode)
    {
        _collect(mode);
        if (mode == MemoryCleanupCollectionMode.Aggressive)
            _trimWorkingSet();
    }

    private static CancellationTokenSource ReplaceCancellationSource(
        ref CancellationTokenSource? target,
        CancellationToken linkedToken = default)
    {
        var next = linkedToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(linkedToken)
            : new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref target, next);
        CancelAndDispose(previous);
        return next;
    }

    private static void DisposeIfCurrent(
        ref CancellationTokenSource? target,
        CancellationTokenSource candidate)
    {
        if (ReferenceEquals(
                Interlocked.CompareExchange(ref target, null, candidate),
                candidate))
        {
            candidate.Dispose();
        }
    }

    private static void CancelAndDispose(
        ref CancellationTokenSource? target)
        => CancelAndDispose(Interlocked.Exchange(ref target, null));

    private static void CancelAndDispose(CancellationTokenSource? source)
    {
        if (source is null)
            return;

        try
        {
            source.Cancel();
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        source.Dispose();
    }
}
