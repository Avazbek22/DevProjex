using DevProjex.Avalonia.Services;

namespace DevProjex.Avalonia.Coordinators;

internal readonly record struct PreviewRefreshOperation(
    Task FirstContentReady,
    Task Completion);

internal sealed class PreviewWorkspacePipeline(
    IPreviewWorkspacePipelineHost host,
    TimeSpan debounceInterval) : IDisposable
{
    private CancellationTokenSource? _previewBuildCts;
    private FirstContentReadySignal? _firstContentReady;
    private PreviewBuildStatusOperation? _activeBuildStatusOperation;
    private DispatcherTimer? _previewDebounceTimer;
    private PreviewCacheKeyData? _cachedPreviewKey;
    private long _requestedRefreshVersion;
    private long _completedRefreshVersion;
    private int _buildVersion;

    public bool IsIdle => !host.IsPreviewModeSwitchInProgress &&
                          !IsRefreshRequested;

    public bool IsRefreshRequested =>
        Volatile.Read(ref _completedRefreshVersion) <
        Volatile.Read(ref _requestedRefreshVersion);

    public void ScheduleRefresh(bool immediate = false)
    {
        var requestVersion = RequestRefresh();

        if (!host.ViewModel.IsProjectLoaded || !host.ViewModel.IsAnyPreviewVisible)
            return;

        if (immediate)
        {
            _previewDebounceTimer?.Stop();
            _ = RefreshAsync(
                allowDuringModeSwitch: false,
                requestVersion,
                Volatile.Read(ref _firstContentReady),
                cancelSignalOnEarlyExit: false);
            return;
        }

        EnsureDebounceTimer();
        _previewDebounceTimer!.Stop();
        _previewDebounceTimer.Start();
    }

    public PreviewRefreshOperation RefreshNowAsync(bool allowDuringModeSwitch = false)
    {
        var requestVersion = RequestRefresh();
        var firstContentReady = new FirstContentReadySignal();
        var previousFirstContentReady = Interlocked.Exchange(
            ref _firstContentReady,
            firstContentReady);
        previousFirstContentReady?.Cancel();
        var completion = RefreshAsync(
            allowDuringModeSwitch,
            requestVersion,
            firstContentReady,
            cancelSignalOnEarlyExit: true);
        return new PreviewRefreshOperation(
            firstContentReady.Ready,
            completion);
    }

    public void CancelRefresh()
    {
        CompleteRefreshRequest(RequestRefresh());
        _previewDebounceTimer?.Stop();
        Interlocked.Increment(ref _buildVersion);
        _previewBuildCts?.Cancel();
        Interlocked.Exchange(ref _firstContentReady, null)?.Cancel();
        CompleteActiveBuildStatusOperation();
        host.ViewModel.IsPreviewLoading = false;
    }

    public void CancelActiveBuildAndInvalidate() => CancelRefresh();

    public void CancelActiveBuild() => CancelRefresh();

    public bool SuspendForTreeHide()
    {
        var shouldResume = IsRefreshRequested || host.ViewModel.IsPreviewLoading;
        CancelRefresh();
        return shouldResume;
    }

    public bool IsCurrentCacheHit(PreviewCacheKeyData key, IPreviewTextDocument? currentDocument) =>
        _cachedPreviewKey == key && currentDocument is not null;

    public void CachePreview(PreviewCacheKeyData key) => _cachedPreviewKey = key;

    public void InvalidateCache() => _cachedPreviewKey = null;

    public Task RefreshAsync(bool allowDuringModeSwitch = false) =>
        RefreshAsync(
            allowDuringModeSwitch,
            Volatile.Read(ref _requestedRefreshVersion),
            Volatile.Read(ref _firstContentReady),
            cancelSignalOnEarlyExit: false);

    private async Task RefreshAsync(
        bool allowDuringModeSwitch,
        long requestVersion,
        FirstContentReadySignal? firstContentReady,
        bool cancelSignalOnEarlyExit)
    {
        if (!IsCurrentRefreshRequest(requestVersion) ||
            !host.ViewModel.IsProjectLoaded ||
            !host.ViewModel.IsAnyPreviewVisible)
        {
            if (cancelSignalOnEarlyExit)
                CancelTrackedFirstContentReady(firstContentReady);
            return;
        }
        if (host.IsPreviewModeSwitchInProgress && !allowDuringModeSwitch)
        {
            if (cancelSignalOnEarlyExit)
                CancelTrackedFirstContentReady(firstContentReady);
            return;
        }

        if (!host.EnsurePreviewTreeReady())
        {
            var noDataBuildVersion = BeginBuildGeneration(firstContentReady);
            _previewBuildCts?.Cancel();
            CompleteActiveBuildStatusOperation();
            host.ApplyPreviewNoDataText();
            CompleteFirstContentReady(firstContentReady, noDataBuildVersion);
            CompleteRefreshRequest(requestVersion);
            host.ViewModel.IsPreviewLoading = false;
            host.SchedulePreviewMemoryCleanup();
            return;
        }

        var buildVersion = BeginBuildGeneration(firstContentReady);
        var previewCts = ReplaceCancellationSource(ref _previewBuildCts);
        var cancellationToken = previewCts.Token;
        CompleteActiveBuildStatusOperation();
        if (!IsCurrentBuild(buildVersion) ||
            !IsCurrentRefreshRequest(requestVersion))
        {
            previewCts.Cancel();
            CancelFirstContentReady(
                firstContentReady,
                buildVersion,
                cancellationToken);
            DisposeIfCurrent(ref _previewBuildCts, previewCts);
            return;
        }

        host.ViewModel.IsPreviewLoading = true;

        // The pipeline owns cancellation/version gates; the host owns UI-specific document work.
        var statusOperation = new PreviewBuildStatusOperation(
            host,
            host.BeginPreviewBuildOperation(previewCts));
        Interlocked.Exchange(
                ref _activeBuildStatusOperation,
                statusOperation)
            ?.Complete();

        if (!IsCurrentBuild(buildVersion) ||
            !IsCurrentRefreshRequest(requestVersion))
        {
            previewCts.Cancel();
            CompleteBuildStatusOperation(statusOperation);
            CancelFirstContentReady(
                firstContentReady,
                buildVersion,
                cancellationToken);
            DisposeIfCurrent(ref _previewBuildCts, previewCts);
            return;
        }

        try
        {
            var input = host.CapturePreviewRefreshInput();
            if (host.IsCurrentPreviewCacheHit(input.CacheKey) &&
                host.CurrentPreviewDocument is { } currentPreviewDocument)
            {
                if (IsCurrentBuild(buildVersion) &&
                    IsCurrentRefreshRequest(requestVersion))
                {
                    host.ApplyPreviewDocument(currentPreviewDocument);
                    CompleteFirstContentReady(firstContentReady, buildVersion);
                    CompleteRefreshRequest(requestVersion);
                }

                return;
            }

            var warmupSnapshot = await host.TryBuildPreviewWarmupSnapshotAsync(input, cancellationToken);
            if (warmupSnapshot is { } warmup &&
                IsCurrentBuild(buildVersion) &&
                IsCurrentRefreshRequest(requestVersion))
            {
                host.ApplyPreviewText(warmup.Text, warmup.LineCount);
                CompleteFirstContentReady(firstContentReady, buildVersion);
            }

            var previewResult = await Task.Run(
                () => host.BuildPreviewDocument(input, cancellationToken),
                cancellationToken);

            if (!IsCurrentBuild(buildVersion) ||
                !IsCurrentRefreshRequest(requestVersion))
            {
                previewResult.Document.Dispose();
                return;
            }

            CachePreview(input.CacheKey);
            host.ApplyPreviewDocument(previewResult.Document);
            CompleteFirstContentReady(firstContentReady, buildVersion);
            CompleteRefreshRequest(requestVersion);
            host.SchedulePreviewRebuildMemoryCleanup();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A newer preview build or user cancellation superseded this build.
            CancelFirstContentReady(
                firstContentReady,
                buildVersion,
                cancellationToken);
        }
        catch (Exception ex)
        {
            if (IsCurrentBuild(buildVersion) &&
                IsCurrentRefreshRequest(requestVersion))
            {
                InvalidateCache();
                host.ApplyPreviewText(ex.Message);
                CompleteFirstContentReady(firstContentReady, buildVersion);
                CompleteRefreshRequest(requestVersion);
                host.SchedulePreviewMemoryCleanup();
            }
            else
            {
                CancelFirstContentReady(
                    firstContentReady,
                    buildVersion,
                    cancellationToken);
            }
        }
        finally
        {
            if (IsCurrentRefreshRequest(requestVersion))
            {
                CancelFirstContentReady(
                    firstContentReady,
                    buildVersion,
                    cancellationToken);
            }

            if (IsCurrentBuild(buildVersion) &&
                IsLatestRefreshRequest(requestVersion))
                host.ViewModel.IsPreviewLoading = false;

            CompleteBuildStatusOperation(statusOperation);
            DisposeIfCurrent(ref _previewBuildCts, previewCts);
        }
    }

    public void Dispose()
    {
        CancelRefresh();
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

    private int BeginBuildGeneration(
        FirstContentReadySignal? firstContentReady)
    {
        var buildVersion = Interlocked.Increment(ref _buildVersion);
        firstContentReady?.TransferTo(buildVersion);
        return buildVersion;
    }

    private void CancelTrackedFirstContentReady(
        FirstContentReadySignal? firstContentReady)
    {
        if (firstContentReady is null)
            return;

        if (ReferenceEquals(
                Interlocked.CompareExchange(
                    ref _firstContentReady,
                    null,
                    firstContentReady),
                firstContentReady))
        {
            firstContentReady.Cancel();
        }
    }

    private void CancelFirstContentReady(
        FirstContentReadySignal? firstContentReady,
        int buildVersion,
        CancellationToken cancellationToken)
    {
        if (firstContentReady is null ||
            !firstContentReady.TryCancel(buildVersion, cancellationToken))
        {
            return;
        }

        Interlocked.CompareExchange(
            ref _firstContentReady,
            null,
            firstContentReady);
    }

    private void CompleteFirstContentReady(
        FirstContentReadySignal? firstContentReady,
        int buildVersion)
    {
        if (firstContentReady is null ||
            !firstContentReady.TryComplete(buildVersion))
        {
            return;
        }

        Interlocked.CompareExchange(
            ref _firstContentReady,
            null,
            firstContentReady);
    }

    private void CompleteActiveBuildStatusOperation() =>
        Interlocked.Exchange(
                ref _activeBuildStatusOperation,
                null)
            ?.Complete();

    private void CompleteBuildStatusOperation(
        PreviewBuildStatusOperation statusOperation)
    {
        Interlocked.CompareExchange(
            ref _activeBuildStatusOperation,
            null,
            statusOperation);
        statusOperation.Complete();
    }

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
        _ = RefreshAsync(
            allowDuringModeSwitch: false,
            Volatile.Read(ref _requestedRefreshVersion),
            Volatile.Read(ref _firstContentReady),
            cancelSignalOnEarlyExit: false);
    }

    private long RequestRefresh() =>
        Interlocked.Increment(ref _requestedRefreshVersion);

    private bool IsCurrentRefreshRequest(long requestVersion) =>
        IsLatestRefreshRequest(requestVersion) &&
        requestVersion > Volatile.Read(ref _completedRefreshVersion);

    private bool IsLatestRefreshRequest(long requestVersion) =>
        requestVersion == Volatile.Read(ref _requestedRefreshVersion);

    private void CompleteRefreshRequest(long requestVersion)
    {
        var completedVersion = Volatile.Read(ref _completedRefreshVersion);
        while (requestVersion > completedVersion)
        {
            var observed = Interlocked.CompareExchange(
                ref _completedRefreshVersion,
                requestVersion,
                completedVersion);
            if (observed == completedVersion)
                return;

            completedVersion = observed;
        }
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

    private sealed class PreviewBuildStatusOperation(
        IPreviewWorkspacePipelineHost host,
        long operationId)
    {
        private int _completed;

        public void Complete()
        {
            if (Interlocked.Exchange(ref _completed, 1) != 0)
                return;

            host.CompletePreviewBuildOperation(operationId);
        }
    }

    private sealed class FirstContentReadySignal
    {
        private readonly object _sync = new();
        private readonly TaskCompletionSource _source = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _ownerBuildVersion;
        private bool _settled;

        public Task Ready => _source.Task;

        public void TransferTo(int buildVersion)
        {
            lock (_sync)
            {
                if (!_settled)
                    _ownerBuildVersion = buildVersion;
            }
        }

        public bool TryComplete(int buildVersion)
        {
            lock (_sync)
            {
                if (_settled || _ownerBuildVersion != buildVersion)
                    return false;

                _settled = true;
            }

            _source.TrySetResult();
            return true;
        }

        public bool TryCancel(
            int buildVersion,
            CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                if (_settled || _ownerBuildVersion != buildVersion)
                    return false;

                _settled = true;
            }

            _source.TrySetCanceled(cancellationToken);
            return true;
        }

        public void Cancel()
        {
            lock (_sync)
            {
                if (_settled)
                    return;

                _settled = true;
            }

            _source.TrySetCanceled(
                new CancellationToken(canceled: true));
        }
    }
}
