using DevProjex.Application.Models;
using DevProjex.Avalonia.Coordinators;
using DevProjex.Kernel;

namespace DevProjex.Avalonia;

public partial class MainWindow : IProjectLoadSnapshotPipelineHost
{
    Task<SelectionRefreshSnapshot?> IProjectLoadSnapshotPipelineHost.BuildSelectionSnapshotAsync(
        string currentPath,
        CancellationToken cancellationToken) =>
        _selectionCoordinator.BuildRootAndDependentsSnapshotAsync(currentPath, cancellationToken);

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
            selectionSnapshot.ExtensionOptions,
            StringComparer.OrdinalIgnoreCase);
        var allowedRoot = CollectCheckedSelectionNames(
            selectionSnapshot.RootOptions,
            PathComparer.Default);
        var selectedIgnoreOptions = CollectCheckedIgnoreOptionIds(selectionSnapshot.IgnoreOptions);
        var ignoreRules = BuildIgnoreRules(currentPath, selectedIgnoreOptions, allowedRoot);
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

        return new TreeRefreshInput(currentPath, displayName, options, nameFilter);
    }

    void IProjectLoadSnapshotPipelineHost.BeforeProjectLoadTreeRefresh()
    {
        // Mirrors a full tree refresh without requiring an intermediate UI-applied
        // selection state. This keeps obsolete metrics IO out of the load hot path.
        _metrics.CancelBackgroundCalculation();
        _viewModel.StatusMetricsVisible = false;
    }

    BuildTreeResult IProjectLoadSnapshotPipelineHost.BuildTree(
        TreeRefreshInput input,
        CancellationToken cancellationToken) =>
        _buildTree.Execute(new BuildTreeRequest(input.CurrentPath, input.Options), cancellationToken);

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

        if (!_selectionCoordinator.ApplyRootAndDependentsSnapshot(
            snapshot.TreeInput.CurrentPath,
            snapshot.SelectionSnapshot))
        {
            return;
        }

        ((IRefreshTreePipelineHost)this).ApplyTreeRefreshResult(
            snapshot.TreeInput,
            snapshot.TreeResult,
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

    private static HashSet<IgnoreOptionId> CollectCheckedIgnoreOptionIds(
        IReadOnlyList<ResolvedIgnoreOptionState> options)
    {
        var selected = new HashSet<IgnoreOptionId>();
        foreach (var option in options)
        {
            if (option.IsChecked)
                selected.Add(option.Id);
        }

        return selected;
    }
}
