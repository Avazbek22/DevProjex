namespace DevProjex.Avalonia;

public partial class MainWindow
{
    private void CancelAndDisposeWindowOperations()
    {
        _metrics.Dispose();
        _projectLoadPipeline.Dispose();
        StopMetricsDebounceTimers();
        CancelBackgroundMemoryCleanup();

        CancelAndDispose(ref _previewBuildCts);
        CancelAndDispose(ref _previewSelectionMetricsCts);
        CancelAndDispose(ref _previewMemoryCleanupCts);
        CancelAndDispose(ref _searchMemoryCleanupCts);
        CancelAndDispose(ref _previewModeSwitchCts);

        CancelAndDispose(ref _projectOperationCts);
        CancelAndDispose(ref _refreshCts);
        CancelAndDispose(ref _gitCloneCts);
        CancelAndDispose(ref _gitOperationCts);
    }

    private void StopMetricsDebounceTimers()
    {
        if (_previewSelectionMetricsDebounceTimer is not null)
        {
            _previewSelectionMetricsDebounceTimer.Stop();
            _previewSelectionMetricsDebounceTimer.Tick -= OnPreviewSelectionMetricsDebounceTick;
        }

        if (_previewDebounceTimer is not null)
        {
            _previewDebounceTimer.Stop();
            _previewDebounceTimer.Tick -= OnPreviewDebounceTick;
        }
    }

    private static void CancelAndDispose(ref CancellationTokenSource? source)
    {
        var current = Interlocked.Exchange(ref source, null);

        if (current is null)
            return;

        // Closing the window is the ownership boundary for all in-flight UI work.
        // Cancel before disposing so background continuations can observe shutdown.
        try
        {
            current.Cancel();
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        current.Dispose();
    }
}
