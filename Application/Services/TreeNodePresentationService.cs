namespace DevProjex.Application.Services;

public sealed class TreeNodePresentationService(LocalizationService localization, IIconMapper iconMapper)
{
	private const int RootParallelProjectionThreshold = 24;

	public TreeNodeDescriptor Build(FileSystemNode root)
	{
		return BuildWithCancellation(root, CancellationToken.None);
	}

	public TreeNodeDescriptor BuildWithCancellation(
		FileSystemNode root,
		CancellationToken cancellationToken)
	{
		return BuildNode(root, isRoot: true, orderedFilePaths: null, cancellationToken);
	}

	public TreeNodePresentationResult BuildWithFilePaths(FileSystemNode root)
	{
		return BuildWithFilePathsWithCancellation(root, CancellationToken.None);
	}

	public TreeNodePresentationResult BuildWithFilePathsWithCancellation(
		FileSystemNode root,
		CancellationToken cancellationToken)
	{
		var orderedFilePaths = new List<string>();
		var descriptor = BuildNode(root, isRoot: true, orderedFilePaths, cancellationToken);
		return new TreeNodePresentationResult(descriptor, orderedFilePaths);
	}

	private TreeNodeDescriptor BuildNode(
		FileSystemNode node,
		bool isRoot,
		List<string>? orderedFilePaths,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if (!isRoot)
			return BuildSubtree(node, orderedFilePaths, cancellationToken);

		var header = CreateHeader(node, orderedFilePaths);
		var children = BuildChildren(
			node.Children,
			allowParallelAtThisLevel: true,
			orderedFilePaths,
			cancellationToken);
		return CreateDescriptor(header, children);
	}

	private IReadOnlyList<TreeNodeDescriptor> BuildChildren(
		IReadOnlyList<FileSystemNode> children,
		bool allowParallelAtThisLevel,
		List<string>? orderedFilePaths,
		CancellationToken cancellationToken)
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
					CancellationToken = cancellationToken,
					MaxDegreeOfParallelism = Math.Min(ScanParallelismPolicy.MaxDegreeOfParallelism, children.Count)
				},
				index =>
				{
					var localFilePaths = orderedFilePaths is null ? null : new List<string>();
					projectedChildren[index] = BuildSubtree(children[index], localFilePaths, cancellationToken);
					if (localFilePaths is { Count: > 0 })
						orderedFilePathSegments![index] = localFilePaths;
				});

			if (orderedFilePathSegments is not null)
			{
				foreach (var segment in orderedFilePathSegments)
				{
					cancellationToken.ThrowIfCancellationRequested();
					if (segment is not null)
						orderedFilePaths!.AddRange(segment);
				}
			}

			return projectedChildren;
		}

		var projected = new List<TreeNodeDescriptor>(children.Count);
		foreach (var child in children)
			projected.Add(BuildSubtree(child, orderedFilePaths, cancellationToken));

		return projected;
	}

	private TreeNodeDescriptor BuildSubtree(
		FileSystemNode root,
		List<string>? orderedFilePaths,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		var pending = new List<PresentationFrame>
		{
			new(CreateHeader(root, orderedFilePaths))
		};
		while (pending.Count > 0)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var frameIndex = pending.Count - 1;
			var frame = pending[frameIndex];
			if (frame.NextChildIndex < frame.Header.Node.Children.Count)
			{
				var child = frame.Header.Node.Children[frame.NextChildIndex++];
				pending[frameIndex] = frame;
				pending.Add(new PresentationFrame(CreateHeader(child, orderedFilePaths)));
				continue;
			}

			pending.RemoveAt(frameIndex);
			var descriptor = CreateDescriptor(frame.Header, frame.Children);
			if (pending.Count == 0)
				return descriptor;
			var parentIndex = pending.Count - 1;
			var parent = pending[parentIndex];
			parent.AddChild(descriptor);
			pending[parentIndex] = parent;
		}

		throw new InvalidOperationException("Tree projection did not produce a root node.");
	}

	private PresentationHeader CreateHeader(
		FileSystemNode node,
		List<string>? orderedFilePaths)
	{
		if (!node.IsDirectory)
			orderedFilePaths?.Add(node.FullPath);

		return new PresentationHeader(
			node,
			node.IsAccessDenied
				? $"{node.Name} [{localization["Tree.AccessDenied"]}]"
				: node.Name,
			iconMapper.GetIconKey(node));
	}

	private static TreeNodeDescriptor CreateDescriptor(
		PresentationHeader header,
		IReadOnlyList<TreeNodeDescriptor> children) =>
		new(
			header.DisplayName,
			header.Node.FullPath,
			header.Node.IsDirectory,
			header.Node.IsAccessDenied,
			header.IconKey,
			children);

	private struct PresentationFrame
	{
		private List<TreeNodeDescriptor>? _children;
		private readonly int _childCapacity;

		public PresentationFrame(PresentationHeader header)
		{
			Header = header;
			_childCapacity = header.Node.Children.Count;
		}

		public PresentationHeader Header { get; }
		public IReadOnlyList<TreeNodeDescriptor> Children => _children ?? [];
		public int NextChildIndex { get; set; }

		public void AddChild(TreeNodeDescriptor child) =>
			(_children ??= new List<TreeNodeDescriptor>(_childCapacity)).Add(child);
	}

	private readonly record struct PresentationHeader(
		FileSystemNode Node,
		string DisplayName,
		string IconKey);
}
