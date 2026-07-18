namespace DevProjex.Infrastructure.FileSystem;

internal static class ProjectTreeInventoryScanner
{
	private const int RootSubtreeParallelThreshold = 4;

	public static ProjectTreeInventorySnapshot Read(
		string rootPath,
		Func<FileSystemTreeEntry, bool, bool> shouldTraverseDirectory,
		CancellationToken cancellationToken) =>
		Read(
			rootPath,
			ProjectTreeGitIgnoreContexts.Disabled,
			(entry, isProjectRootChild, _) => shouldTraverseDirectory(entry, isProjectRootChild),
			cancellationToken);

	public static ProjectTreeInventorySnapshot Read(
		string rootPath,
		ProjectTreeGitIgnoreContexts initialGitIgnoreContexts,
		Func<FileSystemTreeEntry, bool, ProjectTreeGitIgnoreContexts, bool> shouldTraverseDirectory,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();

		var rootName = Path.GetFileName(rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
		if (string.IsNullOrEmpty(rootName))
			rootName = rootPath;

		var entries = new List<ProjectTreeInventoryEntry>(capacity: 256)
		{
			new(
				rootName,
				rootPath,
				relativePath: string.Empty,
				parentIndex: -1,
				isDirectory: true,
				isHidden: SafeHasHiddenAttribute(rootPath),
				length: 0)
		};

		if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
			return new ProjectTreeInventorySnapshot(entries, rootAccessDenied: false, hadAccessDenied: false);

		var rootAccessDenied = false;
		var hadAccessDenied = false;
		List<FileSystemTreeEntry> rootChildren;
		try
		{
			rootChildren = ReadDirectoryEntries(rootPath, relativePath: string.Empty, cancellationToken);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (UnauthorizedAccessException)
		{
			MarkAccessDenied(entries, 0);
			return new ProjectTreeInventorySnapshot(entries, rootAccessDenied: true, hadAccessDenied: true);
		}
		catch
		{
			return new ProjectTreeInventorySnapshot(entries, rootAccessDenied: false, hadAccessDenied: false);
		}

		var discoveredGitIgnoreMatchers = new List<ScopedGitIgnoreMatcher>();
		var rootGitIgnoreContexts = initialGitIgnoreContexts.EnterDirectory(
			rootPath,
			directoryRelativePath: string.Empty,
			FindGitIgnorePath(rootChildren),
			discoveredGitIgnoreMatchers);
		var rootDirectoryChildren = AddProjectRootChildren(
			entries,
			rootChildren,
			rootGitIgnoreContexts,
			shouldTraverseDirectory,
			cancellationToken);

		if (rootDirectoryChildren.Count == 0)
		{
			return new ProjectTreeInventorySnapshot(
				entries,
				rootAccessDenied,
				hadAccessDenied,
				discoveredGitIgnoreMatchers);
		}

		var subtreeResults = new SubtreeScanResult[rootDirectoryChildren.Count];
		if (rootDirectoryChildren.Count < RootSubtreeParallelThreshold)
		{
			for (var index = 0; index < rootDirectoryChildren.Count; index++)
			{
				cancellationToken.ThrowIfCancellationRequested();
				var rootChildIndex = rootDirectoryChildren[index];
				subtreeResults[index] = ReadSubtree(
					rootChildIndex,
					entries[rootChildIndex],
					rootGitIgnoreContexts,
					shouldTraverseDirectory,
					cancellationToken);
			}
		}
		else
		{
			var parallelOptions = ScanParallelismPolicy.CreateOptions(cancellationToken);
			Parallel.For(0, rootDirectoryChildren.Count, parallelOptions, index =>
			{
				var rootChildIndex = rootDirectoryChildren[index];
				subtreeResults[index] = ReadSubtree(
					rootChildIndex,
					entries[rootChildIndex],
					rootGitIgnoreContexts,
					shouldTraverseDirectory,
					parallelOptions.CancellationToken);
			});
		}

		foreach (var result in subtreeResults)
		{
			if (result.HadAccessDenied)
				hadAccessDenied = true;

			MergeSubtree(entries, result);
			discoveredGitIgnoreMatchers.AddRange(result.DiscoveredGitIgnoreMatchers);
		}

		var uniqueMatchers = MergeDiscoveredGitIgnoreMatchers(discoveredGitIgnoreMatchers);
		return new ProjectTreeInventorySnapshot(
			entries,
			rootAccessDenied,
			hadAccessDenied,
			uniqueMatchers);
	}

	private static List<int> AddProjectRootChildren(
		List<ProjectTreeInventoryEntry> entries,
		IReadOnlyList<FileSystemTreeEntry> rootChildren,
		ProjectTreeGitIgnoreContexts gitIgnoreContexts,
		Func<FileSystemTreeEntry, bool, ProjectTreeGitIgnoreContexts, bool> shouldTraverseDirectory,
		CancellationToken cancellationToken)
	{
		var firstChildIndex = entries.Count;
		var directoryIndices = new List<int>();
		foreach (var child in rootChildren)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (child.IsDirectory && !shouldTraverseDirectory(child, true, gitIgnoreContexts))
				continue;

			var childIndex = entries.Count;
			entries.Add(new ProjectTreeInventoryEntry(
				child.Name,
				child.FullPath,
				child.RelativePath,
				parentIndex: 0,
				child.IsDirectory,
				child.IsHidden,
				child.Length));

			if (child.IsDirectory)
				directoryIndices.Add(childIndex);
		}

		if (entries.Count == firstChildIndex)
			return directoryIndices;

		var root = entries[0];
		root.FirstChildIndex = firstChildIndex;
		root.ChildCount = entries.Count - firstChildIndex;
		entries[0] = root;
		return directoryIndices;
	}

	private static SubtreeScanResult ReadSubtree(
		int rootGlobalIndex,
		ProjectTreeInventoryEntry rootEntry,
		ProjectTreeGitIgnoreContexts inheritedGitIgnoreContexts,
		Func<FileSystemTreeEntry, bool, ProjectTreeGitIgnoreContexts, bool> shouldTraverseDirectory,
		CancellationToken cancellationToken)
	{
		var entries = new List<ProjectTreeInventoryEntry>(capacity: 128)
		{
			rootEntry
		};
		var hadAccessDenied = false;
		var discoveredGitIgnoreMatchers = new List<ScopedGitIgnoreMatcher>();
		var pendingDirectories = new Stack<(int Index, ProjectTreeGitIgnoreContexts GitIgnoreContexts)>();
		pendingDirectories.Push((0, inheritedGitIgnoreContexts));

		while (pendingDirectories.Count > 0)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var (parentIndex, parentGitIgnoreContexts) = pendingDirectories.Pop();
			var parent = entries[parentIndex];

			List<FileSystemTreeEntry> childEntries;
			try
			{
				childEntries = ReadDirectoryEntries(parent.FullPath, parent.RelativePath, cancellationToken);
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch (UnauthorizedAccessException)
			{
				MarkAccessDenied(entries, parentIndex);
				hadAccessDenied = true;
				continue;
			}
			catch
			{
				continue;
			}

			if (childEntries.Count == 0)
				continue;

			var gitIgnoreContexts = parentGitIgnoreContexts.EnterDirectory(
				parent.FullPath,
				parent.RelativePath,
				FindGitIgnorePath(childEntries),
				discoveredGitIgnoreMatchers);

			var firstChildIndex = entries.Count;
			var childDirectoryIndices = new List<int>();
			foreach (var child in childEntries)
			{
				cancellationToken.ThrowIfCancellationRequested();
				if (child.IsDirectory && !shouldTraverseDirectory(child, false, gitIgnoreContexts))
					continue;

				var childIndex = entries.Count;
				entries.Add(new ProjectTreeInventoryEntry(
					child.Name,
					child.FullPath,
					child.RelativePath,
					parentIndex,
					child.IsDirectory,
					child.IsHidden,
					child.Length));

				if (child.IsDirectory)
					childDirectoryIndices.Add(childIndex);
			}

			if (entries.Count == firstChildIndex)
				continue;

			parent.FirstChildIndex = firstChildIndex;
			parent.ChildCount = entries.Count - firstChildIndex;
			entries[parentIndex] = parent;

			for (var index = childDirectoryIndices.Count - 1; index >= 0; index--)
				pendingDirectories.Push((childDirectoryIndices[index], gitIgnoreContexts));
		}

		return new SubtreeScanResult(
			rootGlobalIndex,
			entries,
			hadAccessDenied,
			discoveredGitIgnoreMatchers);
	}

	private static void MergeSubtree(
		List<ProjectTreeInventoryEntry> targetEntries,
		SubtreeScanResult subtree)
	{
		if (subtree.Entries.Count == 0)
			return;

		var root = subtree.Entries[0];
		var rootGlobalIndex = subtree.RootGlobalIndex;

		if (subtree.Entries.Count == 1)
		{
			targetEntries[rootGlobalIndex] = root;
			return;
		}

		var globalBaseIndex = targetEntries.Count;
		if (root.FirstChildIndex >= 0)
			root.FirstChildIndex = globalBaseIndex + root.FirstChildIndex - 1;
		targetEntries[rootGlobalIndex] = root;

		for (var localIndex = 1; localIndex < subtree.Entries.Count; localIndex++)
		{
			var entry = subtree.Entries[localIndex];
			entry = RemapSubtreeEntry(entry, rootGlobalIndex, globalBaseIndex);
			targetEntries.Add(entry);
		}
	}

	private static ProjectTreeInventoryEntry RemapSubtreeEntry(
		ProjectTreeInventoryEntry entry,
		int rootGlobalIndex,
		int globalBaseIndex)
	{
		var parentIndex = entry.ParentIndex == 0
			? rootGlobalIndex
			: globalBaseIndex + entry.ParentIndex - 1;

		var remapped = new ProjectTreeInventoryEntry(
			entry.Name,
			entry.FullPath,
			entry.RelativePath,
			parentIndex,
			entry.IsDirectory,
			entry.IsHidden,
			entry.Length)
		{
			IsAccessDenied = entry.IsAccessDenied,
			ChildCount = entry.ChildCount,
			FirstChildIndex = entry.FirstChildIndex < 0
				? -1
				: globalBaseIndex + entry.FirstChildIndex - 1
		};

		return remapped;
	}

	private static List<FileSystemTreeEntry> ReadDirectoryEntries(
		string path,
		string relativePath,
		CancellationToken cancellationToken)
	{
		var entries = new List<FileSystemTreeEntry>(capacity: 32);
		foreach (var entry in FileSystemEntryEnumerator.EnumerateEntries(path, relativePath))
		{
			cancellationToken.ThrowIfCancellationRequested();
			entries.Add(entry);
		}

		entries.Sort(ProjectInventoryEntryComparer.Instance);
		return entries;
	}

	private static string? FindGitIgnorePath(IReadOnlyList<FileSystemTreeEntry> entries)
	{
		foreach (var entry in entries)
		{
			if (!entry.IsDirectory && PathComparer.Default.Equals(entry.Name, ".gitignore"))
				return entry.FullPath;
		}

		return null;
	}

	private static IReadOnlyList<ScopedGitIgnoreMatcher> MergeDiscoveredGitIgnoreMatchers(
		IReadOnlyList<ScopedGitIgnoreMatcher> matchers)
	{
		if (matchers.Count <= 1)
			return matchers;

		var unique = new Dictionary<string, ScopedGitIgnoreMatcher>(PathComparer.Default);
		foreach (var matcher in matchers)
			unique[matcher.ScopeRootPath] = matcher;

		var merged = unique.Values.ToList();
		merged.Sort(static (left, right) =>
		{
			var depth = left.ScopeRootPath.Length.CompareTo(right.ScopeRootPath.Length);
			return depth != 0
				? depth
				: PathComparer.Default.Compare(left.ScopeRootPath, right.ScopeRootPath);
		});
		return merged;
	}

	private static void MarkAccessDenied(List<ProjectTreeInventoryEntry> entries, int index)
	{
		var entry = entries[index];
		entry.IsAccessDenied = true;
		entries[index] = entry;
	}

	private static bool SafeHasHiddenAttribute(string path)
	{
		try
		{
			return File.GetAttributes(path).HasFlag(FileAttributes.Hidden);
		}
		catch
		{
			return false;
		}
	}

	private readonly record struct SubtreeScanResult(
		int RootGlobalIndex,
		List<ProjectTreeInventoryEntry> Entries,
		bool HadAccessDenied,
		IReadOnlyList<ScopedGitIgnoreMatcher> DiscoveredGitIgnoreMatchers);
}

internal readonly record struct ProjectTreeGitIgnoreContexts(
	bool Enabled,
	IgnoreRules.GitIgnoreScanContext Primary,
	IgnoreRules.GitIgnoreScanContext Secondary)
{
	public static ProjectTreeGitIgnoreContexts Disabled => default;

	public static ProjectTreeGitIgnoreContexts Create(
		IgnoreRules.GitIgnoreScanContext primary,
		IgnoreRules.GitIgnoreScanContext secondary) =>
		new(Enabled: true, primary, secondary);

	public ProjectTreeGitIgnoreContexts EnterDirectory(
		string directoryPath,
		string directoryRelativePath,
		string? gitIgnorePath,
		List<ScopedGitIgnoreMatcher> discoveredMatchers)
	{
		if (!Enabled || string.IsNullOrWhiteSpace(gitIgnorePath))
			return this;

		var primaryContainsScope = Primary.ContainsScope(directoryPath);
		var secondaryContainsScope = Secondary.ContainsScope(directoryPath);

		if (!GitIgnoreMatcherFileCache.TryLoad(directoryPath, gitIgnorePath, out var matcher))
			return this;

		discoveredMatchers.Add(matcher);
		return this with
		{
			Primary = primaryContainsScope
				? Primary
				: Primary.WithScope(matcher, directoryRelativePath),
			Secondary = secondaryContainsScope
				? Secondary
				: Secondary.WithScope(matcher, directoryRelativePath)
		};
	}
}
