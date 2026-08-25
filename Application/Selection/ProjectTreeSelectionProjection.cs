using System.Collections.Frozen;

namespace DevProjex.Application.Selection;

/// <summary>
/// Projects checkbox state onto the effective tree without depending on realized UI nodes.
/// Directory selection includes its complete descriptor subtree; partial selection keeps only
/// selected descendants and the ancestor directories required to preserve their paths.
/// </summary>
public static class ProjectTreeSelectionProjection
{
	private static readonly IReadOnlySet<string> FullTreeSelection =
		Array.Empty<string>().ToFrozenSet(PathComparer.Default);

	/// <summary>
	/// Converts the two UI representations of the complete tree into the canonical
	/// empty-path representation consumed by preview, metrics, and export pipelines.
	/// Explicit checkbox state remains in the view model so users can still uncheck
	/// individual descendants after selecting the project root.
	/// </summary>
	public static IReadOnlySet<string> NormalizeSelectedPaths(
		TreeNodeDescriptor root,
		IReadOnlySet<string> selectedPaths)
	{
		ArgumentNullException.ThrowIfNull(root);
		ArgumentNullException.ThrowIfNull(selectedPaths);

		if (CoversWholeTree(root, selectedPaths))
			return FullTreeSelection;

		// Preserve the caller's sparse path set. Preview warmup and collection code use
		// it as a direct lookup index; traversing the full tree here makes a rare leaf
		// selection scale with every sibling in a wide workspace.
		return selectedPaths;
	}

	public static bool CoversWholeTree(
		TreeNodeDescriptor root,
		IReadOnlySet<string> selectedPaths)
	{
		ArgumentNullException.ThrowIfNull(root);
		ArgumentNullException.ThrowIfNull(selectedPaths);

		return selectedPaths.Count == 0 ||
		       selectedPaths.Contains(root.FullPath);
	}

	public static IReadOnlyList<TreeNodeDescriptor> BuildIncludedNodes(
		TreeNodeDescriptor root,
		IReadOnlySet<string> selectedPaths)
	{
		ArgumentNullException.ThrowIfNull(root);
		ArgumentNullException.ThrowIfNull(selectedPaths);

		var effectiveSelectedPaths = NormalizeSelectedPaths(root, selectedPaths);
		var included = new List<TreeNodeDescriptor>();
		var uniquePaths = new HashSet<string>(PathComparer.Default);
		VisitIncludedTree(
			root,
			effectiveSelectedPaths,
			ancestorSelected: effectiveSelectedPaths.Count == 0,
			node =>
			{
				if (uniquePaths.Add(node.FullPath))
					included.Add(node);
			});

		return included;
	}

	public static List<string> BuildOrderedSelectedFilePaths(
		TreeNodeDescriptor root,
		IReadOnlySet<string> selectedPaths,
		bool ensureExists = true)
	{
		ArgumentNullException.ThrowIfNull(root);
		ArgumentNullException.ThrowIfNull(selectedPaths);

		var effectiveSelectedPaths = NormalizeSelectedPaths(root, selectedPaths);
		var uniquePaths = new HashSet<string>(PathComparer.Default);
		VisitIncludedTree(
			root,
			effectiveSelectedPaths,
			ancestorSelected: effectiveSelectedPaths.Count == 0,
			node =>
			{
				if (!node.IsDirectory && (!ensureExists || File.Exists(node.FullPath)))
					uniquePaths.Add(node.FullPath);
			});

		var orderedPaths = new List<string>(uniquePaths);
		orderedPaths.Sort(PathComparer.Default);
		return orderedPaths;
	}

	internal static TreeNodeDescriptor? BuildProjectedTree(
		TreeNodeDescriptor root,
		IReadOnlySet<string> includedPaths)
	{
		if (!includedPaths.Contains(root.FullPath))
			return null;

		var projectedNodes = new Dictionary<TreeNodeDescriptor, TreeNodeDescriptor?>(
			ReferenceEqualityComparer.Instance);
		var stack = new Stack<(TreeNodeDescriptor Node, bool Visited)>();
		stack.Push((root, false));
		while (stack.Count > 0)
		{
			var (node, visited) = stack.Pop();
			if (!includedPaths.Contains(node.FullPath))
			{
				projectedNodes[node] = null;
				continue;
			}

			if (!visited && node.IsDirectory && node.Children.Count > 0)
			{
				stack.Push((node, true));
				for (var index = node.Children.Count - 1; index >= 0; index--)
					stack.Push((node.Children[index], false));
				continue;
			}

			if (!node.IsDirectory || node.Children.Count == 0)
			{
				projectedNodes[node] = node;
				continue;
			}

			var children = new List<TreeNodeDescriptor>(node.Children.Count);
			foreach (var child in node.Children)
			{
				if (projectedNodes[child] is { } projectedChild)
					children.Add(projectedChild);
			}
			projectedNodes[node] = node with { Children = children };
		}

		return projectedNodes[root];
	}

	public static void CollectSelectedFilePaths(
		TreeNodeDescriptor node,
		IReadOnlySet<string> selectedPaths,
		HashSet<string> uniquePaths,
		int maxCount,
		bool ensureExists)
	{
		var pending = new Stack<TreeNodeDescriptor>();
		pending.Push(node);
		while (pending.Count > 0 && uniquePaths.Count < maxCount)
		{
			var current = pending.Pop();
			if (selectedPaths.Contains(current.FullPath))
			{
				CollectAllFilePaths(current, uniquePaths, maxCount, ensureExists);
				continue;
			}

			if (!current.IsDirectory)
				continue;

			for (var index = current.Children.Count - 1; index >= 0; index--)
				pending.Push(current.Children[index]);
		}
	}

	private static bool VisitIncludedTree(
		TreeNodeDescriptor node,
		IReadOnlySet<string> selectedPaths,
		bool ancestorSelected,
		Action<TreeNodeDescriptor> include)
	{
		var pending = new List<SelectionTraversalFrame>
		{
			new(node, ancestorSelected || selectedPaths.Contains(node.FullPath))
		};
		var rootIncluded = false;
		while (pending.Count > 0)
		{
			var frameIndex = pending.Count - 1;
			var frame = pending[frameIndex];
			if (frame.Node.IsDirectory && frame.NextChildIndex < frame.Node.Children.Count)
			{
				var child = frame.Node.Children[frame.NextChildIndex++];
				pending[frameIndex] = frame;
				pending.Add(new SelectionTraversalFrame(
					child,
					frame.NodeSelected || selectedPaths.Contains(child.FullPath)));
				continue;
			}

			pending.RemoveAt(frameIndex);
			var isIncluded = frame.NodeSelected || frame.HasIncludedChild;
			if (isIncluded)
				include(frame.Node);

			if (pending.Count == 0)
			{
				rootIncluded = isIncluded;
				continue;
			}

			if (isIncluded)
			{
				var parentIndex = pending.Count - 1;
				var parent = pending[parentIndex];
				parent.HasIncludedChild = true;
				pending[parentIndex] = parent;
			}
		}

		return rootIncluded;
	}

	private static void CollectAllFilePaths(
		TreeNodeDescriptor node,
		HashSet<string> uniquePaths,
		int maxCount,
		bool ensureExists)
	{
		var stack = new Stack<TreeNodeDescriptor>();
		stack.Push(node);
		while (stack.Count > 0 && uniquePaths.Count < maxCount)
		{
			var current = stack.Pop();
			if (!current.IsDirectory)
			{
				if (!ensureExists || File.Exists(current.FullPath))
					uniquePaths.Add(current.FullPath);
				continue;
			}

			for (var index = current.Children.Count - 1; index >= 0; index--)
				stack.Push(current.Children[index]);
		}
	}

	private struct SelectionTraversalFrame(
		TreeNodeDescriptor node,
		bool nodeSelected)
	{
		public TreeNodeDescriptor Node { get; } = node;
		public bool NodeSelected { get; } = nodeSelected;
		public int NextChildIndex { get; set; }
		public bool HasIncludedChild { get; set; }
	}
}
