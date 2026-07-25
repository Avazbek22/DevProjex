using DevProjex.Avalonia.Coordinators;
using DevProjex.Avalonia.Services;

namespace DevProjex.Avalonia;

public partial class MainWindow
{
    #region Real-time Status Metrics

    private bool IsBackgroundMetricsActive() => _metrics.IsBackgroundActive;

    private void OnTreeNodeCheckedChanged(TreeNodeViewModel _)
    {
        _treeSelectionSnapshotCache.Invalidate();
        _metrics.ScheduleRecalculate();
        SchedulePreviewRefresh();
    }

    private void RenderPreviewSelectionMetrics()
    {
        if (!_hasPreviewSelectionMetricsSnapshot)
        {
            _viewModel.StatusPreviewSelectionVisible = false;
            _viewModel.StatusPreviewSelectionStatsText = string.Empty;
            return;
        }

        _viewModel.StatusPreviewSelectionStatsText = PreviewSelectionMetricsPolicy.FormatStatusMetricsText(
            _lastPreviewSelectionMetrics,
            BuildStatusMetricLabels(),
            useCompactMode: false);
        _viewModel.StatusPreviewSelectionVisible = true;
    }

    private StatusMetricLabels BuildStatusMetricLabels()
    {
        var linesLabel = _localization.Format("Status.Metric.Lines", "{0}");
        var charsLabel = _localization.Format("Status.Metric.Chars", "{0}");
        var tokensLabel = _localization.Format("Status.Metric.Tokens", "{0}");

        return new StatusMetricLabels(
            linesLabel.Replace("{0}", string.Empty).Trim(),
            charsLabel.Replace("{0}", string.Empty).Trim(),
            tokensLabel.Replace("{0}", string.Empty).Trim());
    }

    private void SchedulePreviewSelectionMetricsUpdate(bool immediate = false)
    {
        if (!_viewModel.IsAnyPreviewVisible || _previewTextControl is null)
        {
            ClearPreviewSelectionMetrics();
            return;
        }

        if (!_previewTextControl.TryGetSelectionRange(out _))
        {
            ClearPreviewSelectionMetrics();
            return;
        }

        if (immediate)
        {
            _previewSelectionMetricsDebounceTimer?.Stop();
            RecalculatePreviewSelectionMetricsAsync();
            return;
        }

        if (_previewSelectionMetricsDebounceTimer is null)
        {
            _previewSelectionMetricsDebounceTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
            {
                Interval = PreviewSelectionMetricsDebounceInterval
            };
            _previewSelectionMetricsDebounceTimer.Tick += OnPreviewSelectionMetricsDebounceTick;
        }

        _previewSelectionMetricsDebounceTimer.Stop();
        _previewSelectionMetricsDebounceTimer.Start();
    }

    private void OnPreviewSelectionMetricsDebounceTick(object? sender, EventArgs e)
    {
        _previewSelectionMetricsDebounceTimer?.Stop();
        RecalculatePreviewSelectionMetricsAsync();
    }

    private void RecalculatePreviewSelectionMetricsAsync()
    {
        if (!TryCapturePreviewSelectionMetricsSnapshot(out var snapshot))
        {
            ClearPreviewSelectionMetrics();
            return;
        }

        if (TryGetCachedPreviewSelectionMetrics(snapshot, out var cachedMetrics))
        {
            _previewSelectionMetricsDebounceTimer?.Stop();
            var previousCts = Interlocked.Exchange(ref _previewSelectionMetricsCts, null);
            previousCts?.Cancel();
            previousCts?.Dispose();
            Interlocked.Increment(ref _previewSelectionMetricsVersion);
            _lastPreviewSelectionMetrics = cachedMetrics;
            _hasPreviewSelectionMetricsSnapshot = true;
            RenderPreviewSelectionMetrics();
            return;
        }

        var metricsCts = ReplaceCancellationSource(ref _previewSelectionMetricsCts);
        var token = metricsCts.Token;
        var version = Interlocked.Increment(ref _previewSelectionMetricsVersion);

        _ = RecalculatePreviewSelectionMetricsCoreAsync(snapshot, metricsCts, token, version);
    }

    private async Task RecalculatePreviewSelectionMetricsCoreAsync(
        PreviewSelectionMetricsSnapshot snapshot,
        CancellationTokenSource metricsCts,
        CancellationToken cancellationToken,
        int version)
    {
        try
        {
            var metrics = await Task.Run(
                () => PreviewSelectionMetricsCalculator.Calculate(
                    snapshot.Document,
                    snapshot.SelectionRange,
                    cancellationToken),
                cancellationToken);

            if (cancellationToken.IsCancellationRequested)
                return;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (cancellationToken.IsCancellationRequested ||
                    version != Volatile.Read(ref _previewSelectionMetricsVersion))
                {
                    return;
                }

                if (!TryCapturePreviewSelectionMetricsSnapshot(out var currentSnapshot) ||
                    !ReferenceEquals(currentSnapshot.Document, snapshot.Document) ||
                    currentSnapshot.SelectionRange != snapshot.SelectionRange)
                {
                    return;
                }

                _lastPreviewSelectionMetrics = metrics;
                _hasPreviewSelectionMetricsSnapshot = metrics != ExportOutputMetrics.Empty;
                RenderPreviewSelectionMetrics();
            }, DispatcherPriority.Background);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            DisposeIfCurrent(ref _previewSelectionMetricsCts, metricsCts);
        }
    }

    private bool TryCapturePreviewSelectionMetricsSnapshot(out PreviewSelectionMetricsSnapshot snapshot)
    {
        snapshot = default;

        if (!_viewModel.IsAnyPreviewVisible || _previewTextControl is null)
            return false;

        var document = _previewTextControl.Document ?? _viewModel.PreviewDocument;
        if (document is null)
            return false;

        if (!_previewTextControl.TryGetSelectionRange(out var selectionRange))
            return false;

        snapshot = new PreviewSelectionMetricsSnapshot(document, selectionRange);
        return true;
    }

    private bool TryGetCachedPreviewSelectionMetrics(
        PreviewSelectionMetricsSnapshot snapshot,
        out ExportOutputMetrics metrics)
    {
        return _metrics.TryGetCachedPreviewSelectionMetrics(
            _viewModel.SelectedPreviewContentMode,
            snapshot.Document,
            snapshot.SelectionRange,
            out metrics);
    }

    private void ClearPreviewSelectionMetrics()
    {
        _previewSelectionMetricsDebounceTimer?.Stop();
        var previousCts = Interlocked.Exchange(ref _previewSelectionMetricsCts, null);
        previousCts?.Cancel();
        previousCts?.Dispose();
        Interlocked.Increment(ref _previewSelectionMetricsVersion);

        _lastPreviewSelectionMetrics = ExportOutputMetrics.Empty;
        _hasPreviewSelectionMetricsSnapshot = false;
        _viewModel.StatusPreviewSelectionVisible = false;
        _viewModel.StatusPreviewSelectionStatsText = string.Empty;
    }

    private void OnStatusOperationCancelRequested(object? sender, RoutedEventArgs e)
    {
        var activeOperation = _statusOperations.GetActiveSnapshot();
        var activeOperationId = activeOperation.OperationId;
        var activeOperationType = activeOperation.OperationType;

        // Primary cancellation path for the currently visible status operation.
        try
        {
            activeOperation.CancelAction?.Invoke();
        }
        catch
        {
            // Ignore cancellation callback errors and continue with fallback logic.
        }

        // Scoped fallback path: cancel only the currently active operation family.
        switch (activeOperationType)
        {
            case StatusOperationType.LoadProject:
                _projectLoadPipeline.CancelActiveLoad();
                break;
            case StatusOperationType.RefreshProject:
                _projectOperationCts?.Cancel();
                _refreshPipeline.CancelActiveRefresh();
                break;
            case StatusOperationType.GitPullUpdates:
            case StatusOperationType.GitSwitchBranch:
                _gitOperationCts?.Cancel();
                break;
            case StatusOperationType.PreviewBuild:
                _previewPipeline.CancelActiveBuild();
                break;
            case StatusOperationType.SelectionRefresh:
                if (_selectionCoordinator.CancelPendingRefreshes())
                    _toastService.Show(_localization["Toast.Operation.RefreshCanceled"]);
                break;
            case StatusOperationType.ApplySettings:
                _applySettingsCts?.Cancel();
                _selectionCoordinator.CancelPendingRefreshes();
                _refreshPipeline.CancelActiveRefresh();
                break;
            case StatusOperationType.ProjectCopyExport:
                _projectCopyExportCts?.Cancel();
                break;
            case StatusOperationType.MetricsCalculation:
                // Metrics cancellation is handled below by dedicated fallback logic.
                break;
            case StatusOperationType.None:
            default:
                break;
        }

        if (activeOperationType == StatusOperationType.MetricsCalculation)
        {
            _metrics.CancelByUser();
            _toastService.Show(_localization["Toast.Operation.MetricsCanceled"]);
        }

        if (activeOperationType == StatusOperationType.LoadProject)
        {
            if (TryApplyActiveProjectLoadCancellationFallback())
                _toastService.Show(_localization["Toast.Operation.LoadCanceled"]);
        }

        // Cancel preview build if in progress
        if (_viewModel.IsPreviewLoading || activeOperationType == StatusOperationType.PreviewBuild)
        {
            _previewPipeline.CancelActiveBuild();
            _viewModel.IsPreviewLoading = false;
            _toastService.Show(_viewModel.ToastPreviewCanceled);
        }

        _statusOperations.Complete(activeOperationId);
    }

    #endregion
}
