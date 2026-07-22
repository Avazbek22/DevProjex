namespace DevProjex.Avalonia;

public partial class MainWindow
{
    private void CancelAndDisposeWindowOperations()
    {
        CancelAndDispose(ref _windowLifetimeCts);
        _metrics.Dispose();
        _projectLoadPipeline.Dispose();
        _previewPipeline.Dispose();
        _refreshPipeline.Dispose();
        StopMetricsDebounceTimers();

        CancelAndDispose(ref _previewSelectionMetricsCts);
        CancelAndDispose(ref _previewMemoryCleanupCts);
        CancelAndDispose(ref _searchMemoryCleanupCts);
        CancelAndDispose(ref _backgroundMemoryCleanupCts);
        CancelAndDispose(ref _previewModeSwitchCts);

        CancelAndDispose(ref _projectOperationCts);
        CancelAndDispose(ref _applySettingsCts);
        CancelAndDispose(ref _gitCloneCts);
        CancelAndDispose(ref _gitOperationCts);
        CancelAndDispose(ref _projectCopyExportCts);
    }

    private void StopMetricsDebounceTimers()
    {
        if (_previewSelectionMetricsDebounceTimer is not null)
        {
            _previewSelectionMetricsDebounceTimer.Stop();
            _previewSelectionMetricsDebounceTimer.Tick -= OnPreviewSelectionMetricsDebounceTick;
            _previewSelectionMetricsDebounceTimer = null;
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
