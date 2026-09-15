using DevProjex.Application.Models;
using DevProjex.Avalonia.Coordinators;

namespace DevProjex.Avalonia;

public partial class MainWindow : IProjectLoadSnapshotPipelineHost
{
    Task<SelectionRefreshSnapshot?> IProjectLoadSnapshotPipelineHost.BuildSelectionSnapshotAsync(
        string currentPath,
        CancellationToken cancellationToken) =>
        _selectionCoordinator.BuildProjectSelectionSnapshotAsync(currentPath, cancellationToken);

    bool IProjectLoadSnapshotPipelineHost.TryHandleSelectionRootAccessDenied(
        string currentPath,
        SelectionRefreshSnapshot snapshot) =>
        snapshot.RootAccessDenied &&
        PathComparer.Default.Equals(_currentPath, currentPath) &&
        HandleBackgroundRootAccessDenied(currentPath);

    TreeRefreshInput IProjectLoadSnapshotPipelineHost.CreateTreeRefreshInput(
        string currentPath,
        SelectionRefreshSnapshot selectionSnapshot,
        bool preserveTreeState)
    {
        var allowedExt = CollectCheckedSelectionNames(
            selectionSnapshot.EffectiveExtensionOptions,
            StringComparer.OrdinalIgnoreCase);
		var allowedRoot = CollectCheckedSelectionNames(
            selectionSnapshot.RootOptions,
			ProjectTreePathIdentity.CanonicalComparer);
		var availableRoot = selectionSnapshot.RootOptions?
			.Select(static option => option.Name)
			.ToHashSet(ProjectTreePathIdentity.CanonicalComparer) ??
			new HashSet<string>(ProjectTreePathIdentity.CanonicalComparer);
        var ignoreRules = ProjectLoadIgnoreRulesResolver.Resolve(
            selectionSnapshot,
            selectedIgnoreOptions => BuildIgnoreRules(currentPath, selectedIgnoreOptions, allowedRoot));
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
            : GetDirectoryNameSafe(currentPath);

        var inventoryScope = selectionSnapshot.TreeInventory is null
            ? null
            : ProjectTreeInventoryReuseScope.Create(
                currentPath,
                options,
                supportsHiddenDotFolderVariants: true);

        return new TreeRefreshInput(
            currentPath,
            displayName,
            options,
            nameFilter,
			selectionSnapshot.TreeInventory,
			inventoryScope,
			GitMode: _selectionCoordinator.ActiveGitFilteringMode,
			GitScope: selectionSnapshot.GitScope,
			GitScopePresentation: selectionSnapshot.GitScopePresentation,
			AvailableRootFolders: availableRoot,
			PreserveCheckedPaths: preserveTreeState,
            PreserveExpandedPaths: preserveTreeState);
    }

    void IProjectLoadSnapshotPipelineHost.BeforeProjectLoadTreeRefresh()
    {
		CancelSecretRedactionDiscovery();
        // Mirrors a full tree refresh without requiring an intermediate UI-applied
        // selection state. This keeps obsolete metrics IO out of the load hot path.
        _metrics.CancelBackgroundCalculation();
        _viewModel.StatusMetricsVisible = false;
    }

    BuildTreeSnapshotResult IProjectLoadSnapshotPipelineHost.BuildTree(
        TreeRefreshInput input,
        CancellationToken cancellationToken) =>
		BuildTreeWithGitScope(input, cancellationToken);

	bool IProjectLoadSnapshotPipelineHost.TryHandleGitScopeDiagnostics(BuildTreeSnapshotResult result) =>
		HandleGitScopeDiagnostics(result.Diagnostics);

    bool IProjectLoadSnapshotPipelineHost.TryHandleTreeRootAccessDenied(
        TreeRefreshInput input,
        BuildTreeResult result) =>
        result.RootAccessDenied &&
        PathComparer.Default.Equals(_currentPath, input.CurrentPath) &&
        HandleBackgroundRootAccessDenied(input.CurrentPath);

	void IProjectLoadSnapshotPipelineHost.ReportIncompleteTreeScan() =>
		_toastService.Show(_localization["Scan.Error.Incomplete"]);

    TreeNodeViewModel IProjectLoadSnapshotPipelineHost.BuildTreeViewModel(
        TreeRefreshInput input,
		BuildTreeResult result,
		CancellationToken cancellationToken)
    {
		var root = BuildTreeViewModel(result.Root, null, cancellationToken);
        root.DisplayName = input.DisplayName;
        return root;
    }

    bool IProjectLoadSnapshotPipelineHost.ApplyProjectLoadSnapshot(
        ProjectLoadSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_selectionCoordinator.ApplyProjectSelectionSnapshot(
            snapshot.TreeInput.CurrentPath,
            snapshot.SelectionSnapshot))
        {
			return false;
        }
		PrepareContentSessionsForPublishedProject(snapshot);

        // Project profiles are applied with option notifications suppressed. Publish the resolved
        // transformation state before post-load work starts, otherwise a persisted compression
        // selection is visible in the UI while background prewarm still observes the previous project.
        PublishTransformationContext();

		((IRefreshTreePipelineHost)this).ApplyTreeRefreshResult(
			snapshot.TreeInput,
			new BuildTreeSnapshotResult(
				snapshot.TreeResult,
				snapshot.TreeInventory,
				GitScopePresentation: snapshot.GitScopePresentation),
			snapshot.TreeRoot,
            interactiveFilter: false,
            usedInMemoryFilter: false,
            postLoadCleanupReason: null,
            cancellationToken);
		_viewModel.IsProjectLoadInProgress = false;
		return true;
    }

	private void PrepareContentSessionsForPublishedProject(ProjectLoadSnapshot snapshot)
	{
		var projectPath = snapshot.TreeInput.CurrentPath;
		if (string.IsNullOrWhiteSpace(_contentSessionProjectPath) ||
		    !PathComparer.Default.Equals(_contentSessionProjectPath, projectPath))
		{
			_secretRedactionSession.Reset();
			_codeCompressionSession.Reset();
			_contentSessionProjectPath = projectPath;
		}

		_secretRedactionSession.AdvanceContentGeneration(projectPath);
		if (snapshot.PersistentMarks is not null)
			_secretRedactionSession.ReplacePersistentMarks(projectPath, snapshot.PersistentMarks);
	}

    private static HashSet<string> CollectCheckedSelectionNames(
        IReadOnlyList<SelectionOption>? options,
        IEqualityComparer<string> comparer)
    {
        if (options is null || options.Count == 0)
            return new HashSet<string>(comparer);

        var selected = new HashSet<string>(comparer);
        foreach (var option in options)
        {
            if (option.IsChecked)
                selected.Add(option.Name);
        }

        return selected;
    }

}
