namespace DevProjex.Avalonia.Services;

internal sealed class TreeSelectionSnapshotCache
{
    private long _selectionVersion;
    private long _snapshotVersion = -1;
    private TreeNodeViewModel? _snapshotRoot;
    private HashSet<string>? _snapshot;

    public void Invalidate() => _selectionVersion = unchecked(_selectionVersion + 1);

    public HashSet<string> GetOrCreate(IList<TreeNodeViewModel> roots)
    {
        var root = roots.Count > 0 ? roots[0] : null;
        if (_snapshot is not null &&
            _snapshotVersion == _selectionVersion &&
            ReferenceEquals(_snapshotRoot, root))
        {
            return _snapshot;
        }

        var selected = new HashSet<string>(PathComparer.Default);
        for (var index = 0; index < roots.Count; index++)
            roots[index].CollectCheckedPaths(selected);

        _snapshotRoot = root;
        _snapshotVersion = _selectionVersion;
        _snapshot = selected;
        return selected;
    }
}
