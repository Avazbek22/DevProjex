using DevProjex.Avalonia.Coordinators;

namespace DevProjex.Avalonia;

public partial class MainWindow
{
    private bool IsBackgroundMetricsActive()
        => _metrics.IsBackgroundActive;

    private void OnTreeNodeCheckedChanged(TreeNodeViewModel _)
    {
        _treeSelectionSnapshotCache.Invalidate();
		InvalidateSecretRedactionCount();
		ScheduleCompressionRefreshForSelectionChange();
        _metrics.ScheduleRecalculate();
        SchedulePreviewRefresh();
    }

    private void OnStatusOperationCancelRequested(
        object? sender,
        RoutedEventArgs e)
    {
        var activeOperation = _statusOperations.GetActiveSnapshot();
        var activeOperationId = activeOperation.OperationId;
        var activeOperationType = activeOperation.OperationType;

        try
        {
            activeOperation.CancelAction?.Invoke();
        }
        catch
        {
            // Cancellation is best effort; the scoped fallback below must still run.
        }

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
                {
                    _toastService.Show(
                        _localization[
                            "Toast.Operation.RefreshCanceled"]);
                }

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
            case StatusOperationType.None:
            default:
                break;
        }

        if (activeOperationType ==
            StatusOperationType.MetricsCalculation)
        {
            _metrics.CancelByUser();
            _toastService.Show(
                _localization["Toast.Operation.MetricsCanceled"]);
        }

        if (activeOperationType == StatusOperationType.LoadProject &&
            TryApplyActiveProjectLoadCancellationFallback())
        {
            _toastService.Show(
                _localization["Toast.Operation.LoadCanceled"]);
        }

        if (_viewModel.IsPreviewLoading ||
            activeOperationType == StatusOperationType.PreviewBuild)
        {
            _previewPipeline.CancelActiveBuild();
            _viewModel.IsPreviewLoading = false;
            _toastService.Show(_viewModel.ToastPreviewCanceled);
        }

        _statusOperations.Complete(activeOperationId);
    }
}
