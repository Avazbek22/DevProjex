namespace DevProjex.Avalonia.Services;

internal sealed class ProjectTreeSelectionSnapshot
{
    private const int MinimumOverrideCompactionCount = 64;

    private readonly IReadOnlySet<string> _checkedPaths;
    private readonly List<TreeCheckedStateOverride> _overrides = [];
    private readonly Dictionary<string, int> _latestOverrideIndices = new(PathComparer.Default);
    private long _nextOverrideSequence;

    private ProjectTreeSelectionSnapshot(
        string projectPath,
        IReadOnlySet<string> checkedPaths)
    {
        ProjectPath = projectPath;
        _checkedPaths = checkedPaths;
    }

    public string ProjectPath { get; }

    internal int StoredOverrideCount => _overrides.Count;

    internal int EffectiveOverrideCount => _latestOverrideIndices.Count;

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
            snapshotCache.DetachRawSnapshotForTreeReplacement(roots));
    }

    public bool IsForProject(string projectPath) =>
        PathComparer.Default.Equals(ProjectPath, projectPath);

    public void RecordOverride(string path, bool isChecked)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var stateOverride = new TreeCheckedStateOverride(
            path,
            isChecked,
            unchecked(++_nextOverrideSequence));
        _latestOverrideIndices[path] = _overrides.Count;
        _overrides.Add(stateOverride);
        CompactOverridesIfNeeded();
    }

    public TreeSelectionRestoreResult Restore(TreeNodeViewModel root)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (!PathComparer.Default.Equals(ProjectPath, root.FullPath))
            return TreeSelectionRestoreResult.ProjectMismatch;

        if (_checkedPaths.Count == 0 && _latestOverrideIndices.Count == 0)
            return new TreeSelectionRestoreResult(Applied: true, MissingCheckedPathCount: 0);

        var pathsToResolve = BuildPathsToResolve();
        var resolution = ProjectTreeUiState.ResolvePaths(root, pathsToResolve);
        var restoreState = new CheckedStateRestoreSession();
        foreach (var path in _checkedPaths)
        {
            if (resolution.TryGetNode(path, out var node))
                restoreState.Apply(node, isChecked: true, sequence: 0);
        }

        for (var index = 0; index < _overrides.Count; index++)
        {
            if (!IsLatestOverride(index))
                continue;

            var stateOverride = _overrides[index];
            if (resolution.TryGetNode(stateOverride.Path, out var node))
                restoreState.Apply(node, stateOverride.IsChecked, stateOverride.Sequence);
        }

        var checkedStateRecalculations = restoreState.RecalculateAncestors();
        var missingCheckedPaths = CountMissingCheckedPaths(resolution);
        return new TreeSelectionRestoreResult(
            Applied: true,
            missingCheckedPaths,
            resolution.InspectedChildCount,
            checkedStateRecalculations);
    }

    private IReadOnlyCollection<string> BuildPathsToResolve()
    {
        if (_latestOverrideIndices.Count == 0)
            return _checkedPaths;

        var paths = new HashSet<string>(_checkedPaths, PathComparer.Default);
        for (var index = 0; index < _overrides.Count; index++)
        {
            if (IsLatestOverride(index))
                paths.Add(_overrides[index].Path);
        }

        return paths;
    }

    private int CountMissingCheckedPaths(ProjectTreePathResolution resolution)
    {
        HashSet<string>? candidates = null;
        foreach (var path in _checkedPaths)
        {
            if (!resolution.TryGetNode(path, out _))
                (candidates ??= new HashSet<string>(PathComparer.Default)).Add(path);
        }

        for (var index = 0; index < _overrides.Count; index++)
        {
            if (!IsLatestOverride(index))
                continue;

            var stateOverride = _overrides[index];
            if (stateOverride.IsChecked &&
                !resolution.TryGetNode(stateOverride.Path, out _))
            {
                (candidates ??= new HashSet<string>(PathComparer.Default)).Add(stateOverride.Path);
            }
        }

        if (candidates is null)
            return 0;

        var selectedCandidates = new List<string>(candidates.Count);
        foreach (var path in candidates)
        {
            var latestOverride = FindLatestOverrideAtOrAbove(path);
            if (latestOverride is { IsChecked: false })
                continue;
            if (latestOverride is { IsChecked: true } &&
                !PathComparer.Default.Equals(latestOverride.Value.Path, path))
            {
                continue;
            }

            selectedCandidates.Add(path);
        }

        selectedCandidates.Sort(static (left, right) => left.Length.CompareTo(right.Length));
        var minimalMissingPaths = new HashSet<string>(PathComparer.Default);
        for (var index = 0; index < selectedCandidates.Count; index++)
        {
            var path = selectedCandidates[index];
            if (!HasAncestorInSet(path, minimalMissingPaths))
                minimalMissingPaths.Add(path);
        }

        return minimalMissingPaths.Count;
    }

    private TreeCheckedStateOverride? FindLatestOverrideAtOrAbove(string path)
    {
        TreeCheckedStateOverride? latest = null;
        var current = path;
        while (ProjectTreeUiState.IsSameOrDescendantPath(current, ProjectPath))
        {
            if (_latestOverrideIndices.TryGetValue(current, out var index))
            {
                var candidate = _overrides[index];
                if (latest is null || candidate.Sequence > latest.Value.Sequence)
                    latest = candidate;
            }

            if (PathComparer.Default.Equals(current, ProjectPath))
                break;

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || PathComparer.Default.Equals(parent, current))
                break;
            current = parent;
        }

        return latest;
    }

    private bool IsLatestOverride(int index) =>
        _latestOverrideIndices.TryGetValue(_overrides[index].Path, out var latestIndex) &&
        latestIndex == index;

    private void CompactOverridesIfNeeded()
    {
        if (_overrides.Count < MinimumOverrideCompactionCount ||
            _overrides.Count <= (long)_latestOverrideIndices.Count * 2)
        {
            return;
        }

        var writeIndex = 0;
        for (var readIndex = 0; readIndex < _overrides.Count; readIndex++)
        {
            if (!IsLatestOverride(readIndex))
                continue;

            var stateOverride = _overrides[readIndex];
            _overrides[writeIndex] = stateOverride;
            _latestOverrideIndices[stateOverride.Path] = writeIndex;
            writeIndex++;
        }

        if (writeIndex < _overrides.Count)
            _overrides.RemoveRange(writeIndex, _overrides.Count - writeIndex);
    }

    private static bool HasAncestorInSet(string path, HashSet<string> candidates)
    {
        var parent = Path.GetDirectoryName(path);
        while (!string.IsNullOrEmpty(parent))
        {
            if (candidates.Contains(parent))
                return true;

            var next = Path.GetDirectoryName(parent);
            if (string.IsNullOrEmpty(next) || PathComparer.Default.Equals(next, parent))
                return false;
            parent = next;
        }

        return false;
    }

    private sealed class CheckedStateRestoreSession
    {
        private readonly Dictionary<TreeNodeViewModel, long> _explicitSequences = [];
        private readonly Dictionary<TreeNodeViewModel, long> _latestDescendantSequences = [];

        public void Apply(TreeNodeViewModel node, bool isChecked, long sequence)
        {
            node.SetCheckedForTreeStateRestore(isChecked);
            if (node.HasChildren)
                _explicitSequences[node] = sequence;

            var ancestor = node.Parent;
            while (ancestor is not null)
            {
                if (!_latestDescendantSequences.TryGetValue(ancestor, out var current) ||
                    sequence > current)
                {
                    _latestDescendantSequences[ancestor] = sequence;
                }
                ancestor = ancestor.Parent;
            }
        }

        public int RecalculateAncestors()
        {
            if (_latestDescendantSequences.Count == 0)
                return 0;

            var ancestors = new List<TreeNodeViewModel>(_latestDescendantSequences.Count);
            foreach (var pair in _latestDescendantSequences)
            {
                if (_explicitSequences.TryGetValue(pair.Key, out var explicitSequence) &&
                    explicitSequence >= pair.Value)
                {
                    continue;
                }

                ancestors.Add(pair.Key);
            }

            ancestors.Sort(static (left, right) => right.Depth.CompareTo(left.Depth));
            for (var index = 0; index < ancestors.Count; index++)
                ancestors[index].RecalculateCheckedStateForTreeRestore();
            return ancestors.Count;
        }
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
    int MissingCheckedPathCount,
    int PathLookupChildInspectionCount = 0,
    int CheckedStateRecalculationCount = 0)
{
    public static TreeSelectionRestoreResult ProjectMismatch => new(false, 0);
}

internal readonly record struct TreeCheckedStateOverride(
    string Path,
    bool IsChecked,
    long Sequence);

internal sealed class ProjectTreePathResolution(
    Dictionary<string, TreeNodeViewModel> nodes,
    int inspectedChildCount)
{
    public int InspectedChildCount { get; } = inspectedChildCount;

    public bool TryGetNode(string path, out TreeNodeViewModel node) =>
        nodes.TryGetValue(path, out node!);
}

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

        var resolution = ResolvePaths(root, snapshot.ExpandedPaths);
        var nodes = new List<TreeNodeViewModel>(snapshot.ExpandedPaths.Count);
        for (var index = 0; index < snapshot.ExpandedPaths.Count; index++)
        {
            var path = snapshot.ExpandedPaths[index];
            if (!PathComparer.Default.Equals(path, root.FullPath) &&
                resolution.TryGetNode(path, out var node))
            {
                nodes.Add(node);
            }
        }

        nodes.Sort(static (left, right) => left.Depth.CompareTo(right.Depth));
        using var _ = TreeNodeViewModel.BeginPreserveDescendantExpansionStateScope();
        for (var index = 0; index < nodes.Count; index++)
            nodes[index].IsExpanded = true;
        return true;
    }

    public static TreeNodeViewModel? FindNodeByPath(
        TreeNodeViewModel root,
        string path)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var resolution = ResolvePaths(root, new SinglePathCollection(path));
        return resolution.TryGetNode(path, out var node) ? node : null;
    }

    internal static ProjectTreePathResolution ResolvePaths(
        TreeNodeViewModel root,
        IReadOnlyCollection<string> paths)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(paths);

        var targets = paths is HashSet<string> reusableTargets &&
                      ReferenceEquals(reusableTargets.Comparer, PathComparer.Default)
            ? reusableTargets
            : new HashSet<string>(paths.Count, PathComparer.Default);
        var ancestors = new HashSet<string>(PathComparer.Default);
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path) ||
                !IsSameOrDescendantPath(path, root.FullPath) ||
                (!ReferenceEquals(targets, paths) && !targets.Add(path)) ||
                PathComparer.Default.Equals(path, root.FullPath))
            {
                continue;
            }

            var ancestor = Path.GetDirectoryName(path);
            while (!string.IsNullOrEmpty(ancestor) &&
                   IsSameOrDescendantPath(ancestor, root.FullPath))
            {
                ancestors.Add(ancestor);
                if (PathComparer.Default.Equals(ancestor, root.FullPath))
                    break;
                ancestor = Path.GetDirectoryName(ancestor);
            }
        }

        var resolved = new Dictionary<string, TreeNodeViewModel>(targets.Count, PathComparer.Default);
        if (targets.Contains(root.FullPath))
            resolved[root.FullPath] = root;

        var inspectedChildCount = 0;
        if (ancestors.Contains(root.FullPath))
        {
            var pending = new Stack<TreeNodeViewModel>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                var current = pending.Pop();
                var children = current.Children;
                for (var index = 0; index < children.Count; index++)
                {
                    var child = children[index];
                    inspectedChildCount++;
                    if (targets.Contains(child.FullPath))
                        resolved[child.FullPath] = child;
                    if (ancestors.Contains(child.FullPath))
                        pending.Push(child);
                }
            }
        }

        return new ProjectTreePathResolution(resolved, inspectedChildCount);
    }

    internal static bool IsSameOrDescendantPath(string path, string rootPath)
    {
        if (PathComparer.Default.Equals(path, rootPath))
            return true;
        if (path.Length <= rootPath.Length ||
            !path.StartsWith(rootPath, PathComparer.Comparison))
        {
            return false;
        }

        if (Path.EndsInDirectorySeparator(rootPath))
            return true;

        var separator = path[rootPath.Length];
        return separator == Path.DirectorySeparatorChar ||
               separator == Path.AltDirectorySeparatorChar;
    }

    internal static bool IsCurrentProjectTree(
        string projectPath,
        IList<TreeNodeViewModel> roots) =>
        roots.Count == 1 &&
        PathComparer.Default.Equals(projectPath, roots[0].FullPath);

    private sealed class SinglePathCollection(string path) : IReadOnlyCollection<string>
    {
        public int Count => 1;

        public IEnumerator<string> GetEnumerator()
        {
            yield return path;
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
