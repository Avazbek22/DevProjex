namespace DevProjex.Application.Services;

public sealed class TreeNodePresentationService(LocalizationService localization, IIconMapper iconMapper)
{
	private const int RootParallelProjectionThreshold = 24;

	public TreeNodeDescriptor Build(FileSystemNode root)
	{
		return BuildNode(root, isRoot: true, orderedFilePaths: null);
	}

	public TreeNodePresentationResult BuildWithFilePaths(FileSystemNode root)
	{
		var orderedFilePaths = new List<string>();
		var descriptor = BuildNode(root, isRoot: true, orderedFilePaths: orderedFilePaths);
		return new TreeNodePresentationResult(descriptor, orderedFilePaths);
	}

	private TreeNodeDescriptor BuildNode(
		FileSystemNode node,
		bool isRoot,
		List<string>? orderedFilePaths)
	{
		var displayName = node.IsAccessDenied
			? (isRoot ? localization["Tree.AccessDeniedRoot"] : localization["Tree.AccessDenied"])
			: node.Name;

		var iconKey = iconMapper.GetIconKey(node);
		if (!node.IsDirectory)
			orderedFilePaths?.Add(node.FullPath);

		var children = BuildChildren(node.Children, allowParallelAtThisLevel: isRoot, orderedFilePaths: orderedFilePaths);

		return new TreeNodeDescriptor(
			DisplayName: displayName,
			FullPath: node.FullPath,
			IsDirectory: node.IsDirectory,
			IsAccessDenied: node.IsAccessDenied,
			IconKey: iconKey,
			Children: children);
	}

	private IReadOnlyList<TreeNodeDescriptor> BuildChildren(
		IReadOnlyList<FileSystemNode> children,
		bool allowParallelAtThisLevel,
		List<string>? orderedFilePaths)
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
			var orderedFilePathSegments = orderedFilePaths is null
				? null
				: new List<string>?[children.Count];
			Parallel.For(
				0,
				children.Count,
				new ParallelOptions
				{
					MaxDegreeOfParallelism = Math.Min(ScanParallelismPolicy.MaxDegreeOfParallelism, children.Count)
				},
				index =>
				{
					var localFilePaths = orderedFilePaths is null ? null : new List<string>();
					projectedChildren[index] = BuildNode(children[index], isRoot: false, orderedFilePaths: localFilePaths);
					if (localFilePaths is { Count: > 0 })
						orderedFilePathSegments![index] = localFilePaths;
				});

			if (orderedFilePathSegments is not null)
			{
				foreach (var segment in orderedFilePathSegments)
				{
					if (segment is not null)
						orderedFilePaths!.AddRange(segment);
				}
			}

			return projectedChildren;
		}

		var projected = new List<TreeNodeDescriptor>(children.Count);
		foreach (var child in children)
			projected.Add(BuildNode(child, isRoot: false, orderedFilePaths: orderedFilePaths));

		return projected;
	}
}
