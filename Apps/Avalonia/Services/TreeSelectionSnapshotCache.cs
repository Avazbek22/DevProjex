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

	/// <summary>
	/// Identifies one published selection state. A projection built in the background carries the
	/// version it was built from, and <see cref="StoreProjection"/> drops results whose version is
	/// no longer current instead of caching a stale selection.
	/// </summary>
	public long SelectionVersion => _selectionVersion;

	/// <summary>Returns the ordered file list only when it is already cached for this selection.</summary>
	public IReadOnlyList<string>? TryGetOrderedFiles(TreeNodeDescriptor treeRoot)
	{
		ArgumentNullException.ThrowIfNull(treeRoot);
		return _orderedFiles is not null &&
		       _orderedFilesVersion == _selectionVersion &&
		       ReferenceEquals(_orderedFilesTreeRoot, treeRoot)
			? _orderedFiles
			: null;
	}

	/// <summary>
	/// The pure descriptor-walk part of the projection. It reads no view-model state, so a caller
	/// may capture the checked-path set on the UI thread and run this on a worker.
	/// </summary>
	public static (IReadOnlySet<string> NormalizedPaths, IReadOnlyList<string> OrderedFiles) BuildProjection(
		TreeNodeDescriptor treeRoot,
		IReadOnlySet<string> checkedPaths,
		IReadOnlyList<string>? allOrderedFilePaths)
	{
		ArgumentNullException.ThrowIfNull(treeRoot);
		ArgumentNullException.ThrowIfNull(checkedPaths);
		var normalizedPaths = ProjectTreeSelectionProjection.NormalizeSelectedPaths(
			treeRoot,
			checkedPaths);
		var orderedFiles = normalizedPaths.Count > 0
			? PreviewFileCollectionPolicy.BuildOrderedSelectedFilePaths(normalizedPaths, treeRoot)
			: allOrderedFilePaths ?? PreviewFileCollectionPolicy.BuildOrderedAllFilePaths(treeRoot);
		return (normalizedPaths, orderedFiles);
	}

	public void StoreProjection(
		long selectionVersion,
		TreeNodeDescriptor treeRoot,
		IReadOnlySet<string> normalizedPaths,
		IReadOnlyList<string> orderedFiles)
	{
		ArgumentNullException.ThrowIfNull(treeRoot);
		ArgumentNullException.ThrowIfNull(normalizedPaths);
		ArgumentNullException.ThrowIfNull(orderedFiles);
		if (selectionVersion != _selectionVersion)
			return;

		_normalizedSnapshot = normalizedPaths;
		_normalizedTreeRoot = treeRoot;
		_normalizedVersion = selectionVersion;
		_orderedFiles = orderedFiles;
		_orderedFilesTreeRoot = treeRoot;
		_orderedFilesVersion = selectionVersion;
	}

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
