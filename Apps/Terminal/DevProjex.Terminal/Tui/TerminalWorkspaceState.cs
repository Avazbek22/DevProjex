using System.Collections.ObjectModel;

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
	public override string ToString()
	{
		var indentation = new string(' ', Depth * 2);
		var disclosure = Node.IsDirectory
			? IsExpanded ? "v" : ">"
			: " ";
		var check = CheckState switch
		{
			TerminalTreeCheckState.Checked => "[x]",
			TerminalTreeCheckState.Indeterminate => "[-]",
			_ => "[ ]"
		};
		return $"{indentation}{disclosure} {check} {Node.DisplayName}";
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
	private readonly List<string> _orderedPaths = [];
	private readonly List<IPreviewTextDocument> _retiredPreviewDocuments = [];
	private readonly object _previewSync = new();
	private int _selectedFolderCount;
	private bool _disposed;

	public TerminalWorkspaceState(ProjectContextPlan plan)
	{
		Plan = plan;
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
		RebuildVisibleRows();
		PreviewDocument = new InMemoryPreviewTextDocument(BuildTreePreview());
	}

	public ProjectContextPlan Plan { get; private set; }
	public ObservableCollection<TerminalTreeRow> VisibleRows { get; } = [];
	public int SelectedFileCount => _selectedFiles.Count;
	public int SelectedFolderCount => _selectedFolderCount;
	public IPreviewTextDocument PreviewDocument { get; private set; }
	public string PreviewText => PreviewDocument.GetFullText();

	public void ReplacePlan(ProjectContextPlan plan)
	{
		var expandedPaths = _expandedPaths.ToArray();
		Plan = plan;
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
		_expandedPaths.Add(plan.EffectiveTree.FullPath);
		RecomputeCheckStates();
		RebuildVisibleRows();
		SetPreviewDocument(new InMemoryPreviewTextDocument(BuildTreePreview()));
	}

	public void SetPreviewText(string value) =>
		SetPreviewDocument(new InMemoryPreviewTextDocument(value));

	public void SetPreviewDocument(IPreviewTextDocument document)
	{
		ArgumentNullException.ThrowIfNull(document);
		lock (_previewSync)
		{
			ThrowIfDisposed();
			_retiredPreviewDocuments.Add(PreviewDocument);
			PreviewDocument = document;
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

		var select = GetCheckState(row.Node) != TerminalTreeCheckState.Checked;
		SetSubtreeSelection(row.Node, select);
		RecomputeCheckStates();
		RebuildVisibleRows();
	}

	public int FindNext(string query, int startIndex, bool reverse = false)
	{
		if (string.IsNullOrWhiteSpace(query) || _orderedPaths.Count == 0)
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
			if (!_nodesByPath[path].DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase))
				continue;

			ExpandAncestors(path);
			return VisibleRows
				.Select((row, visibleIndex) => (row, visibleIndex))
				.First(tuple => PathComparer.Default.Equals(tuple.row.Node.FullPath, path))
				.visibleIndex;
		}

		return -1;
	}

	public IReadOnlyList<string> BuildSelectedRelativePaths()
	{
		if (GetCheckState(Plan.EffectiveTree) == TerminalTreeCheckState.Checked)
			return [];

		var result = new List<string>();
		CollectMinimalSelection(Plan.EffectiveTree, result);
		return result
			.Select(path => Path.GetRelativePath(Plan.SourceRoot, path))
			.Select(static path => path == "." ? "." : path.Replace('\\', '/'))
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
				.AppendLine(node.DisplayName);
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
		AppendVisible(Plan.EffectiveTree, depth: 0, rows);

		VisibleRows.Clear();
		foreach (var row in rows)
			VisibleRows.Add(row);
	}

	private void AppendVisible(
		TreeNodeDescriptor node,
		int depth,
		ICollection<TerminalTreeRow> rows)
	{
		var expanded = node.IsDirectory && _expandedPaths.Contains(node.FullPath);
		rows.Add(new TerminalTreeRow(node, depth, expanded, GetCheckState(node)));
		if (!expanded)
			return;

		foreach (var child in node.Children)
			AppendVisible(child, depth + 1, rows);
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
				continue;
			}

			if (current.Children.Count == 0)
			{
				if (selected)
					_selectedEmptyDirectories.Add(current.FullPath);
				else
					_selectedEmptyDirectories.Remove(current.FullPath);
				continue;
			}

			for (var index = current.Children.Count - 1; index >= 0; index--)
				stack.Push(current.Children[index]);
		}
	}

	private bool CollectMinimalSelection(TreeNodeDescriptor node, ICollection<string> result)
	{
		if (!node.IsDirectory)
		{
			if (!_selectedFiles.Contains(node.FullPath))
				return false;
			result.Add(node.FullPath);
			return true;
		}
		if (node.Children.Count == 0)
		{
			if (!_selectedEmptyDirectories.Contains(node.FullPath))
				return false;
			result.Add(node.FullPath);
			return true;
		}

		var selectedBefore = result.Count;
		var allSelected = true;
		foreach (var child in node.Children)
		{
			if (!CollectMinimalSelection(child, result))
				allSelected = false;
		}

		if (!allSelected)
			return false;

		while (result.Count > selectedBefore)
			result.Remove(result.Last());
		result.Add(node.FullPath);
		return true;
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
				var first = _checkStates[node.Children[0].FullPath];
				state = node.Children.Skip(1).All(child => _checkStates[child.FullPath] == first)
					? first
					: TerminalTreeCheckState.Indeterminate;
			}

			_checkStates[node.FullPath] = state;
			if (node.IsDirectory && state != TerminalTreeCheckState.Unchecked)
				_selectedFolderCount++;
		}
	}

	private void IndexTree(TreeNodeDescriptor node, string? parentPath)
	{
		_nodesByPath[node.FullPath] = node;
		_parentsByPath[node.FullPath] = parentPath;
		_orderedPaths.Add(node.FullPath);
		foreach (var child in node.Children)
			IndexTree(child, node.FullPath);
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
