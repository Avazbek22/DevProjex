using DevProjex.Avalonia.Coordinators;
using DevProjex.Avalonia.Services;

namespace DevProjex.Avalonia;

public partial class MainWindow : IRefreshTreePipelineHost
{
    MainWindowViewModel IRefreshTreePipelineHost.ViewModel => _viewModel;

    TreeRefreshInput? IRefreshTreePipelineHost.CaptureTreeRefreshInput()
    {
        if (string.IsNullOrWhiteSpace(_currentPath))
            return null;

        var allowedExt = CollectCheckedOptionNames(_viewModel.Extensions, StringComparer.OrdinalIgnoreCase);
        var allowedRoot = _selectionCoordinator.GetProjectScanRoots()
            .ToHashSet(PathComparer.Default);
        var selectedIgnoreOptions = _selectionCoordinator.GetSelectedIgnoreOptionIds();
        var ignoreRules = BuildIgnoreRules(_currentPath, selectedIgnoreOptions, allowedRoot);
        var nameFilter = string.IsNullOrWhiteSpace(_viewModel.NameFilter)
            ? null
            : _viewModel.NameFilter.Trim();
        var options = new TreeFilterOptions(
            AllowedExtensions: allowedExt,
            AllowedRootFolders: allowedRoot,
            IgnoreRules: ignoreRules,
            NameFilter: nameFilter);
        var displayName = !string.IsNullOrWhiteSpace(_currentProjectDisplayName)
            ? _currentProjectDisplayName
            : GetDirectoryNameSafe(_currentPath);

        var inventoryState = _currentTreeInventory;
        var reusableInventory = inventoryState is not null &&
                                inventoryState.Scope.CanProject(_currentPath, options)
            ? inventoryState
            : null;

        return new TreeRefreshInput(
            _currentPath,
            displayName,
            options,
            nameFilter,
            reusableInventory?.Snapshot,
            reusableInventory?.Scope,
            _selectionCoordinator.CurrentSelectionRevision,
            _filterBaseTree);
    }

    void IRefreshTreePipelineHost.BeforeFullTreeRefresh()
    {
        // Full-tree refresh invalidates the active metrics baseline.
        // Cancel early so obsolete file reads stop before we start the next build.
        _metrics.CancelAndDiscardBackgroundCalculation();
        _viewModel.StatusMetricsVisible = false;
    }

    void IRefreshTreePipelineHost.BeforeInteractiveFilterRefresh()
    {
        // A filter projects a new graph from the in-memory baseline. Stop metrics that still
        // reference the previous full graph before that graph becomes eligible for collection.
        _metrics.CancelAndDiscardBackgroundCalculation();
    }

    BuildTreeSnapshotResult IRefreshTreePipelineHost.BuildTree(TreeRefreshInput input, CancellationToken cancellationToken) =>
        input.TreeInventory is null
            ? _buildTree.ExecuteWithInventory(new BuildTreeRequest(input.CurrentPath, input.Options), cancellationToken)
            : _buildTree.ExecuteWithInventory(new BuildTreeRequest(input.CurrentPath, input.Options), input.TreeInventory, cancellationToken);

    bool IRefreshTreePipelineHost.TryHandleRootAccessDenied(TreeRefreshInput input, BuildTreeResult result) =>
        result.RootAccessDenied &&
        PathComparer.Default.Equals(_currentPath, input.CurrentPath) &&
        TryElevateAndRestart(input.CurrentPath);

    TreeNodeViewModel IRefreshTreePipelineHost.BuildTreeViewModel(TreeRefreshInput input, BuildTreeResult result)
    {
        var root = BuildTreeViewModel(result.Root, null);
        root.DisplayName = input.DisplayName;
        return root;
    }

    bool IRefreshTreePipelineHost.IsTreeRefreshInputCurrent(TreeRefreshInput input) =>
        PathComparer.Default.Equals(_currentPath, input.CurrentPath) &&
        (input.SelectionRevision is null ||
         input.SelectionRevision.Value == _selectionCoordinator.CurrentSelectionRevision);

    void IRefreshTreePipelineHost.ApplyTreeRefreshResult(
        TreeRefreshInput input,
        BuildTreeSnapshotResult result,
        TreeNodeViewModel root,
        bool interactiveFilter,
        bool usedInMemoryFilter,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!PathComparer.Default.Equals(_currentPath, input.CurrentPath))
            return;

        // Swap trees only after the new root is fully materialized.
        // This prevents losing the previously visible project on cancellation.
        _searchFilterController.ClearSearchState();
        if (_treeView is not null)
            _treeView.SelectedItem = null;

        foreach (var node in _viewModel.TreeNodes)
            node.ClearRecursive();
        _viewModel.TreeNodes.Clear();

        _currentTree = result.Tree;
		if (!interactiveFilter)
		{
			// «Apply settings» is the commit point for the Compress Code checkbox. Every
			// non-interactive tree publication - load, refresh, Apply - captures the checkbox as
			// the applied transformation state; until then it is a draft with no effect on
			// produced content, the preview, or the measured counters. Name-filter publications
			// deliberately keep the previously applied state.
			_appliedCompressCodeEnabled = _selectionCoordinator
				.GetSelectedIgnoreOptionIds()
				.Contains(IgnoreOptionId.CompressCode);
			_appliedStripCommentsEnabled = _selectionCoordinator
				.GetSelectedIgnoreOptionIds()
				.Contains(IgnoreOptionId.StripComments);
			if (!_appliedCompressCodeEnabled && !_appliedStripCommentsEnabled)
				_codeCompressionSnapshot = null;
		}
		// Keep tree publication visually pure. Initial/full refresh work must remain pending until
		// StartPostLoadBackgroundWork releases it after the settings reveal and layout-settle gate.
		// Scheduling secrets, compression or metrics here reintroduces the initial-load width jump.
		// An interactive name filter has no reveal boundary and stays immediate.
		InvalidateSecretRedactionCount(scheduleRefreshImmediately: interactiveFilter);
        if (interactiveFilter)
            _lastInteractiveFilterUsedInMemory = usedInMemoryFilter;
        UpdateCurrentTreeInventory(input, result, interactiveFilter, usedInMemoryFilter);
        _metrics.InvalidateComputedCaches();

        if (!interactiveFilter)
        {
            // Keep a baseline snapshot for low-latency in-memory filter updates.
            _filterBaseTree = string.IsNullOrWhiteSpace(input.NameFilter) ? result.Tree : null;
            ResetInteractiveFilterCache();
        }
        else if (!usedInMemoryFilter && string.IsNullOrWhiteSpace(input.NameFilter))
        {
            // Recover baseline after fallback interactive rebuilds.
            _filterBaseTree = result.Tree;
            ResetInteractiveFilterCache();
        }

        _viewModel.TreeNodes.Add(root);
        root.IsExpanded = true;

        if (!interactiveFilter)
            _selectionCoordinator.AcceptCurrentSelectionsAsApplied(input.CurrentPath, result.Inventory);

        if (!interactiveFilter && !string.IsNullOrWhiteSpace(input.NameFilter) && root.Children.Count == 0)
            _toastService.Show(_localization["Toast.NoMatches"]);

        if (!interactiveFilter)
            ReapplyActiveTreeQueryPresentation();

        if (!interactiveFilter)
        {
            StartPostLoadBackgroundWork(result.Tree, cancellationToken);
        }
        else
        {
            _metrics.Recalculate(
                string.IsNullOrWhiteSpace(input.NameFilter)
                    ? null
                    : MemoryCleanupReason.FilterApplied);
        }

        SchedulePreviewRefresh(immediate: true);
    }

    private void ReapplyActiveTreeQueryPresentation() =>
        _searchFilterController.ReapplyActiveTreeQueryPresentation();

    private void UpdateCurrentTreeInventory(
        TreeRefreshInput input,
        BuildTreeSnapshotResult result,
        bool interactiveFilter,
        bool usedInMemoryFilter)
    {
        if (interactiveFilter && usedInMemoryFilter)
            return;

        if (result.Inventory is null)
        {
            if (!interactiveFilter)
                _currentTreeInventory = null;
            return;
        }

        var reusedInventory = ReferenceEquals(result.Inventory, input.TreeInventory);
        if (!interactiveFilter &&
            reusedInventory &&
            ProjectTreeInventoryRetentionPolicy.RequiresVisibleTreeMeasurement(result.Inventory.Entries.Count) &&
            ProjectTreeInventoryRetentionPolicy.ShouldReleaseReusedInventory(
                result.Inventory.Entries.Count,
                ProjectTreeInventoryRetentionPolicy.CountTreeEntries(result.Tree.Root)))
        {
            // A broad inventory makes ignore toggles fast, but retaining it after a drastic
            // projection keeps the complete unfiltered workspace alive behind a small tree.
            _currentTreeInventory = null;
            ScheduleBackgroundMemoryCleanup(MemoryCleanupReason.SelectionProjectionNarrowed);
            return;
        }

        var scope = reusedInventory && input.TreeInventoryScope is not null
            ? input.TreeInventoryScope
            : ProjectTreeInventoryReuseScope.Create(
                input.CurrentPath,
                input.Options,
                supportsHiddenDotFolderVariants: false);
        _currentTreeInventory = new ProjectTreeInventoryState(result.Inventory, scope);
    }
}
