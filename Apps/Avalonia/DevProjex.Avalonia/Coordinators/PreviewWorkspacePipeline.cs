using DevProjex.Avalonia.Services;

namespace DevProjex.Avalonia.Coordinators;

internal sealed class PreviewWorkspacePipeline(
    IPreviewWorkspacePipelineHost host,
    TimeSpan debounceInterval) : IDisposable
{
    private CancellationTokenSource? _previewBuildCts;
    private DispatcherTimer? _previewDebounceTimer;
    private PreviewCacheKeyData? _cachedPreviewKey;
    private volatile bool _refreshRequested;
    private bool _clearBeforeNextRefresh;
    private int _buildVersion;

    public bool IsIdle => !host.IsPreviewModeSwitchInProgress &&
                          !_refreshRequested &&
                          !_clearBeforeNextRefresh;

    public bool IsRefreshRequested => _refreshRequested;

    public bool ShouldClearBeforeNextRefresh => _clearBeforeNextRefresh;

    public void ScheduleRefresh(bool immediate = false, TimeSpan? debounceOverride = null)
    {
        _refreshRequested = true;

        if (!host.ViewModel.IsProjectLoaded || !host.ViewModel.IsAnyPreviewVisible)
            return;

        if (immediate)
        {
            _previewDebounceTimer?.Stop();
            _ = RefreshAsync();
            return;
        }

        EnsureDebounceTimer();
        _previewDebounceTimer!.Stop();
        // Rebuilding the preview reads every selected file. Bulk selection changes ("select all")
        // pass a longer coalescing window so the heavy build waits for the selection to settle,
        // instead of firing on the default incremental-edit debounce.
        _previewDebounceTimer.Interval = debounceOverride is { } overrideInterval && overrideInterval > debounceInterval
            ? overrideInterval
            : debounceInterval;
        _previewDebounceTimer.Start();
    }

    public Task RefreshNowAsync()
    {
        _refreshRequested = true;
        return RefreshAsync();
    }

    public void CancelRefresh()
    {
        _refreshRequested = false;
        _previewDebounceTimer?.Stop();
        _previewBuildCts?.Cancel();
        _clearBeforeNextRefresh = false;
        host.ViewModel.IsPreviewLoading = false;
    }

    public void CancelActiveBuildAndInvalidate()
    {
        CancelRefresh();
        Interlocked.Increment(ref _buildVersion);
    }

    public void CancelActiveBuild()
    {
        _previewBuildCts?.Cancel();
    }

    public void MarkClearBeforeNextRefresh()
    {
        _clearBeforeNextRefresh = true;
    }

    public bool SuspendForTreeHide()
    {
        var shouldResume = _refreshRequested || host.ViewModel.IsPreviewLoading;
        CancelRefresh();
        return shouldResume;
    }

    public bool IsCurrentCacheHit(PreviewCacheKeyData key, IPreviewTextDocument? currentDocument) =>
        _cachedPreviewKey == key && currentDocument is not null;

    public void CachePreview(PreviewCacheKeyData key) => _cachedPreviewKey = key;

    public void InvalidateCache() => _cachedPreviewKey = null;

    public async Task RefreshAsync()
    {
        if (!_refreshRequested || !host.ViewModel.IsProjectLoaded || !host.ViewModel.IsAnyPreviewVisible)
            return;
        if (host.IsPreviewModeSwitchInProgress)
            return;

        if (!host.EnsurePreviewTreeReady())
        {
            host.ApplyPreviewNoDataText();
            _refreshRequested = false;
            host.SchedulePreviewMemoryCleanup(force: false);
            return;
        }

        var previewCts = ReplaceCancellationSource(ref _previewBuildCts);
        var cancellationToken = previewCts.Token;
        var buildVersion = Interlocked.Increment(ref _buildVersion);
        host.ViewModel.IsPreviewLoading = true;

        // The pipeline owns cancellation/version gates; the host owns UI-specific document work.
        var operationId = host.BeginPreviewBuildOperation(previewCts);

        try
        {
            if (_clearBeforeNextRefresh)
            {
                host.ClearPreviewDocument();
                _clearBeforeNextRefresh = false;
            }

            var input = host.CapturePreviewRefreshInput();
            if (host.IsCurrentPreviewCacheHit(input.CacheKey) &&
                host.CurrentPreviewDocument is { } currentPreviewDocument)
            {
                if (IsCurrentBuild(buildVersion))
                {
                    host.ApplyPreviewDocument(currentPreviewDocument);
                    _refreshRequested = false;
                }

                return;
            }

            var warmupSnapshot = await host.TryBuildPreviewWarmupSnapshotAsync(input, cancellationToken);
            if (warmupSnapshot is { } warmup && IsCurrentBuild(buildVersion))
                host.ApplyPreviewText(warmup.Text, warmup.LineCount);

            var previewResult = await Task.Run(
                () => host.BuildPreviewDocument(input, cancellationToken),
                cancellationToken);

            if (!IsCurrentBuild(buildVersion))
            {
                previewResult.Document.Dispose();
                return;
            }

            CachePreview(input.CacheKey);
            host.ApplyPreviewDocument(previewResult.Document);
            _refreshRequested = false;
            host.SchedulePreviewMemoryCleanupForDocument(previewResult.Document);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A newer preview build or user cancellation superseded this build.
        }
        catch (Exception ex)
        {
            if (IsCurrentBuild(buildVersion))
            {
                InvalidateCache();
                host.ApplyPreviewText(ex.Message);
                host.SchedulePreviewMemoryCleanup(force: false);
            }
        }
        finally
        {
            if (IsCurrentBuild(buildVersion))
                host.ViewModel.IsPreviewLoading = false;

            host.CompletePreviewBuildOperation(operationId);
            DisposeIfCurrent(ref _previewBuildCts, previewCts);
        }
    }

    public void Dispose()
    {
        CancelAndDispose(ref _previewBuildCts);
        if (_previewDebounceTimer is not null)
        {
            _previewDebounceTimer.Stop();
            _previewDebounceTimer.Tick -= OnPreviewDebounceTick;
            _previewDebounceTimer = null;
        }
    }

    private bool IsCurrentBuild(int buildVersion) =>
        buildVersion == Volatile.Read(ref _buildVersion);

    private void EnsureDebounceTimer()
    {
        if (_previewDebounceTimer is not null)
            return;

        _previewDebounceTimer = new DispatcherTimer
        {
            Interval = debounceInterval
        };
        _previewDebounceTimer.Tick += OnPreviewDebounceTick;
    }

    private void OnPreviewDebounceTick(object? sender, EventArgs e)
    {
        _previewDebounceTimer?.Stop();
        _ = RefreshAsync();
    }

    private static CancellationTokenSource ReplaceCancellationSource(ref CancellationTokenSource? target)
    {
        var cts = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref target, cts);
        previous?.Cancel();
        previous?.Dispose();
        return cts;
    }

    private static void DisposeIfCurrent(ref CancellationTokenSource? target, CancellationTokenSource candidate)
    {
        var current = Interlocked.CompareExchange(ref target, null, candidate);
        if (ReferenceEquals(current, candidate))
            candidate.Dispose();
    }

    private static void CancelAndDispose(ref CancellationTokenSource? source)
    {
        var current = Interlocked.Exchange(ref source, null);
        if (current is null)
            return;

        try
        {
            current.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        current.Dispose();
    }
}
