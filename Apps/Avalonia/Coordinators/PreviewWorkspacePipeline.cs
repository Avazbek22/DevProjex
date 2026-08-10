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
                cancelSignalOnEarlyExit: false,
                publicationReady: null,
                deferPresentationUntilPublication: false);
            return;
        }

        EnsureDebounceTimer();
        _previewDebounceTimer!.Stop();
        _previewDebounceTimer.Start();
    }

    public PreviewRefreshOperation RefreshNowAsync(
        bool allowDuringModeSwitch = false,
        Task? publicationReady = null,
        bool deferPresentationUntilPublication = false)
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
            cancelSignalOnEarlyExit: true,
            publicationReady,
            deferPresentationUntilPublication);
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
            cancelSignalOnEarlyExit: false,
            publicationReady: null,
            deferPresentationUntilPublication: false);

    private async Task RefreshAsync(
        bool allowDuringModeSwitch,
        long requestVersion,
        FirstContentReadySignal? firstContentReady,
        bool cancelSignalOnEarlyExit,
        Task? publicationReady,
        bool deferPresentationUntilPublication)
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
            var noDataCts = ReplaceCancellationSource(ref _previewBuildCts);
            var noDataCancellationToken = noDataCts.Token;
            CompleteActiveBuildStatusOperation();
            try
            {
                await WaitForPublicationAsync(
                    publicationReady,
                    noDataCancellationToken);
                if (!IsCurrentBuild(noDataBuildVersion) ||
                    !IsCurrentRefreshRequest(requestVersion))
                {
                    CancelFirstContentReady(
                        firstContentReady,
                        noDataBuildVersion,
                        noDataCancellationToken);
                    return;
                }

                host.ApplyPreviewNoDataText();
                CompleteFirstContentReady(
                    firstContentReady,
                    noDataBuildVersion);
                CompleteRefreshRequest(requestVersion);
                host.SchedulePreviewMemoryCleanup();
            }
            catch (OperationCanceledException)
                when (noDataCancellationToken.IsCancellationRequested)
            {
                CancelFirstContentReady(
                    firstContentReady,
                    noDataBuildVersion,
                    noDataCancellationToken);
            }
            finally
            {
                if (IsCurrentBuild(noDataBuildVersion) &&
                    IsLatestRefreshRequest(requestVersion))
                {
                    host.ViewModel.IsPreviewLoading = false;
                }

                DisposeIfCurrent(ref _previewBuildCts, noDataCts);
            }
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

        var presentationDeferred =
            deferPresentationUntilPublication && publicationReady is not null;
        PreviewBuildStatusOperation? statusOperation = presentationDeferred
            ? null
            : BeginBuildPresentation(previewCts);

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

        Task<PreviewBuildResult>? previewBuildTask = null;
        IPreviewTextDocument? unpublishedDocument = null;
        var buildResultObserved = false;
        try
        {
            var input = host.CapturePreviewRefreshInput();
            if (host.IsCurrentPreviewCacheHit(input.CacheKey) &&
                host.CurrentPreviewDocument is { } currentPreviewDocument)
            {
                await WaitForPublicationAsync(
                    publicationReady,
                    cancellationToken);
                if (IsCurrentBuild(buildVersion) &&
                    IsCurrentRefreshRequest(requestVersion))
                {
                    host.ApplyPreviewDocument(currentPreviewDocument);
                    CompleteFirstContentReady(firstContentReady, buildVersion);
                    CompleteRefreshRequest(requestVersion);
                }

                return;
            }

            var warmupTask = host.TryBuildPreviewWarmupSnapshotAsync(input, cancellationToken);
            previewBuildTask = Task.Run(
                () => host.BuildPreviewDocument(input, cancellationToken),
                cancellationToken);
            var warmupSnapshot = await warmupTask;

            if (publicationReady is null &&
                warmupSnapshot is { } immediateWarmup &&
                IsCurrentBuild(buildVersion) &&
                IsCurrentRefreshRequest(requestVersion))
            {
                host.ApplyPreviewText(
                    immediateWarmup.Text,
                    immediateWarmup.LineCount);
                CompleteFirstContentReady(firstContentReady, buildVersion);
            }

            await WaitForPublicationAsync(
                publicationReady,
                cancellationToken);

            if (presentationDeferred && !previewBuildTask.IsCompleted)
                statusOperation = BeginBuildPresentation(previewCts);

            if (publicationReady is not null &&
                !previewBuildTask.IsCompleted &&
                warmupSnapshot is { } deferredWarmup &&
                IsCurrentBuild(buildVersion) &&
                IsCurrentRefreshRequest(requestVersion))
            {
                host.ApplyPreviewText(
                    deferredWarmup.Text,
                    deferredWarmup.LineCount);
                CompleteFirstContentReady(firstContentReady, buildVersion);
            }

            var previewResult = await previewBuildTask;
            buildResultObserved = true;
            unpublishedDocument = previewResult.Document;

            if (!IsCurrentBuild(buildVersion) ||
                !IsCurrentRefreshRequest(requestVersion))
            {
                return;
            }

            CachePreview(input.CacheKey);
            host.ApplyPreviewDocument(previewResult.Document);
            unpublishedDocument = null;
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
            await WaitForPublicationAsync(
                publicationReady,
                cancellationToken);
            if (IsCurrentBuild(buildVersion) &&
                IsCurrentRefreshRequest(requestVersion))
            {
                InvalidateCache();
                host.ApplyPreviewText(host.ResolvePreviewErrorMessage(ex));
                host.HandlePreviewBuildFailure(ex);
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
            unpublishedDocument?.Dispose();
            if (previewBuildTask is not null && !buildResultObserved)
                _ = DisposeUnpublishedResultAsync(previewBuildTask);

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

    private PreviewBuildStatusOperation BeginBuildPresentation(
        CancellationTokenSource previewCts)
    {
        host.ViewModel.IsPreviewLoading = true;
        var statusOperation = new PreviewBuildStatusOperation(
            host,
            host.BeginPreviewBuildOperation(previewCts));
        Interlocked.Exchange(
                ref _activeBuildStatusOperation,
                statusOperation)
            ?.Complete();
        return statusOperation;
    }

    private void CompleteBuildStatusOperation(
        PreviewBuildStatusOperation? statusOperation)
    {
        if (statusOperation is null)
            return;

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
            cancelSignalOnEarlyExit: false,
            publicationReady: null,
            deferPresentationUntilPublication: false);
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

    private static Task WaitForPublicationAsync(
        Task? publicationReady,
        CancellationToken cancellationToken) =>
        publicationReady is null
            ? Task.CompletedTask
            : publicationReady.WaitAsync(cancellationToken);

    private static async Task DisposeUnpublishedResultAsync(
        Task<PreviewBuildResult> previewBuildTask)
    {
        try
        {
            var previewResult = await previewBuildTask.ConfigureAwait(false);
            previewResult.Document.Dispose();
        }
        catch (OperationCanceledException)
        {
            // Cancellation means no document was produced.
        }
        catch (Exception)
        {
            // A stale build failure is observed here after its owner was canceled.
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
