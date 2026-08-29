using DevProjex.Avalonia.Coordinators;
using DevProjex.Avalonia.Services;
using DevProjex.Application.Context;

namespace DevProjex.Avalonia;

public partial class MainWindow : IRefreshTreePipelineHost
{
    MainWindowViewModel IRefreshTreePipelineHost.ViewModel => _viewModel;

    TreeRefreshInput? IRefreshTreePipelineHost.CaptureTreeRefreshInput(bool preserveCheckedPaths)
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
		var gitMode = _selectionCoordinator.ActiveGitFilteringMode;
		var gitScopeRefresh = _selectionCoordinator.GetPendingGitScopeRefresh(_currentPath, gitMode);

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
            _filterBaseTree,
			GitMode: gitMode,
			GitScope: gitScopeRefresh?.Scope,
			GitScopePresentation: gitScopeRefresh?.Presentation,
			EffectiveExtensionPolicy: _selectionCoordinator.GetEffectiveExtensionPolicy(),
			AvailableRootFolders: _selectionCoordinator.GetAvailableProjectScanRoots(),
            PreserveCheckedPaths: preserveCheckedPaths);
    }

    void IRefreshTreePipelineHost.BeforeFullTreeRefresh(bool preserveStatusMetrics)
    {
		CancelSecretRedactionDiscovery();
        // Full-tree refresh invalidates the active metrics baseline.
        // Cancel early so obsolete file reads stop before we start the next build.
        _metrics.CancelAndDiscardBackgroundCalculation();
        // Apply keeps the last published snapshot visible until its replacement is ready.
        if (!preserveStatusMetrics)
            _viewModel.StatusMetricsVisible = false;
    }

    void IRefreshTreePipelineHost.BeforeInteractiveFilterRefresh()
    {
        // A filter projects a new graph from the in-memory baseline. Stop metrics that still
        // reference the previous full graph before that graph becomes eligible for collection.
        _metrics.CancelAndDiscardBackgroundCalculation();
    }

    BuildTreeSnapshotResult IRefreshTreePipelineHost.BuildTree(TreeRefreshInput input, CancellationToken cancellationToken) =>
		BuildTreeWithGitScope(input, cancellationToken);

	bool IRefreshTreePipelineHost.TryHandleGitScopeDiagnostics(BuildTreeSnapshotResult result) =>
		HandleGitScopeDiagnostics(result.Diagnostics);

    bool IRefreshTreePipelineHost.TryHandleRootAccessDenied(TreeRefreshInput input, BuildTreeResult result) =>
        result.RootAccessDenied &&
        PathComparer.Default.Equals(_currentPath, input.CurrentPath) &&
        TryElevateAndRestart(input.CurrentPath);

	void IRefreshTreePipelineHost.ReportIncompleteTreeScan() =>
		_toastService.Show(_localization["Scan.Error.Incomplete"]);

	TreeNodeViewModel IRefreshTreePipelineHost.BuildTreeViewModel(
		TreeRefreshInput input,
		BuildTreeResult result,
		CancellationToken cancellationToken)
    {
		var root = BuildTreeViewModel(result.Root, null, cancellationToken);
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
        MemoryCleanupReason? postLoadCleanupReason,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!PathComparer.Default.Equals(_currentPath, input.CurrentPath))
            return;
		if (result.GitScopePresentation is { } gitScopePresentation)
			_selectionCoordinator.ApplyGitScopePresentation(gitScopePresentation);

        if (interactiveFilter)
            _searchFilterController.CaptureFilterExpansionForTreeReplacement(input.CurrentPath);

        var selectionSnapshot = CaptureSelectionForTreeReplacement(input);
        var expansionSnapshot = input.PreserveExpandedPaths &&
                                string.IsNullOrWhiteSpace(input.NameFilter)
            ? ProjectTreeUiState.CaptureExpansion(
                input.CurrentPath,
                _viewModel.TreeNodes)
            : null;

		_treeSelectionSnapshotCache.ResetForTreeReplacement();

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
			// "Apply settings" is the commit point for syntax transformations and section-wide
			// content batches. Every non-interactive tree publication - load, refresh, Apply -
			// captures the selected transformation state; until then its draft has no effect on
			// produced content, preview, or counters. Name-filter publications deliberately keep
			// the previously applied state.
			CaptureAppliedContentTransformationState();
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
        ProjectTreeUiState.RestoreExpansion(root, expansionSnapshot);

        var selectionRestore = TreeSelectionRestoreResult.ProjectMismatch;
        if (selectionSnapshot is not null)
        {
            ApplyTreeSelectionWithoutPublishing(() =>
            {
                selectionRestore = selectionSnapshot.Restore(root);
            });
        }

        var completedFilterSelectionTransfer =
            _interactiveFilterSelectionSnapshot is not null &&
            string.IsNullOrWhiteSpace(input.NameFilter);
        if (completedFilterSelectionTransfer)
            _interactiveFilterSelectionSnapshot = null;

		if (!interactiveFilter &&
		    (!GitScopeSelection.IsMomentary(input.GitMode) ||
		     _gitScopeSelectionSnapshot is { } scopeSnapshot &&
		     !scopeSnapshot.IsForProject(input.CurrentPath)))
		{
			_gitScopeSelectionSnapshot = null;
		}

		if (!interactiveFilter && input.SelectionRevision is { } selectionRevision)
			_selectionCoordinator.ConsumePendingGitScopeRefresh(
				input.CurrentPath,
				input.GitMode,
				selectionRevision);

		if (!interactiveFilter)
			_selectionCoordinator.AcceptCurrentSelectionsAsApplied(input.CurrentPath, result.Inventory);

        if (!interactiveFilter && !string.IsNullOrWhiteSpace(input.NameFilter) && root.Children.Count == 0)
            _toastService.Show(_localization["Toast.NoMatches"]);

        var shouldReportMissingSelection =
            completedFilterSelectionTransfer ||
            !interactiveFilter && _interactiveFilterSelectionSnapshot is null;
        if (shouldReportMissingSelection &&
            selectionRestore.MissingCheckedPathCount > 0)
        {
            _toastService.Show(
                _localization.Format(
                    "Toast.Tree.CheckedSelectionHidden",
                    selectionRestore.MissingCheckedPathCount));
        }

        if (!interactiveFilter)
            ReapplyActiveTreeQueryPresentation();

        if (!interactiveFilter)
        {
            StartPostLoadBackgroundWork(
                result.Tree,
                cancellationToken,
                postLoadCleanupReason);
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

	private ProjectTreeSelectionSnapshot? CaptureSelectionForTreeReplacement(TreeRefreshInput input)
	{
		if (!input.PreserveCheckedPaths)
			return null;

		if (_interactiveFilterSelectionSnapshot is { } filterSnapshot)
		{
			if (filterSnapshot.IsForProject(input.CurrentPath))
			{
				if (GitScopeSelection.IsMomentary(input.GitMode) &&
				    _gitScopeSelectionSnapshot is null)
				{
					_gitScopeSelectionSnapshot = filterSnapshot;
				}
				return filterSnapshot;
			}

			_interactiveFilterSelectionSnapshot = null;
		}

		if (_gitScopeSelectionSnapshot is { } gitScopeSnapshot)
		{
			if (gitScopeSnapshot.IsForProject(input.CurrentPath))
			{
				if (!string.IsNullOrWhiteSpace(input.NameFilter))
					_interactiveFilterSelectionSnapshot = gitScopeSnapshot;
				return gitScopeSnapshot;
			}

			_gitScopeSelectionSnapshot = null;
		}

		var snapshot = ProjectTreeSelectionSnapshot.Capture(
			input.CurrentPath,
			_viewModel.TreeNodes,
			_treeSelectionSnapshotCache);
		if (snapshot is not null && GitScopeSelection.IsMomentary(input.GitMode))
			_gitScopeSelectionSnapshot = snapshot;
		if (snapshot is not null &&
			!string.IsNullOrWhiteSpace(input.NameFilter))
		{
			_interactiveFilterSelectionSnapshot = snapshot;
		}

		return snapshot;
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
