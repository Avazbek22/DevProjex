using System.Collections.ObjectModel;
using DevProjex.Terminal.Rendering;
using Terminal.Gui.Text;

namespace DevProjex.Terminal.Tui;

public enum TerminalTreeCheckState
{
	Unchecked,
	Checked,
	Indeterminate
}

public sealed record TerminalTreeRow(
	TreeNodeDescriptor Node,
	int Depth,
	bool IsExpanded,
	TerminalTreeCheckState CheckState)
{
	private readonly string _displayText = BuildDisplayText(Node, Depth, IsExpanded, CheckState);
	private readonly int _displayWidth = ResolveDisplayWidth(Node, Depth);

	public int DisplayWidth => _displayWidth;

	public override string ToString() => _displayText;

	private static int ResolveDisplayWidth(TreeNodeDescriptor node, int depth) =>
		depth * 2 + 6 + TerminalTextEscaping.EscapeSingleLine(node.DisplayName).GetColumns();

	private static string BuildDisplayText(
		TreeNodeDescriptor node,
		int depth,
		bool isExpanded,
		TerminalTreeCheckState checkState)
	{
		var indentation = new string(' ', depth * 2);
		var disclosure = node.IsDirectory
			? isExpanded ? "v" : ">"
			: " ";
		var check = checkState switch
		{
			TerminalTreeCheckState.Checked => "[x]",
			TerminalTreeCheckState.Indeterminate => "[-]",
			_ => "[ ]"
		};
		return $"{indentation}{disclosure} {check} {TerminalTextEscaping.EscapeSingleLine(node.DisplayName)}";
	}
}

/// <summary>
/// Keeps terminal tree interaction entirely in memory. Filesystem scans are reserved for
/// structural settings changes; expanding nodes and changing check state only rebuild the
/// visible projection.
/// </summary>
public sealed class TerminalWorkspaceState : IDisposable
{
	private readonly HashSet<string> _expandedPaths = new(PathComparer.Default);
	private readonly HashSet<string> _selectedFiles = new(PathComparer.Default);
	private readonly HashSet<string> _selectedEmptyDirectories = new(PathComparer.Default);
	private readonly Dictionary<string, TreeNodeDescriptor> _nodesByPath = new(PathComparer.Default);
	private readonly Dictionary<string, string?> _parentsByPath = new(PathComparer.Default);
	private readonly Dictionary<string, TerminalTreeCheckState> _checkStates = new(PathComparer.Default);
	private readonly Dictionary<string, bool> _extensionOptionStates = new(StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<string, bool> _pathOptionStates = new(PathComparer.Default);
	private readonly List<string> _orderedPaths = [];
	private readonly List<IPreviewTextDocument> _retiredPreviewDocuments = [];
	private readonly ResettableObservableCollection<TerminalTreeRow> _visibleRows = [];
	private readonly object _previewSync = new();
	private int _selectedFolderCount;
	private long _revision;
	private string _treeFilterQuery = string.Empty;
	private bool _disposed;

	public TerminalWorkspaceState(ProjectContextPlan plan)
	{
		Plan = plan;
		var profileExtensionStates = ProjectSelectionAdapter.GetLocalProfileExtensionStates(
			plan.Selection);
		if (profileExtensionStates is not null)
		{
			foreach (var (extension, isSelected) in profileExtensionStates)
				_extensionOptionStates[extension] = isSelected;
		}
		UpdateExtensionOptionStates(plan);
		IndexTree(plan.EffectiveTree, parentPath: null);
		foreach (var file in plan.IncludedFiles)
			_selectedFiles.Add(file);
		foreach (var directory in plan.IncludedFolders)
		{
			if (_nodesByPath.TryGetValue(directory, out var node) && node.Children.Count == 0)
				_selectedEmptyDirectories.Add(directory);
		}

		_expandedPaths.Add(plan.EffectiveTree.FullPath);
		RecomputeCheckStates();
		UpdatePathOptionStates(plan);
		RebuildVisibleRows();
		var initialPreview = BuildTreePreview();
		PreviewDocument = new InMemoryPreviewTextDocument(initialPreview);
		PreviewOutputMetrics = ExportOutputMetricsCalculator.FromText(initialPreview);
	}

	public ProjectContextPlan Plan { get; private set; }
	public ObservableCollection<TerminalTreeRow> VisibleRows => _visibleRows;
	public int VisibleRowWidth { get; private set; } = 1;
	public int SelectedFileCount => _selectedFiles.Count;
	public int SelectedFolderCount => _selectedFolderCount;
	public bool HasVisibleTreeItems => Plan.EffectiveTree.Children.Count > 0;
	public IReadOnlyDictionary<string, bool> ExtensionOptionStates => _extensionOptionStates;
	public IReadOnlyDictionary<string, bool> PathOptionStates => _pathOptionStates;
	public string TreeFilterQuery => _treeFilterQuery;
	public int TreeFilterMatchCount { get; private set; }
	public bool HasTreeFilter => _treeFilterQuery.Length > 0;
	public long Revision => Volatile.Read(ref _revision);
	public IPreviewTextDocument PreviewDocument { get; private set; }
	public ExportOutputMetrics PreviewOutputMetrics { get; private set; }
	public string PreviewText => PreviewDocument.GetFullText();

	public void ReplacePlan(
		ProjectContextPlan plan,
		IReadOnlyDictionary<string, bool>? extensionOptionStates = null,
		IReadOnlyDictionary<string, bool>? pathOptionStates = null)
	{
		Interlocked.Increment(ref _revision);
		var expandedPaths = _expandedPaths.ToArray();
		Plan = plan;
		if (extensionOptionStates is not null)
		{
			_extensionOptionStates.Clear();
			foreach (var (extension, isSelected) in extensionOptionStates)
				_extensionOptionStates[extension] = isSelected;
		}
		UpdateExtensionOptionStates(plan);
		if (pathOptionStates is not null)
		{
			_pathOptionStates.Clear();
			foreach (var (path, isSelected) in pathOptionStates)
				_pathOptionStates[path] = isSelected;
		}
		_nodesByPath.Clear();
		_parentsByPath.Clear();
		_orderedPaths.Clear();
		_selectedFiles.Clear();
		_selectedEmptyDirectories.Clear();
		_checkStates.Clear();
		_expandedPaths.Clear();
		IndexTree(plan.EffectiveTree, parentPath: null);
		foreach (var file in plan.IncludedFiles)
			_selectedFiles.Add(file);
		foreach (var directory in plan.IncludedFolders)
		{
			if (_nodesByPath.TryGetValue(directory, out var node) && node.Children.Count == 0)
				_selectedEmptyDirectories.Add(directory);
		}
		foreach (var path in expandedPaths)
		{
			if (_nodesByPath.ContainsKey(path))
				_expandedPaths.Add(path);
		}
		RecomputeCheckStates();
		UpdatePathOptionStates(plan);
		RebuildVisibleRows();
		SetPreviewText(BuildTreePreview());
	}

	public IReadOnlyDictionary<string, bool> BuildExtensionOptionStates(
		IReadOnlyCollection<string> selectedExtensions)
	{
		var selected = new HashSet<string>(selectedExtensions, StringComparer.OrdinalIgnoreCase);
		var result = new Dictionary<string, bool>(_extensionOptionStates, StringComparer.OrdinalIgnoreCase);
		foreach (var extension in Plan.AvailableExtensions)
			result[extension] = selected.Contains(extension);
		foreach (var extension in selected)
			result.TryAdd(extension, true);
		return result;
	}

	public IReadOnlySet<string> BuildSelectedItemRelativePaths() =>
		_selectedFiles
			.Concat(_selectedEmptyDirectories)
			.Select(ToRelativePath)
			.ToHashSet(PathComparer.Default);

	internal static IReadOnlyList<string> BuildSelectableRelativePaths(
		TreeNodeDescriptor root,
		string sourceRoot)
	{
		var result = new List<string>();
		var stack = new Stack<TreeNodeDescriptor>();
		stack.Push(root);
		while (stack.Count > 0)
		{
			var node = stack.Pop();
			if (!node.IsDirectory || node.Children.Count == 0)
			{
				result.Add(NormalizeRelativePath(sourceRoot, node.FullPath));
				continue;
			}
			for (var index = node.Children.Count - 1; index >= 0; index--)
				stack.Push(node.Children[index]);
		}
		return result;
	}

	private void UpdateExtensionOptionStates(ProjectContextPlan plan)
	{
		var selected = new HashSet<string>(plan.SelectedExtensions, StringComparer.OrdinalIgnoreCase);
		foreach (var extension in plan.AvailableExtensions)
			_extensionOptionStates[extension] = selected.Contains(extension);
		foreach (var extension in plan.Selection.Extensions ?? [])
			_extensionOptionStates.TryAdd(extension, true);
	}

	private void UpdatePathOptionStates(ProjectContextPlan plan)
	{
		foreach (var path in BuildSelectableRelativePaths(plan.EffectiveTree, plan.SourceRoot))
			_pathOptionStates[path] = IsRelativePathSelected(path);
	}

	public void ReplaceContentTransformationPlan(ProjectContextPlan plan)
	{
		ArgumentNullException.ThrowIfNull(plan);
		if (!ReferenceEquals(Plan.EffectiveTree, plan.EffectiveTree) ||
			!ReferenceEquals(Plan.ProjectedTree, plan.ProjectedTree))
		{
			throw new ArgumentException(
				"A content-only plan update must preserve both tree instances.",
				nameof(plan));
		}

		Interlocked.Increment(ref _revision);
		Plan = plan;
	}

	public bool TryReplacePlan(ProjectContextPlan plan, long expectedRevision)
	{
		if (Revision != expectedRevision)
			return false;
		ReplacePlan(plan);
		return true;
	}

	public void SetPreviewText(string value) =>
		SetPreviewDocument(
			new InMemoryPreviewTextDocument(value),
			ExportOutputMetricsCalculator.FromText(value));

	public void SetPreviewDocument(
		IPreviewTextDocument document,
		ExportOutputMetrics outputMetrics)
	{
		ArgumentNullException.ThrowIfNull(document);
		lock (_previewSync)
		{
			ThrowIfDisposed();
			_retiredPreviewDocuments.Add(PreviewDocument);
			PreviewDocument = document;
			PreviewOutputMetrics = outputMetrics;
		}
	}

	public bool TrySetPreviewDocument(
		IPreviewTextDocument document,
		ExportOutputMetrics outputMetrics,
		long expectedRevision)
	{
		ArgumentNullException.ThrowIfNull(document);
		lock (_previewSync)
		{
			if (_disposed || Revision != expectedRevision)
				return false;

			_retiredPreviewDocuments.Add(PreviewDocument);
			PreviewDocument = document;
			PreviewOutputMetrics = outputMetrics;
			return true;
		}
	}

	public void ReleaseRetiredPreviewDocuments()
	{
		IPreviewTextDocument[] retired;
		lock (_previewSync)
		{
			retired = _retiredPreviewDocuments.ToArray();
			_retiredPreviewDocuments.Clear();
		}

		foreach (var document in retired)
			document.Dispose();
	}

	public void ToggleExpansion(int rowIndex)
	{
		if (!TryGetRow(rowIndex, out var row) || !row.Node.IsDirectory)
			return;

		if (!_expandedPaths.Add(row.Node.FullPath))
			_expandedPaths.Remove(row.Node.FullPath);
		RebuildVisibleRows();
	}

	public void Expand(int rowIndex)
	{
		if (!TryGetRow(rowIndex, out var row) || !row.Node.IsDirectory)
			return;
		if (_expandedPaths.Add(row.Node.FullPath))
			RebuildVisibleRows();
	}

	public void Collapse(int rowIndex)
	{
		if (!TryGetRow(rowIndex, out var row) || !row.Node.IsDirectory)
			return;
		if (_expandedPaths.Remove(row.Node.FullPath))
			RebuildVisibleRows();
	}

	public void ToggleSelection(int rowIndex)
	{
		if (!TryGetRow(rowIndex, out var row))
			return;

		Interlocked.Increment(ref _revision);
		var select = GetCheckState(row.Node) != TerminalTreeCheckState.Checked;
		SetSubtreeSelection(row.Node, select);
		RecomputeCheckStates();
		RebuildVisibleRows();
	}

	public int FindNext(string query, int startIndex, bool reverse = false)
	{
		if (string.IsNullOrWhiteSpace(query))
			return -1;
		if (!HasTreeFilter)
			return FindNextInCompleteTree(query, startIndex, reverse);
		if (VisibleRows.Count == 0)
			return -1;

		var currentIndex = startIndex >= 0 && startIndex < VisibleRows.Count
			? startIndex
			: reverse ? 0 : -1;
		for (var offset = 1; offset <= VisibleRows.Count; offset++)
		{
			var index = reverse
				? Mod(currentIndex - offset, VisibleRows.Count)
				: Mod(currentIndex + offset, VisibleRows.Count);
			if (!VisibleRows[index].Node.DisplayName.Contains(
					query,
					StringComparison.OrdinalIgnoreCase))
				continue;

			return index;
		}

		return -1;
	}

	private int FindNextInCompleteTree(string query, int startIndex, bool reverse)
	{
		if (_orderedPaths.Count == 0)
			return -1;

		var currentPath = startIndex >= 0 && startIndex < VisibleRows.Count
			? VisibleRows[startIndex].Node.FullPath
			: null;
		var currentIndex = currentPath is null
			? reverse ? 0 : -1
			: _orderedPaths.FindIndex(path => PathComparer.Default.Equals(path, currentPath));
		for (var offset = 1; offset <= _orderedPaths.Count; offset++)
		{
			var index = reverse
				? Mod(currentIndex - offset, _orderedPaths.Count)
				: Mod(currentIndex + offset, _orderedPaths.Count);
			var path = _orderedPaths[index];
			if (!_nodesByPath[path].DisplayName.Contains(
					query,
					StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}

			ExpandAncestors(path);
			return VisibleRows
				.Select((row, visibleIndex) => (row, visibleIndex))
				.First(tuple => PathComparer.Default.Equals(tuple.row.Node.FullPath, path))
				.visibleIndex;
		}

		return -1;
	}

	public void ApplyTreeFilter(string? query)
	{
		var normalized = query?.Trim() ?? string.Empty;
		if (string.Equals(_treeFilterQuery, normalized, StringComparison.Ordinal))
			return;

		_treeFilterQuery = normalized;
		RebuildVisibleRows();
	}

	public IReadOnlyList<string> BuildSelectedRelativePaths()
	{
		if (GetCheckState(Plan.EffectiveTree) == TerminalTreeCheckState.Checked)
			return [];

		var result = new List<string>();
		CollectMinimalSelection(Plan.EffectiveTree, result);
		return result
			.Select(path => Path.GetRelativePath(Plan.SourceRoot, path))
			.Select(static path => path == "." ? "." : PathUtility.NormalizeSeparators(path))
			.OrderBy(static path => path, StringComparer.Ordinal)
			.ToArray();
	}

	public ProjectSelectionSpec BuildSelection() =>
		Plan.Selection with { SelectedPaths = BuildSelectedRelativePaths() };

	public string BuildTreePreview(int maximumRows = 2_000)
	{
		var output = new StringBuilder();
		var written = 0;
		var stack = new Stack<(TreeNodeDescriptor Node, int Depth)>();
		stack.Push((Plan.EffectiveTree, 0));
		while (stack.Count > 0 && written < maximumRows)
		{
			var (node, depth) = stack.Pop();
			if (GetCheckState(node) == TerminalTreeCheckState.Unchecked)
				continue;

			output.Append(' ', depth * 2)
				.Append(node.IsDirectory ? "+ " : "- ")
				.AppendLine(TerminalTextEscaping.EscapeSingleLine(node.DisplayName));
			written++;

			for (var index = node.Children.Count - 1; index >= 0; index--)
				stack.Push((node.Children[index], depth + 1));
		}

		if (stack.Count > 0)
			output.AppendLine("...");
		return output.ToString().TrimEnd();
	}

	private void RebuildVisibleRows()
	{
		var rows = new List<TerminalTreeRow>();
		if (_treeFilterQuery.Length == 0)
		{
			TreeFilterMatchCount = 0;
			AppendVisible(Plan.EffectiveTree, depth: 0, rows);
		}
		else
		{
			var matchCount = 0;
			AppendFiltered(Plan.EffectiveTree, depth: 0, rows, ref matchCount);
			TreeFilterMatchCount = matchCount;
		}

		VisibleRowWidth = 1;
		foreach (var row in rows)
			VisibleRowWidth = Math.Max(VisibleRowWidth, row.DisplayWidth);
		_visibleRows.Reset(rows);
	}

	private bool AppendFiltered(
		TreeNodeDescriptor node,
		int depth,
		List<TerminalTreeRow> rows,
		ref int matchCount)
	{
		var includedPaths = new HashSet<string>(PathComparer.Default);
		var traversal = new Stack<(TreeNodeDescriptor Node, bool Visited)>();
		traversal.Push((node, false));
		while (traversal.Count > 0)
		{
			var (current, visited) = traversal.Pop();
			if (!visited && current.IsDirectory && current.Children.Count > 0)
			{
				traversal.Push((current, true));
				for (var index = current.Children.Count - 1; index >= 0; index--)
					traversal.Push((current.Children[index], false));
				continue;
			}

			var selfMatches = current.DisplayName.Contains(
				_treeFilterQuery,
				StringComparison.OrdinalIgnoreCase);
			if (selfMatches)
				matchCount++;
			var descendantMatches = current.IsDirectory &&
				current.Children.Any(child => includedPaths.Contains(child.FullPath));
			if (selfMatches || descendantMatches)
				includedPaths.Add(current.FullPath);
		}

		if (!includedPaths.Contains(node.FullPath))
			return false;

		var projection = new Stack<(TreeNodeDescriptor Node, int Depth)>();
		projection.Push((node, depth));
		while (projection.Count > 0)
		{
			var (current, currentDepth) = projection.Pop();
			var descendantMatches = current.IsDirectory &&
				current.Children.Any(child => includedPaths.Contains(child.FullPath));
			rows.Add(new TerminalTreeRow(
				current,
				currentDepth,
				descendantMatches,
				GetCheckState(current)));
			for (var index = current.Children.Count - 1; index >= 0; index--)
			{
				var child = current.Children[index];
				if (includedPaths.Contains(child.FullPath))
					projection.Push((child, currentDepth + 1));
			}
		}
		return true;
	}

	private void AppendVisible(
		TreeNodeDescriptor node,
		int depth,
		ICollection<TerminalTreeRow> rows)
	{
		var stack = new Stack<(TreeNodeDescriptor Node, int Depth)>();
		stack.Push((node, depth));
		while (stack.Count > 0)
		{
			var (current, currentDepth) = stack.Pop();
			var expanded = current.IsDirectory && _expandedPaths.Contains(current.FullPath);
			rows.Add(new TerminalTreeRow(current, currentDepth, expanded, GetCheckState(current)));
			if (!expanded)
				continue;

			for (var index = current.Children.Count - 1; index >= 0; index--)
				stack.Push((current.Children[index], currentDepth + 1));
		}
	}

	private TerminalTreeCheckState GetCheckState(TreeNodeDescriptor node)
	{
		return _checkStates.TryGetValue(node.FullPath, out var state)
			? state
			: TerminalTreeCheckState.Unchecked;
	}

	private void SetSubtreeSelection(TreeNodeDescriptor node, bool selected)
	{
		var stack = new Stack<TreeNodeDescriptor>();
		stack.Push(node);
		while (stack.Count > 0)
		{
			var current = stack.Pop();
			if (!current.IsDirectory)
			{
				if (selected)
					_selectedFiles.Add(current.FullPath);
				else
					_selectedFiles.Remove(current.FullPath);
				_pathOptionStates[ToRelativePath(current.FullPath)] = selected;
				continue;
			}

			if (current.Children.Count == 0)
			{
				if (selected)
					_selectedEmptyDirectories.Add(current.FullPath);
				else
					_selectedEmptyDirectories.Remove(current.FullPath);
				_pathOptionStates[ToRelativePath(current.FullPath)] = selected;
				continue;
			}

			for (var index = current.Children.Count - 1; index >= 0; index--)
				stack.Push(current.Children[index]);
		}
	}

	private bool IsRelativePathSelected(string path)
	{
		var fullPath = Path.GetFullPath(Path.Combine(
			Plan.SourceRoot,
			path.Replace('/', Path.DirectorySeparatorChar)));
		return _selectedFiles.Contains(fullPath) || _selectedEmptyDirectories.Contains(fullPath);
	}

	private string ToRelativePath(string fullPath) =>
		NormalizeRelativePath(Plan.SourceRoot, fullPath);

	private static string NormalizeRelativePath(string sourceRoot, string fullPath)
	{
		var relative = Path.GetRelativePath(sourceRoot, fullPath);
		return relative == "." ? "." : PathUtility.NormalizeSeparators(relative);
	}

	private void CollectMinimalSelection(TreeNodeDescriptor node, ICollection<string> result)
	{
		var stack = new Stack<TreeNodeDescriptor>();
		stack.Push(node);
		while (stack.Count > 0)
		{
			var current = stack.Pop();
			var state = GetCheckState(current);
			if (state == TerminalTreeCheckState.Unchecked)
				continue;
			if (state == TerminalTreeCheckState.Checked)
			{
				result.Add(current.FullPath);
				continue;
			}

			for (var index = current.Children.Count - 1; index >= 0; index--)
				stack.Push(current.Children[index]);
		}
	}

	private void RecomputeCheckStates()
	{
		_checkStates.Clear();
		_selectedFolderCount = 0;
		var stack = new Stack<(TreeNodeDescriptor Node, bool Visited)>();
		stack.Push((Plan.EffectiveTree, false));
		while (stack.Count > 0)
		{
			var (node, visited) = stack.Pop();
			if (!visited && node.IsDirectory && node.Children.Count > 0)
			{
				stack.Push((node, true));
				for (var index = node.Children.Count - 1; index >= 0; index--)
					stack.Push((node.Children[index], false));
				continue;
			}

			TerminalTreeCheckState state;
			if (!node.IsDirectory)
			{
				state = _selectedFiles.Contains(node.FullPath)
					? TerminalTreeCheckState.Checked
					: TerminalTreeCheckState.Unchecked;
			}
			else if (node.Children.Count == 0)
			{
				state = _selectedEmptyDirectories.Contains(node.FullPath)
					? TerminalTreeCheckState.Checked
					: TerminalTreeCheckState.Unchecked;
			}
			else
			{
				state = _checkStates[node.Children[0].FullPath];
				for (var index = 1; index < node.Children.Count; index++)
				{
					if (_checkStates[node.Children[index].FullPath] == state)
						continue;

					state = TerminalTreeCheckState.Indeterminate;
					break;
				}
			}

			_checkStates[node.FullPath] = state;
			if (node.IsDirectory && state != TerminalTreeCheckState.Unchecked)
				_selectedFolderCount++;
		}
	}

	private void IndexTree(TreeNodeDescriptor node, string? parentPath)
	{
		var stack = new Stack<(TreeNodeDescriptor Node, string? ParentPath)>();
		stack.Push((node, parentPath));
		while (stack.Count > 0)
		{
			var (current, currentParentPath) = stack.Pop();
			_nodesByPath[current.FullPath] = current;
			_parentsByPath[current.FullPath] = currentParentPath;
			_orderedPaths.Add(current.FullPath);
			for (var index = current.Children.Count - 1; index >= 0; index--)
				stack.Push((current.Children[index], current.FullPath));
		}
	}

	private void ExpandAncestors(string path)
	{
		var changed = false;
		var current = _parentsByPath[path];
		while (current is not null)
		{
			if (_nodesByPath[current].IsDirectory)
				changed |= _expandedPaths.Add(current);
			current = _parentsByPath[current];
		}

		if (changed)
			RebuildVisibleRows();
	}

	private bool TryGetRow(int index, out TerminalTreeRow row)
	{
		if (index >= 0 && index < VisibleRows.Count)
		{
			row = VisibleRows[index];
			return true;
		}

		row = null!;
		return false;
	}

	private static int Mod(int value, int modulus) =>
		(value % modulus + modulus) % modulus;

	public void Dispose()
	{
		if (_disposed)
			return;

		IPreviewTextDocument[] documents;
		lock (_previewSync)
		{
			if (_disposed)
				return;
			_disposed = true;
			documents = _retiredPreviewDocuments
				.Append(PreviewDocument)
				.ToArray();
			_retiredPreviewDocuments.Clear();
		}

		foreach (var document in documents)
			document.Dispose();
	}

	private void ThrowIfDisposed()
	{
		if (_disposed)
			throw new ObjectDisposedException(nameof(TerminalWorkspaceState));
	}
}
