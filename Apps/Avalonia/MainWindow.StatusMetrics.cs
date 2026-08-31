using DevProjex.Avalonia.Coordinators;

namespace DevProjex.Avalonia;

public partial class MainWindow
{
    private bool IsBackgroundMetricsActive()
        => _metrics.IsBackgroundActive;

	private void OnTreeNodeCheckedChanged(TreeNodeViewModel node)
    {
		if (_suppressTreeSelectionChanges > 0)
			return;
		_explicitTreeSelectionProjectPath = _currentPath;

		RecordTreeSelectionOverride(node.FullPath, node.IsChecked == true);

		if (_treeSelectionChangeBatchDepth > 0)
		{
			_treeSelectionChangedDuringBatch = true;
			return;
		}

		PublishTreeSelectionChange();
	}

	private void RecordTreeSelectionOverride(string path, bool isChecked)
	{
		if (string.IsNullOrWhiteSpace(_currentPath))
			return;

		var filterSnapshot = _interactiveFilterSelectionSnapshot;
		if (filterSnapshot is not null && filterSnapshot.IsForProject(_currentPath))
			filterSnapshot.RecordOverride(path, isChecked);

		var gitScopeSnapshot = _gitScopeSelectionSnapshot;
		if (gitScopeSnapshot is not null &&
		    !ReferenceEquals(gitScopeSnapshot, filterSnapshot) &&
		    gitScopeSnapshot.IsForProject(_currentPath))
		{
			gitScopeSnapshot.RecordOverride(path, isChecked);
		}
	}

	private void ApplyTreeSelectionWithoutPublishing(Action applyChanges)
	{
		ArgumentNullException.ThrowIfNull(applyChanges);
		_suppressTreeSelectionChanges++;
		try
		{
			applyChanges();
		}
		finally
		{
			_suppressTreeSelectionChanges--;
		}
	}

	private void ApplyTreeSelectionBatch(Action applyChanges)
	{
		ArgumentNullException.ThrowIfNull(applyChanges);
		_treeSelectionChangeBatchDepth++;
		try
		{
			applyChanges();
		}
		finally
		{
			_treeSelectionChangeBatchDepth--;
			if (_treeSelectionChangeBatchDepth == 0 && _treeSelectionChangedDuringBatch)
			{
				// Restoring a saved selection may touch thousands of nodes. Publish it as one atomic
				// selection revision so dependent scans start once from the final state.
				_treeSelectionChangedDuringBatch = false;
				PublishTreeSelectionChange();
			}
		}
	}

	private void PublishTreeSelectionChange()
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
			case StatusOperationType.SecretAnalysis:
				_secretRedactionCountCts?.Cancel();
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
