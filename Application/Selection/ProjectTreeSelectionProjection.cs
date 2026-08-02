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

	public static void CollectSelectedFilePaths(
		TreeNodeDescriptor node,
		IReadOnlySet<string> selectedPaths,
		HashSet<string> uniquePaths,
		int maxCount,
		bool ensureExists)
	{
		if (uniquePaths.Count >= maxCount)
			return;

		if (selectedPaths.Contains(node.FullPath))
		{
			CollectAllFilePaths(node, uniquePaths, maxCount, ensureExists);
			return;
		}

		if (!node.IsDirectory)
			return;

		foreach (var child in node.Children)
		{
			CollectSelectedFilePaths(child, selectedPaths, uniquePaths, maxCount, ensureExists);
			if (uniquePaths.Count >= maxCount)
				break;
		}
	}

	private static bool VisitIncludedTree(
		TreeNodeDescriptor node,
		IReadOnlySet<string> selectedPaths,
		bool ancestorSelected,
		Action<TreeNodeDescriptor> include)
	{
		var nodeSelected = ancestorSelected || selectedPaths.Contains(node.FullPath);
		if (!node.IsDirectory)
		{
			if (nodeSelected)
				include(node);

			return nodeSelected;
		}

		var hasIncludedChild = false;
		foreach (var child in node.Children)
		{
			if (VisitIncludedTree(child, selectedPaths, nodeSelected, include))
				hasIncludedChild = true;
		}

		if (!nodeSelected && !hasIncludedChild)
			return false;

		include(node);
		return true;
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
}
