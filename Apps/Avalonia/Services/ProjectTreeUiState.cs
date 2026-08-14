namespace DevProjex.Avalonia.Services;

internal sealed class ProjectTreeSelectionSnapshot
{
    private readonly HashSet<string> _checkedPaths;
    private readonly List<TreeCheckedStateOverride> _overrides = [];

    private ProjectTreeSelectionSnapshot(
        string projectPath,
        HashSet<string> checkedPaths)
    {
        ProjectPath = projectPath;
        _checkedPaths = checkedPaths;
    }

    public string ProjectPath { get; }

    public static ProjectTreeSelectionSnapshot? Capture(
        string projectPath,
        IList<TreeNodeViewModel> roots,
        TreeSelectionSnapshotCache snapshotCache)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(snapshotCache);

        if (!ProjectTreeUiState.IsCurrentProjectTree(projectPath, roots))
            return null;

        return new ProjectTreeSelectionSnapshot(
            projectPath,
            new HashSet<string>(snapshotCache.GetOrCreate(roots), PathComparer.Default));
    }

    public bool IsForProject(string projectPath) =>
        PathComparer.Default.Equals(ProjectPath, projectPath);

    public void RecordOverride(string path, bool isChecked)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _overrides.Add(new TreeCheckedStateOverride(path, isChecked));
    }

    public TreeSelectionRestoreResult Restore(TreeNodeViewModel root)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (!PathComparer.Default.Equals(ProjectPath, root.FullPath))
            return TreeSelectionRestoreResult.ProjectMismatch;

        var missingCheckedPaths = 0;
        foreach (var path in _checkedPaths)
        {
            var node = ProjectTreeUiState.FindNodeByPath(root, path);
            if (node is null)
            {
                missingCheckedPaths++;
                continue;
            }

            node.IsChecked = true;
        }

        for (var index = 0; index < _overrides.Count; index++)
        {
            var stateOverride = _overrides[index];
            var node = ProjectTreeUiState.FindNodeByPath(root, stateOverride.Path);
            if (node is null)
            {
                if (stateOverride.IsChecked)
                    missingCheckedPaths++;
                continue;
            }

            node.IsChecked = stateOverride.IsChecked;
        }

        return new TreeSelectionRestoreResult(Applied: true, missingCheckedPaths);
    }
}

internal sealed record ProjectTreeExpansionSnapshot(
    string ProjectPath,
    IReadOnlyList<string> ExpandedPaths)
{
    public bool IsForProject(string projectPath) =>
        PathComparer.Default.Equals(ProjectPath, projectPath);
}

internal readonly record struct TreeSelectionRestoreResult(
    bool Applied,
    int MissingCheckedPathCount)
{
    public static TreeSelectionRestoreResult ProjectMismatch => new(false, 0);
}

internal readonly record struct TreeCheckedStateOverride(string Path, bool IsChecked);

internal static class ProjectTreeUiState
{
    public static ProjectTreeExpansionSnapshot? CaptureExpansion(
        string projectPath,
        IList<TreeNodeViewModel> roots)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        ArgumentNullException.ThrowIfNull(roots);

        if (!IsCurrentProjectTree(projectPath, roots))
            return null;

        var expandedPaths = new List<string>();
        TreeNodeViewModel.ForEachRealizedDescendant(roots, node =>
        {
            if (node.IsExpanded)
                expandedPaths.Add(node.FullPath);
        });
        return new ProjectTreeExpansionSnapshot(projectPath, expandedPaths);
    }

    public static bool RestoreExpansion(
        TreeNodeViewModel root,
        ProjectTreeExpansionSnapshot? snapshot)
    {
        ArgumentNullException.ThrowIfNull(root);

        root.IsExpanded = true;
        if (snapshot is null || !snapshot.IsForProject(root.FullPath))
            return snapshot is null;

        using var _ = TreeNodeViewModel.BeginPreserveDescendantExpansionStateScope();
        for (var index = 0; index < snapshot.ExpandedPaths.Count; index++)
        {
            var path = snapshot.ExpandedPaths[index];
            if (PathComparer.Default.Equals(path, root.FullPath))
                continue;

            var node = FindNodeByPath(root, path);
            if (node is not null)
                node.IsExpanded = true;
        }

        return true;
    }

    public static TreeNodeViewModel? FindNodeByPath(
        TreeNodeViewModel root,
        string path)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (PathComparer.Default.Equals(root.FullPath, path))
            return root;
        if (!PathUtility.IsPathInside(path, root.FullPath))
            return null;

        var current = root;
        while (true)
        {
            TreeNodeViewModel? next = null;
            var children = current.Children;
            for (var index = 0; index < children.Count; index++)
            {
                var child = children[index];
                if (PathComparer.Default.Equals(child.FullPath, path))
                    return child;
                if (PathUtility.IsPathInside(path, child.FullPath))
                {
                    next = child;
                    break;
                }
            }

            if (next is null)
                return null;
            current = next;
        }
    }

    internal static bool IsCurrentProjectTree(
        string projectPath,
        IList<TreeNodeViewModel> roots) =>
        roots.Count == 1 &&
        PathComparer.Default.Equals(projectPath, roots[0].FullPath);
}
