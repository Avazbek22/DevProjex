namespace DevProjex.Application.Services;

public sealed class TreeNodePresentationService(LocalizationService localization, IIconMapper iconMapper)
{
	private const int RootParallelProjectionThreshold = 24;

	public TreeNodeDescriptor Build(FileSystemNode root)
	{
		return BuildNode(root, isRoot: true);
	}

	private TreeNodeDescriptor BuildNode(FileSystemNode node, bool isRoot)
	{
		var displayName = node.IsAccessDenied
			? (isRoot ? localization["Tree.AccessDeniedRoot"] : localization["Tree.AccessDenied"])
			: node.Name;

		var iconKey = iconMapper.GetIconKey(node);

		var children = BuildChildren(node.Children, allowParallelAtThisLevel: isRoot);

		return new TreeNodeDescriptor(
			DisplayName: displayName,
			FullPath: node.FullPath,
			IsDirectory: node.IsDirectory,
			IsAccessDenied: node.IsAccessDenied,
			IconKey: iconKey,
			Children: children);
	}

	private List<TreeNodeDescriptor> BuildChildren(
		IReadOnlyList<FileSystemNode> children,
		bool allowParallelAtThisLevel)
	{
		if (children.Count == 0)
			return [];

		// Descriptor projection is a full second pass over the tree after filesystem scan.
		// Parallelizing only the first level keeps the implementation predictable while
		// shaving CPU time on large workspaces with many top-level branches.
		if (allowParallelAtThisLevel && children.Count >= RootParallelProjectionThreshold)
		{
			// Parallel descriptor projection is only profitable for broad roots.
			// Small projects are faster on the current thread because Task/worker setup
			// dominates the actual node mapping cost.
			var projectedChildren = new TreeNodeDescriptor[children.Count];
			Parallel.For(
				0,
				children.Count,
				new ParallelOptions
				{
					MaxDegreeOfParallelism = Math.Min(ScanParallelismPolicy.MaxDegreeOfParallelism, children.Count)
				},
				index => projectedChildren[index] = BuildNode(children[index], isRoot: false));

			return [.. projectedChildren];
		}

		var projected = new List<TreeNodeDescriptor>(children.Count);
		foreach (var child in children)
			projected.Add(BuildNode(child, isRoot: false));

		return projected;
	}
}
