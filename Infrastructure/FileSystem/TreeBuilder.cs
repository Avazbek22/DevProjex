namespace DevProjex.Infrastructure.FileSystem;

public sealed class TreeBuilder : ITreeBuilder
{
	public TreeBuildResult Build(string rootPath, TreeFilterOptions options, CancellationToken cancellationToken = default)
	{
		var state = new BuildState();
		var extensionLookup = new AllowedExtensionLookup(options.AllowedExtensions);
		var gitIgnoreContext = options.IgnoreRules.CreateGitIgnoreScanContext(rootPath);

		var rootInfo = new DirectoryInfo(rootPath);
		var root = new FileSystemNode(
			name: rootInfo.Name,
			fullPath: rootPath,
			isDirectory: true,
			isAccessDenied: false,
			children: new List<FileSystemNode>());

		BuildChildren(
			parent: root,
			path: rootPath,
			relativePath: string.Empty,
			options: options,
			allowedExtensions: extensionLookup,
			gitIgnoreContext: gitIgnoreContext,
			isRoot: true,
			state: state,
			cancellationToken: cancellationToken);

		return new TreeBuildResult(root, state.RootAccessDenied, state.HadAccessDenied);
	}

	private static void BuildChildren(
		FileSystemNode parent,
		string path,
		string relativePath,
		TreeFilterOptions options,
		AllowedExtensionLookup allowedExtensions,
		IgnoreRules.GitIgnoreScanContext gitIgnoreContext,
		bool isRoot,
		BuildState state,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();

		var snapshot = ProjectInventorySnapshot.ReadDirectory(path, relativePath, isRoot, cancellationToken);
		if (snapshot.HadAccessDenied)
		{
			if (snapshot.RootAccessDenied)
				state.MarkRootAccessDenied();
			else
				state.MarkAccessDenied();
			parent.IsAccessDenied = true;
			return;
		}

		var children = (List<FileSystemNode>)parent.Children;
		var hasNameFilter = !string.IsNullOrWhiteSpace(options.NameFilter);
		var shouldApplySmartIgnoreForFiles = options.IgnoreRules.ShouldApplySmartIgnore(path, isDirectory: true);

		if (isRoot)
		{
			BuildRootChildrenInParallel(
				snapshot.Entries,
				children,
				options,
				allowedExtensions,
				gitIgnoreContext,
				hasNameFilter,
				shouldApplySmartIgnoreForFiles,
				state,
				cancellationToken);
			return;
		}

		foreach (var entry in snapshot.Entries)
		{
			cancellationToken.ThrowIfCancellationRequested();

			var node = BuildNodeForEntry(
				entry,
				options,
				allowedExtensions,
				gitIgnoreContext,
				isRoot,
				hasNameFilter,
				shouldApplySmartIgnoreForFiles,
				state,
				cancellationToken);
			if (node is not null)
				children.Add(node);
		}
	}

	private static void BuildRootChildrenInParallel(
		IReadOnlyList<FileSystemTreeEntry> entries,
		List<FileSystemNode> children,
		TreeFilterOptions options,
		AllowedExtensionLookup allowedExtensions,
		IgnoreRules.GitIgnoreScanContext gitIgnoreContext,
		bool hasNameFilter,
		bool shouldApplySmartIgnoreForFiles,
		BuildState state,
		CancellationToken cancellationToken)
	{
		var nodes = new FileSystemNode?[entries.Count];
		var parallelOptions = ScanParallelismPolicy.CreateOptions(cancellationToken);

		Parallel.For(0, entries.Count, parallelOptions, i =>
		{
			var entry = entries[i];
			nodes[i] = BuildNodeForEntry(
				entry,
				options,
				allowedExtensions,
				gitIgnoreContext,
				isRoot: true,
				hasNameFilter,
				shouldApplySmartIgnoreForFiles,
				state,
				parallelOptions.CancellationToken);
		});

		for (var i = 0; i < nodes.Length; i++)
		{
			var node = nodes[i];
			if (node is not null)
				children.Add(node);
		}
	}

	private static FileSystemNode? BuildNodeForEntry(
		FileSystemTreeEntry entry,
		TreeFilterOptions options,
		AllowedExtensionLookup allowedExtensions,
		IgnoreRules.GitIgnoreScanContext gitIgnoreContext,
		bool isRoot,
		bool hasNameFilter,
		bool shouldApplySmartIgnoreForFiles,
		BuildState state,
		CancellationToken cancellationToken)
	{
		var name = entry.Name;
		bool isDir = entry.IsDirectory;

		if (isDir && isRoot && !options.AllowedRootFolders.Contains(name))
			return null;

		var ignore = options.IgnoreRules;
		if (isDir)
		{
			var directoryGitIgnore = ignore.UseGitIgnore
				? gitIgnoreContext.Evaluate(entry.FullPath, entry.RelativePath, isDirectory: true, name)
				: IgnoreRules.GitIgnoreEvaluation.NotIgnored;
			if (ShouldSkipDirectory(entry, ignore, directoryGitIgnore))
				return null;

			var dirNode = new FileSystemNode(
				name: name,
				fullPath: entry.FullPath,
				isDirectory: true,
				isAccessDenied: false,
				children: new List<FileSystemNode>());

			BuildChildren(
				dirNode,
				entry.FullPath,
				entry.RelativePath,
				options,
				allowedExtensions,
				gitIgnoreContext,
				isRoot: false,
				state,
				cancellationToken);

			if (ignore.IgnoreEmptyFolders &&
			    dirNode.Children.Count == 0 &&
			    !dirNode.IsAccessDenied)
			{
				return null;
			}

			// Keep full directory context when extension/ignore filters remove all files.
			// Name filter remains strict to preserve intentional narrowing behavior.
			if (hasNameFilter)
			{
				bool hasMatchingChildren = dirNode.Children.Count > 0;
				bool matchesName = name.Contains(options.NameFilter!, StringComparison.OrdinalIgnoreCase);
				return (hasMatchingChildren || matchesName) ? dirNode : null;
			}

			// Keep ignored directories out of UI when traversal found no visible descendants.
			// Parents that only became empty after descendant filtering remain visible until
			// IgnoreEmptyFolders explicitly removes them.
			if (directoryGitIgnore.IsIgnored &&
			    directoryGitIgnore.ShouldTraverseIgnoredDirectory &&
			    dirNode.Children.Count == 0 &&
			    !dirNode.IsAccessDenied)
			{
				return null;
			}

			return dirNode;
		}

		var fileGitIgnore = ignore.UseGitIgnore
			? gitIgnoreContext.Evaluate(entry.FullPath, entry.RelativePath, isDirectory: false, name)
			: IgnoreRules.GitIgnoreEvaluation.NotIgnored;
		if (ShouldSkipFile(entry, ignore, shouldApplySmartIgnoreForFiles, fileGitIgnore))
			return null;

		if (IsExtensionlessFileName(name))
		{
			// Extensionless files are intentionally controlled only by ignore options.
		}
		else
		{
			if (allowedExtensions.IsEmpty)
				return null;

			if (!allowedExtensions.AllowsFileName(name))
				return null;
		}

		if (hasNameFilter && !name.Contains(options.NameFilter!, StringComparison.OrdinalIgnoreCase))
			return null;

		return new FileSystemNode(
			name: name,
			fullPath: entry.FullPath,
			isDirectory: false,
			isAccessDenied: false,
			children: FileSystemNode.EmptyChildren);
	}

	private static bool ShouldSkipDirectory(
		FileSystemTreeEntry entry,
		IgnoreRules rules,
		in IgnoreRules.GitIgnoreEvaluation gitIgnoreEvaluation)
	{
		if (gitIgnoreEvaluation.IsIgnored)
		{
			if (!gitIgnoreEvaluation.ShouldTraverseIgnoredDirectory)
				return true;
		}

		if (rules.IsSmartIgnoredDirectory(entry.FullPath, entry.Name))
			return true;

		var isDot = IgnoreRuleSemantics.IsDotName(entry.Name);
		if (IgnoreRuleSemantics.ShouldIgnoreDotDirectory(rules.IgnoreDotFolders, isDot))
			return true;

		if (IgnoreRuleSemantics.ShouldIgnoreHiddenDirectory(
			    rules.IgnoreHiddenFolders,
			    entry.IsHidden,
			    isDot,
			    rules.IgnoreDotFolders))
			return true;

		return false;
	}

	private static bool ShouldSkipFile(
		FileSystemTreeEntry entry,
		IgnoreRules rules,
		bool shouldApplySmartIgnoreForFiles,
		in IgnoreRules.GitIgnoreEvaluation gitIgnoreEvaluation)
	{
		if (gitIgnoreEvaluation.IsIgnored)
			return true;

		if (rules.IsSmartIgnoredFile(entry.FullPath, entry.Name, shouldApplySmartIgnoreForFiles))
			return true;

		var isDot = IgnoreRuleSemantics.IsDotName(entry.Name);
		if (IgnoreRuleSemantics.ShouldIgnoreDotFile(rules.IgnoreDotFiles, isDot))
			return true;

		if (rules.IgnoreExtensionlessFiles && IsExtensionlessFileName(entry.Name))
			return true;

		if (rules.IgnoreEmptyFiles && entry.Length == 0)
			return true;

		if (IgnoreRuleSemantics.ShouldIgnoreHiddenFile(
			    rules.IgnoreHiddenFiles,
			    entry.IsHidden,
			    isDot,
			    rules.IgnoreDotFiles))
			return true;

		return false;
	}

	private static bool IsExtensionlessFileName(string fileName)
	{
		if (string.IsNullOrWhiteSpace(fileName))
			return false;

		var dotIndex = fileName.AsSpan().LastIndexOf('.');
		if (dotIndex <= 0)
			return dotIndex != 0;

		return dotIndex == fileName.Length - 1;
	}

	private sealed class BuildState
	{
		private int _rootAccessDenied;
		private int _hadAccessDenied;

		public bool RootAccessDenied => Volatile.Read(ref _rootAccessDenied) == 1;
		public bool HadAccessDenied => Volatile.Read(ref _hadAccessDenied) == 1;

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void MarkRootAccessDenied()
		{
			Interlocked.Exchange(ref _rootAccessDenied, 1);
			Interlocked.Exchange(ref _hadAccessDenied, 1);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void MarkAccessDenied()
		{
			Interlocked.Exchange(ref _hadAccessDenied, 1);
		}
	}

	private readonly struct AllowedExtensionLookup(IReadOnlySet<string> allowedExtensions)
	{
		private readonly HashSet<string>? _hashSet = allowedExtensions as HashSet<string>;

		public bool IsEmpty => allowedExtensions.Count == 0;

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public bool AllowsFileName(string fileName)
		{
			var extension = Path.GetExtension(fileName.AsSpan());
			if (extension.IsWhiteSpace())
				return false;

			if (_hashSet is not null &&
			    _hashSet.TryGetAlternateLookup<ReadOnlySpan<char>>(out var lookup))
			{
				return lookup.Contains(extension);
			}

			return allowedExtensions.Contains(extension.ToString());
		}
	}
}
