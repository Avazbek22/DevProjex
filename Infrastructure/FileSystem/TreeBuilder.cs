namespace DevProjex.Infrastructure.FileSystem;

public sealed class TreeBuilder : ITreeBuilder
{
	// Pre-allocated comparer instance to avoid allocation per sort
	private static readonly FileSystemTreeEntryComparer EntryComparer = new();
	private static readonly int RootBuildParallelism =
		Math.Clamp(Environment.ProcessorCount, min: 2, max: 16);

	public TreeBuildResult Build(string rootPath, TreeFilterOptions options, CancellationToken cancellationToken = default)
	{
		var state = new BuildState();

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
			options: options,
			isRoot: true,
			state: state,
			cancellationToken: cancellationToken);

		return new TreeBuildResult(root, state.RootAccessDenied, state.HadAccessDenied);
	}

	private static void BuildChildren(
		FileSystemNode parent,
		string path,
		TreeFilterOptions options,
		bool isRoot,
		BuildState state,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();

		var entries = new List<FileSystemTreeEntry>(capacity: 32);
		try
		{
			// Tree building is on the project-load hot path, so avoid FileSystemInfo materialization.
			// The lightweight entry snapshots already carry the metadata we need for sorting/filtering.
			foreach (var entry in FileSystemEntryEnumerator.EnumerateEntries(path))
				entries.Add(entry);
			entries.Sort(EntryComparer);
		}
		catch (UnauthorizedAccessException)
		{
			if (isRoot)
				state.MarkRootAccessDenied();
			else
				state.MarkAccessDenied();
			parent.IsAccessDenied = true;
			return;
		}
		catch
		{
			return;
		}

		var children = (List<FileSystemNode>)parent.Children;
		var hasNameFilter = !string.IsNullOrWhiteSpace(options.NameFilter);
		var shouldApplySmartIgnoreForFiles = options.IgnoreRules.ShouldApplySmartIgnore(path, isDirectory: true);

		if (isRoot && entries.Count > 1)
		{
			BuildRootChildrenInParallel(
				entries,
				children,
				options,
				hasNameFilter,
				shouldApplySmartIgnoreForFiles,
				state,
				cancellationToken);
			return;
		}

		foreach (var entry in entries)
		{
			cancellationToken.ThrowIfCancellationRequested();

			var node = BuildNodeForEntry(
				entry,
				options,
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
		bool hasNameFilter,
		bool shouldApplySmartIgnoreForFiles,
		BuildState state,
		CancellationToken cancellationToken)
	{
		var nodes = new FileSystemNode?[entries.Count];
		var maxDegree = Math.Min(RootBuildParallelism, entries.Count);
		var parallelOptions = new ParallelOptions
		{
			MaxDegreeOfParallelism = maxDegree,
			CancellationToken = cancellationToken
		};

		Parallel.For(0, entries.Count, parallelOptions, i =>
		{
			var entry = entries[i];
			nodes[i] = BuildNodeForEntry(
				entry,
				options,
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
				? ignore.EvaluateGitIgnore(entry.FullPath, isDirectory: true, name)
				: IgnoreRules.GitIgnoreEvaluation.NotIgnored;
			if (ShouldSkipDirectory(entry, ignore, directoryGitIgnore))
				return null;

			var dirNode = new FileSystemNode(
				name: name,
				fullPath: entry.FullPath,
				isDirectory: true,
				isAccessDenied: false,
				children: new List<FileSystemNode>());

			BuildChildren(dirNode, entry.FullPath, options, isRoot: false, state, cancellationToken);

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
			? ignore.EvaluateGitIgnore(entry.FullPath, isDirectory: false, name)
			: IgnoreRules.GitIgnoreEvaluation.NotIgnored;
		if (ShouldSkipFile(entry, ignore, shouldApplySmartIgnoreForFiles, fileGitIgnore))
			return null;

		if (IsExtensionlessFileName(name))
		{
			// Extensionless files are intentionally controlled only by ignore options.
		}
		else
		{
			if (options.AllowedExtensions.Count == 0)
				return null;

			var ext = Path.GetExtension(name);
			if (!options.AllowedExtensions.Contains(ext))
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

		if (rules.ShouldApplySmartIgnore(entry.FullPath, isDirectory: true) && rules.SmartIgnoredFolders.Contains(entry.Name))
			return true;

		if (rules.IgnoreDotFolders && entry.Name.StartsWith(".", StringComparison.Ordinal))
			return true;

		if (rules.IgnoreHiddenFolders && entry.IsHidden)
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

		if (shouldApplySmartIgnoreForFiles && rules.SmartIgnoredFiles.Contains(entry.Name))
			return true;

		if (rules.IgnoreDotFiles && entry.Name.StartsWith(".", StringComparison.Ordinal))
			return true;

		if (rules.IgnoreExtensionlessFiles && IsExtensionlessFileName(entry.Name))
			return true;

		if (rules.IgnoreEmptyFiles && entry.Length == 0)
			return true;

		if (rules.IgnoreHiddenFiles && entry.IsHidden)
			return true;

		return false;
	}

	private static bool IsExtensionlessFileName(string fileName)
	{
		if (string.IsNullOrWhiteSpace(fileName))
			return false;

		var extension = Path.GetExtension(fileName);
		return string.IsNullOrEmpty(extension) || extension == ".";
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

	/// <summary>
	/// Sorts lightweight tree entries without allocating FileSystemInfo wrappers.
	/// The tree keeps directories first to match the long-standing UI contract.
	/// </summary>
	private sealed class FileSystemTreeEntryComparer : IComparer<FileSystemTreeEntry>
	{
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public int Compare(FileSystemTreeEntry x, FileSystemTreeEntry y)
		{
			if (x.IsDirectory != y.IsDirectory)
				return x.IsDirectory ? -1 : 1;

			// Then by name, case-insensitive ordinal (fastest string comparison)
			return string.Compare(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);
		}
	}
}
