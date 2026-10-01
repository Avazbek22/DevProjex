using System.Runtime;
using DevProjex.Avalonia.Services;

namespace DevProjex.Avalonia.Coordinators;

internal sealed class MemoryCleanupCoordinator(
    SessionMetricsRecorder sessionMetrics,
    Func<bool> uiReady,
    TimeSpan animationDuration,
    Func<MemoryCleanupRetentionSnapshot>? captureMemoryRetention = null,
    BackgroundTaskRegistry? backgroundTasks = null)
    : IDisposable
{
    private static readonly TimeSpan BackgroundCollectionObservationTimeout =
        TimeSpan.FromSeconds(2);
    private static readonly TimeSpan BackgroundCollectionPollInterval =
        TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan EscalationIdleDelay =
        TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan UiReadinessTimeout =
        TimeSpan.FromSeconds(3);
    private static readonly TimeSpan UiReadinessPollInterval =
        TimeSpan.FromMilliseconds(120);
    private const int UiReadinessRequiredStableSamples = 3;
    private const int UiReadinessMaximumAttempts = 24;

    private readonly Func<MemoryCleanupSnapshot> _captureMemorySnapshot =
        MemoryCleanupSnapshot.Capture;
    private readonly MemoryCleanupTrace? _memoryCleanupTrace =
        MemoryCleanupTrace.Create(captureMemoryRetention);
    private readonly Action<MemoryCleanupCollectionMode> _collect =
        CollectMemory;
    private readonly Action _trimWorkingSet =
        ProcessWorkingSetTrimmer.TrimCurrentProcess;
    private readonly Func<TimeSpan, CancellationToken, Task> _deferCleanup =
        Task.Delay;
    private readonly Func<CancellationToken, Task> _waitForRenderPasses =
        WaitForRenderPassesAsync;
    private readonly Func<CancellationToken, Task<bool>> _queryUiReadiness =
        cancellationToken => QueryUiReadinessAsync(
            uiReady,
            cancellationToken);
	private readonly BackgroundTaskRegistry _backgroundTasks = backgroundTasks ?? new();
	private readonly bool _ownsBackgroundTasks = backgroundTasks is null;
    private readonly TimeSpan _uiReadinessTimeout = UiReadinessTimeout;
    private readonly TimeSpan _uiReadinessPollInterval =
        UiReadinessPollInterval;
    private readonly int _uiReadinessMaximumAttempts =
        UiReadinessMaximumAttempts;
    private readonly object _backgroundCleanupGate = new();
    private CancellationTokenSource? _backgroundCleanupCts;
    private MemoryCleanupCollectionMode _backgroundCleanupMode;
    private MemoryCleanupEscalationMode _backgroundEscalationMode;
    private CancellationTokenSource? _previewCleanupCts;
    private int _previewCleanupVersion;
    private int _cleanupInProgress;
    private int _disposed;

    // SchedulePreview publishes the background operation before clearing its preview gate.
    // Reading in the same direction prevents a false idle state during that handoff.
    internal bool IsCleanupPendingOrRunning =>
        Volatile.Read(ref _previewCleanupCts) is not null ||
        Volatile.Read(ref _backgroundCleanupCts) is not null ||
        Volatile.Read(ref _cleanupInProgress) != 0;

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
        Func<TimeSpan, CancellationToken, Task>? deferCleanup = null,
        Func<CancellationToken, Task>? waitForRenderPasses = null,
        Func<CancellationToken, Task<bool>>? queryUiReadiness = null,
        MemoryCleanupTrace? memoryCleanupTrace = null,
		BackgroundTaskRegistry? backgroundTasks = null)
        : this(sessionMetrics, uiReady, animationDuration, captureMemoryRetention: null, backgroundTasks)
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
        _waitForRenderPasses =
            waitForRenderPasses ?? WaitForRenderPassesAsync;
        _queryUiReadiness =
            queryUiReadiness ??
            (cancellationToken => QueryUiReadinessAsync(
                uiReady,
                cancellationToken));
        _memoryCleanupTrace = memoryCleanupTrace;
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

    internal void Schedule(
        MemoryCleanupReason reason,
        MemoryCleanupPlan cleanupPlan,
        Task? visualReadyTask = null)
    {
        if (Volatile.Read(ref _disposed) != 0 ||
            cleanupPlan.CollectionMode == MemoryCleanupCollectionMode.None)
        {
            return;
        }

        ScheduleCore(reason, cleanupPlan, visualReadyTask: visualReadyTask);
    }

    public void SchedulePreview(MemoryCleanupReason reason)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        var cleanupPlan =
            MemoryCleanupPolicy.CreateDeferredPlan(reason, animationDuration);
        if (cleanupPlan.CollectionMode == MemoryCleanupCollectionMode.None)
            return;

        var cleanupCts = ReplaceCancellationSource(
			ref _previewCleanupCts,
			_backgroundTasks.LifetimeToken);
        var cleanupToken = cleanupCts.Token;
        var cleanupVersion = Interlocked.Increment(ref _previewCleanupVersion);

		var task = Task.Run(async () =>
        {
            try
            {
                await _waitForRenderPasses(cleanupToken);
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
		_backgroundTasks.Register(task, "MemoryCleanup.SchedulePreview");
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
            reason: null,
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
		if (_ownsBackgroundTasks)
			_backgroundTasks.Dispose();
    }

    private void ScheduleCore(
        MemoryCleanupReason reason,
        MemoryCleanupPlan cleanupPlan,
        CancellationToken triggerCancellationToken = default,
        Task? visualReadyTask = null)
    {
        if (!TryScheduleBackgroundCleanup(
                cleanupPlan.CollectionMode,
                cleanupPlan.EscalationMode,
                triggerCancellationToken,
                out var cleanupCts))
        {
            return;
        }

        sessionMetrics.RecordMemoryCleanupScheduled(reason);
        var cleanupToken = cleanupCts.Token;

		var task = Task.Run(async () =>
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
					var backgroundTraceBefore = cleanupPlan.CollectionMode ==
						MemoryCleanupCollectionMode.Background
						? _memoryCleanupTrace?.Capture()
						: null;
                    CollectAndTrim(
						reason,
						cleanupPlan.CollectionMode,
						writeTrace: backgroundTraceBefore is null);
					if (cleanupPlan.CollectionMode ==
						MemoryCleanupCollectionMode.Background)
                    {
						MemoryCleanupSnapshot? observedBackgroundCollection = null;
						if (snapshotBeforeCollection.CollectionIndex > 0 &&
							(backgroundTraceBefore is not null ||
							 cleanupPlan.EscalationMode != MemoryCleanupEscalationMode.None))
						{
							observedBackgroundCollection = await WaitForBackgroundCollectionAsync(
								snapshotBeforeCollection.CollectionIndex,
								cleanupToken);
						}

						if (backgroundTraceBefore is { } capturedBefore)
						{
							_memoryCleanupTrace!.Write(
								reason,
								MemoryCleanupCollectionMode.Background,
								capturedBefore,
								_memoryCleanupTrace.Capture());
						}

						if (cleanupPlan.EscalationMode != MemoryCleanupEscalationMode.None)
						{
							await RunEscalationIfStillMateriallyFragmentedAsync(
								reason,
								observedBackgroundCollection,
								cleanupPlan.WaitForUiSettled,
								cleanupPlan.EscalationMode,
								cleanupToken);
						}
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
		_backgroundTasks.Register(task, "MemoryCleanup.ScheduleCore");
    }

    private bool TryScheduleBackgroundCleanup(
        MemoryCleanupCollectionMode collectionMode,
        MemoryCleanupEscalationMode escalationMode,
        CancellationToken triggerCancellationToken,
        out CancellationTokenSource cleanupCts)
    {
        CancellationTokenSource? previous;
        lock (_backgroundCleanupGate)
        {
            if (_backgroundCleanupCts is not null &&
                IsStrongerCleanupAlreadyScheduled(collectionMode, escalationMode))
            {
                cleanupCts = null!;
                return false;
            }

			cleanupCts = triggerCancellationToken.CanBeCanceled
				? CancellationTokenSource.CreateLinkedTokenSource(
					triggerCancellationToken,
					_backgroundTasks.LifetimeToken)
				: CancellationTokenSource.CreateLinkedTokenSource(
					_backgroundTasks.LifetimeToken);
            previous = _backgroundCleanupCts;
            _backgroundCleanupCts = cleanupCts;
            _backgroundCleanupMode = collectionMode;
            _backgroundEscalationMode = escalationMode;
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
            _backgroundEscalationMode = MemoryCleanupEscalationMode.None;
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
            _backgroundEscalationMode = MemoryCleanupEscalationMode.None;
        }

        cleanupCts.Dispose();
    }

    private bool IsStrongerCleanupAlreadyScheduled(
        MemoryCleanupCollectionMode collectionMode,
        MemoryCleanupEscalationMode escalationMode) =>
        _backgroundCleanupMode > collectionMode ||
        (_backgroundCleanupMode == collectionMode &&
         _backgroundEscalationMode > escalationMode);

    private async Task RunEscalationIfStillMateriallyFragmentedAsync(
		MemoryCleanupReason reason,
		MemoryCleanupSnapshot? observedBackgroundCollection,
        bool waitForUiSettled,
        MemoryCleanupEscalationMode escalationMode,
        CancellationToken cancellationToken)
    {
        if (observedBackgroundCollection is null ||
            !MemoryCleanupPolicy.ShouldCompactAfterBackgroundCollection(
				observedBackgroundCollection.Value))
        {
            return;
        }

        var scaledDelay = UiTimingProfile.Scale(EscalationIdleDelay);
        if (scaledDelay > TimeSpan.Zero)
            await Task.Delay(scaledDelay, cancellationToken);

        if (waitForUiSettled &&
            !await WaitForUiReadyAsync(cancellationToken))
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
		var snapshot = _captureMemorySnapshot();
        if (!MemoryCleanupPolicy.ShouldCompactAfterBackgroundCollection(
                snapshot))
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();

        // The reason selects the least disruptive follow-up that can reclaim its released graph.
        // Any new interaction still cancels this blocking second phase before it starts.
        CollectAndTrim(reason, ToCollectionMode(escalationMode));
    }

    private static MemoryCleanupCollectionMode ToCollectionMode(
        MemoryCleanupEscalationMode escalationMode) =>
        escalationMode switch
        {
            MemoryCleanupEscalationMode.Compacting =>
                MemoryCleanupCollectionMode.Compacting,
            MemoryCleanupEscalationMode.Aggressive =>
                MemoryCleanupCollectionMode.Aggressive,
            _ => MemoryCleanupCollectionMode.None
        };

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
                var isUiReady = await _queryUiReadiness(readinessToken);
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
            DispatcherPriority.Render,
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        await Dispatcher.UIThread.InvokeAsync(
            static () => { },
            DispatcherPriority.Render,
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static async Task<bool> QueryUiReadinessAsync(
        Func<bool> uiReady,
        CancellationToken cancellationToken) =>
        await Dispatcher.UIThread.InvokeAsync(
            uiReady,
            DispatcherPriority.Background,
            cancellationToken);

    private static void CollectMemory(MemoryCleanupCollectionMode mode)
    {
        if (mode == MemoryCleanupCollectionMode.None)
            return;

        if (mode is MemoryCleanupCollectionMode.Compacting or
            MemoryCleanupCollectionMode.Aggressive)
        {
            GCSettings.LargeObjectHeapCompactionMode =
                GCLargeObjectHeapCompactionMode.CompactOnce;

            if (mode == MemoryCleanupCollectionMode.Compacting)
            {
                // One forced compacting Gen2 decommits released regions without the finalizer
                // cycle, second full collection, and working-set trim that strip transition paths.
                GC.Collect(
                    generation: GC.MaxGeneration,
                    GCCollectionMode.Forced,
                    blocking: true,
                    compacting: true);
                return;
            }

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

    private void CollectAndTrim(
        MemoryCleanupReason? reason,
        MemoryCleanupCollectionMode mode,
		bool writeTrace = true)
    {
        if (mode == MemoryCleanupCollectionMode.None)
            return;

		var before = writeTrace ? _memoryCleanupTrace?.Capture() : null;
        _collect(mode);
        if (mode == MemoryCleanupCollectionMode.Aggressive)
            _trimWorkingSet();
        if (before is { } capturedBefore)
        {
            _memoryCleanupTrace!.Write(
                reason,
                mode,
                capturedBefore,
                _memoryCleanupTrace.Capture());
        }
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
