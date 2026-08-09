namespace DevProjex.Avalonia.Services;

internal sealed class TreeSelectionSnapshotCache
{
    private long _selectionVersion;
    private long _snapshotVersion = -1;
    private TreeNodeViewModel? _snapshotRoot;
    private HashSet<string>? _snapshot;
	private long _normalizedVersion = -1;
	private TreeNodeDescriptor? _normalizedTreeRoot;
	private IReadOnlySet<string>? _normalizedSnapshot;
	private long _orderedFilesVersion = -1;
	private TreeNodeDescriptor? _orderedFilesTreeRoot;
	private IReadOnlyList<string>? _orderedFiles;

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

	public IReadOnlySet<string> GetOrCreateNormalized(
		IList<TreeNodeViewModel> roots,
		TreeNodeDescriptor treeRoot)
	{
		ArgumentNullException.ThrowIfNull(roots);
		ArgumentNullException.ThrowIfNull(treeRoot);
		if (_normalizedSnapshot is not null &&
		    _normalizedVersion == _selectionVersion &&
		    ReferenceEquals(_normalizedTreeRoot, treeRoot))
		{
			return _normalizedSnapshot;
		}

		_normalizedSnapshot = ProjectTreeSelectionProjection.NormalizeSelectedPaths(
			treeRoot,
			GetOrCreate(roots));
		_normalizedTreeRoot = treeRoot;
		_normalizedVersion = _selectionVersion;
		return _normalizedSnapshot;
	}

	public IReadOnlyList<string> GetOrCreateOrderedFiles(
		IList<TreeNodeViewModel> roots,
		TreeNodeDescriptor treeRoot,
		IReadOnlyList<string>? allOrderedFilePaths)
	{
		ArgumentNullException.ThrowIfNull(roots);
		ArgumentNullException.ThrowIfNull(treeRoot);
		if (_orderedFiles is not null &&
		    _orderedFilesVersion == _selectionVersion &&
		    ReferenceEquals(_orderedFilesTreeRoot, treeRoot))
		{
			return _orderedFiles;
		}

		var selectedPaths = GetOrCreateNormalized(roots, treeRoot);
		_orderedFiles = selectedPaths.Count > 0
			? PreviewFileCollectionPolicy.BuildOrderedSelectedFilePaths(selectedPaths, treeRoot)
			: allOrderedFilePaths ?? PreviewFileCollectionPolicy.BuildOrderedAllFilePaths(treeRoot);
		_orderedFilesTreeRoot = treeRoot;
		_orderedFilesVersion = _selectionVersion;
		return _orderedFiles;
	}
}
