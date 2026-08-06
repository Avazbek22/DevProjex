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
        TryElevateAndRestart(currentPath);

    TreeRefreshInput IProjectLoadSnapshotPipelineHost.CreateTreeRefreshInput(
        string currentPath,
        SelectionRefreshSnapshot selectionSnapshot)
    {
        var allowedExt = CollectCheckedSelectionNames(
            selectionSnapshot.EffectiveExtensionOptions,
            StringComparer.OrdinalIgnoreCase);
        var allowedRoot = CollectCheckedSelectionNames(
            selectionSnapshot.RootOptions,
            PathComparer.Default);
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
            inventoryScope);
    }

    void IProjectLoadSnapshotPipelineHost.BeforeProjectLoadTreeRefresh()
    {
        // Mirrors a full tree refresh without requiring an intermediate UI-applied
        // selection state. This keeps obsolete metrics IO out of the load hot path.
        _metrics.CancelBackgroundCalculation();
        _viewModel.StatusMetricsVisible = false;
    }

    BuildTreeSnapshotResult IProjectLoadSnapshotPipelineHost.BuildTree(
        TreeRefreshInput input,
        CancellationToken cancellationToken) =>
        input.TreeInventory is null
            ? _buildTree.ExecuteWithInventory(new BuildTreeRequest(input.CurrentPath, input.Options), cancellationToken)
            : _buildTree.ExecuteWithInventory(new BuildTreeRequest(input.CurrentPath, input.Options), input.TreeInventory, cancellationToken);

    bool IProjectLoadSnapshotPipelineHost.TryHandleTreeRootAccessDenied(
        TreeRefreshInput input,
        BuildTreeResult result) =>
        result.RootAccessDenied &&
        PathComparer.Default.Equals(_currentPath, input.CurrentPath) &&
        TryElevateAndRestart(input.CurrentPath);

    TreeNodeViewModel IProjectLoadSnapshotPipelineHost.BuildTreeViewModel(
        TreeRefreshInput input,
        BuildTreeResult result)
    {
        var root = BuildTreeViewModel(result.Root, null);
        root.DisplayName = input.DisplayName;
        return root;
    }

    void IProjectLoadSnapshotPipelineHost.ApplyProjectLoadSnapshot(
        ProjectLoadSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_selectionCoordinator.ApplyProjectSelectionSnapshot(
            snapshot.TreeInput.CurrentPath,
            snapshot.SelectionSnapshot))
        {
            return;
        }

        ((IRefreshTreePipelineHost)this).ApplyTreeRefreshResult(
            snapshot.TreeInput,
            new BuildTreeSnapshotResult(snapshot.TreeResult, snapshot.TreeInventory),
            snapshot.TreeRoot,
            interactiveFilter: false,
            usedInMemoryFilter: false,
            cancellationToken);
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
