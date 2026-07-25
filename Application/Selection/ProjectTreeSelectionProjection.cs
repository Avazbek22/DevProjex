namespace DevProjex.Application.Selection;

/// <summary>
/// Projects checkbox state onto the effective tree without depending on realized UI nodes.
/// Directory selection includes its complete descriptor subtree; partial selection keeps only
/// selected descendants and the ancestor directories required to preserve their paths.
/// </summary>
public static class ProjectTreeSelectionProjection
{
	public static IReadOnlyList<TreeNodeDescriptor> BuildIncludedNodes(
		TreeNodeDescriptor root,
		IReadOnlySet<string> selectedPaths)
	{
		ArgumentNullException.ThrowIfNull(root);
		ArgumentNullException.ThrowIfNull(selectedPaths);

		var included = new List<TreeNodeDescriptor>();
		var uniquePaths = new HashSet<string>(PathComparer.Default);
		VisitIncludedTree(
			root,
			selectedPaths,
			ancestorSelected: selectedPaths.Count == 0,
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

		var uniquePaths = new HashSet<string>(PathComparer.Default);
		VisitIncludedTree(
			root,
			selectedPaths,
			ancestorSelected: selectedPaths.Count == 0,
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
