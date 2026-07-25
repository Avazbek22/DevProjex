using System.Runtime;
using System.Runtime.InteropServices;
using DevProjex.Avalonia.Services;

namespace DevProjex.Avalonia.Coordinators;

internal sealed class MemoryCleanupCoordinator(
    SessionMetricsRecorder sessionMetrics,
    Func<bool> uiReady,
    TimeSpan animationDuration)
    : IDisposable
{
    private CancellationTokenSource? _backgroundCleanupCts;
    private CancellationTokenSource? _searchCleanupCts;
    private CancellationTokenSource? _previewCleanupCts;
    private int _searchCleanupVersion;
    private int _previewCleanupVersion;
    private int _disposed;

    public void Schedule(MemoryCleanupReason reason)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        var cleanupPlan =
            MemoryCleanupPolicy.CreateDeferredPlan(reason, animationDuration);
        if (!MemoryCleanupPolicy.ShouldRun(
                cleanupPlan,
                GC.GetTotalMemory(forceFullCollection: false)))
        {
            return;
        }

        ScheduleCore(reason, cleanupPlan);
    }

    public void ScheduleSearchAfterRender()
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        var cleanupCts = ReplaceCancellationSource(ref _searchCleanupCts);
        var cleanupVersion = Interlocked.Increment(ref _searchCleanupVersion);

        _ = Task.Run(async () =>
        {
            try
            {
                await WaitForRenderPassesAsync(cleanupCts.Token);
                if (cleanupVersion != Volatile.Read(ref _searchCleanupVersion))
                    return;

                Schedule(MemoryCleanupReason.SearchClose);
            }
            catch (OperationCanceledException)
            {
                // A newer search result superseded this cleanup request.
            }
            finally
            {
                DisposeIfCurrent(ref _searchCleanupCts, cleanupCts);
            }
        });
    }

    public void SchedulePreview(bool force, MemoryCleanupReason reason)
    {
        if (!force || Volatile.Read(ref _disposed) != 0)
            return;

        var cleanupCts = ReplaceCancellationSource(ref _previewCleanupCts);
        var cleanupVersion = Interlocked.Increment(ref _previewCleanupVersion);

        _ = Task.Run(async () =>
        {
            try
            {
                await WaitForRenderPassesAsync(cleanupCts.Token);
                if (cleanupVersion != Volatile.Read(ref _previewCleanupVersion))
                    return;

                Schedule(reason);
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

    public void CancelBackground()
        => CancelAndDispose(ref _backgroundCleanupCts);

    public void CancelPreview()
    {
        Interlocked.Increment(ref _previewCleanupVersion);
        CancelAndDispose(ref _previewCleanupCts);
    }

    public void RunImmediate(bool compactLargeObjectHeap)
    {
        if (compactLargeObjectHeap)
        {
            ForceMemoryCleanup();
            return;
        }

        GC.Collect(
            generation: 2,
            GCCollectionMode.Forced,
            blocking: true);
    }

    public void CancelAll()
    {
        Interlocked.Increment(ref _searchCleanupVersion);
        Interlocked.Increment(ref _previewCleanupVersion);
        CancelAndDispose(ref _searchCleanupCts);
        CancelAndDispose(ref _previewCleanupCts);
        CancelAndDispose(ref _backgroundCleanupCts);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        CancelAll();
    }

    private void ScheduleCore(
        MemoryCleanupReason reason,
        MemoryCleanupPlan cleanupPlan)
    {
        sessionMetrics.RecordMemoryCleanupScheduled(reason);
        var cleanupCts =
            ReplaceCancellationSource(ref _backgroundCleanupCts);

        _ = Task.Run(async () =>
        {
            try
            {
                if (cleanupPlan.WaitForUiSettled)
                    await WaitForUiReadyAsync(cleanupCts.Token);

                var scaledDelay = UiTimingProfile.Scale(cleanupPlan.Delay);
                if (scaledDelay > TimeSpan.Zero)
                    await Task.Delay(scaledDelay, cleanupCts.Token);

                cleanupCts.Token.ThrowIfCancellationRequested();
                if (!MemoryCleanupPolicy.ShouldRun(
                        cleanupPlan,
                        GC.GetTotalMemory(forceFullCollection: false)))
                {
                    return;
                }

                var stopwatch = Stopwatch.StartNew();
                ForceMemoryCleanup();
                sessionMetrics.RecordMemoryCleanupCompleted(
                    reason,
                    stopwatch.Elapsed);
            }
            catch (OperationCanceledException)
            {
                // A newer interaction superseded this cleanup run.
            }
            finally
            {
                DisposeIfCurrent(ref _backgroundCleanupCts, cleanupCts);
            }
        });
    }

    private async Task WaitForUiReadyAsync(
        CancellationToken cancellationToken)
    {
        var stableSamples = 0;
        while (stableSamples < 3)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var isUiReady = await Dispatcher.UIThread.InvokeAsync(
                uiReady,
                DispatcherPriority.Background);
            stableSamples = isUiReady ? stableSamples + 1 : 0;
            await Task.Delay(
                UiTimingProfile.Scale(TimeSpan.FromMilliseconds(120)),
                cancellationToken);
        }
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

    private static void ForceMemoryCleanup()
    {
        GCSettings.LargeObjectHeapCompactionMode =
            GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(
            generation: 2,
            GCCollectionMode.Aggressive,
            blocking: true,
            compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(
            generation: 1,
            GCCollectionMode.Forced,
            blocking: false);
        TrimNativeWorkingSet();
    }

    private static void TrimNativeWorkingSet()
    {
        if (!OperatingSystem.IsWindows())
            return;

        try
        {
            using var process = Process.GetCurrentProcess();
            SetProcessWorkingSetSize(
                process.Handle,
                minWorkingSetSize: -1,
                maxWorkingSetSize: -1);
        }
        catch
        {
            // Working-set trimming is optional in sandboxed and packaged apps.
        }
    }

    private static CancellationTokenSource ReplaceCancellationSource(
        ref CancellationTokenSource? target)
    {
        var next = new CancellationTokenSource();
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

    [DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSize(
        IntPtr process,
        nint minWorkingSetSize,
        nint maxWorkingSetSize);
}
