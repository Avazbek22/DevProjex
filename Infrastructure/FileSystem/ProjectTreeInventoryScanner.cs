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
		CancellationToken cancellationToken,
		Action<FileSystemScanEnumerationPoint, string>? beforeEnumeration = null)
	{
		cancellationToken.ThrowIfCancellationRequested();
		var gitIgnoreLoadSession = new GitIgnoreMatcherLoadSession();
		if (initialGitIgnoreContexts.SeedMatchers is { Count: > 0 } seedMatchers)
			gitIgnoreLoadSession.Seed(seedMatchers);

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

		if (PathUtility.IsMissingPath(rootPath) || !Directory.Exists(rootPath))
			return new ProjectTreeInventorySnapshot(entries, rootAccessDenied: false, hadAccessDenied: false);
		if (!FileSystemRootEntryPolicy.IsPhysicalDirectory(rootPath))
		{
			MarkAccessDenied(entries, 0);
			return new ProjectTreeInventorySnapshot(entries, rootAccessDenied: true, hadAccessDenied: true);
		}

		var rootAccessDenied = false;
		var hadAccessDenied = false;
		var hadScanFailure = false;
		var discoveredGitRepositoryRoots = new List<string>();
		if (GitTrackedPathIndexCache.TryFindNearestRepositoryBoundary(
			rootPath,
			cancellationToken,
			out var nearestRepositoryRoot))
		{
			discoveredGitRepositoryRoots.Add(nearestRepositoryRoot);
		}
		List<FileSystemTreeEntry> rootChildren;
		try
		{
			beforeEnumeration?.Invoke(FileSystemScanEnumerationPoint.RootDirectories, rootPath);
			rootChildren = ReadDirectoryEntries(rootPath, relativePath: string.Empty, cancellationToken);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (UnauthorizedAccessException)
		{
			MarkAccessDenied(entries, 0);
			return new ProjectTreeInventorySnapshot(
				entries,
				rootAccessDenied: true,
				hadAccessDenied: true,
				discoveredGitRepositoryRoots: discoveredGitRepositoryRoots);
		}
		catch (Exception exception) when (FileSystemScanner.IsExpectedFileSystemScanFailure(exception))
		{
			return new ProjectTreeInventorySnapshot(
				entries,
				rootAccessDenied: false,
				hadAccessDenied: false,
				hadScanFailure: true,
				discoveredGitRepositoryRoots: discoveredGitRepositoryRoots);
		}

		var discoveredGitIgnoreMatchers = new ScopedGitIgnoreMatcherAccumulator();
		var discoveredGitTrackedPathIndexes = new List<GitTrackedPathIndex>();
		var inheritedGitIgnoreContexts = initialGitIgnoreContexts;
		if (initialGitIgnoreContexts.Enabled)
		{
			var ancestorScopes = GitIgnoreAncestorScopeBootstrapper.Apply(
				rootPath,
				inheritedGitIgnoreContexts.Primary,
				inheritedGitIgnoreContexts.Secondary,
				cancellationToken,
				discoveredGitIgnoreMatchers,
				gitIgnoreLoadSession);
			inheritedGitIgnoreContexts = inheritedGitIgnoreContexts with
			{
				Primary = ancestorScopes.Active,
				Secondary = ancestorScopes.Candidate
			};
			if (initialGitIgnoreContexts.ReportGitIgnoreReadFailures &&
			    ancestorScopes.LoadStatus == GitIgnoreMatcherLoadStatus.ReadFailure)
			{
				MarkAccessDenied(entries, 0);
				return new ProjectTreeInventorySnapshot(
					entries,
					rootAccessDenied: false,
					hadAccessDenied: true,
					discoveredGitIgnoreMatchers.Items,
					discoveredGitTrackedPathIndexes,
					discoveredGitRepositoryRoots: discoveredGitRepositoryRoots);
			}
		}
		if (inheritedGitIgnoreContexts.RequiresTrackedPathIndex &&
		    GitTrackedPathIndexCache.TryLoadNearest(
			    rootPath,
			    cancellationToken,
			    out var inheritedTrackedPathIndex))
		{
			discoveredGitTrackedPathIndexes.Add(inheritedTrackedPathIndex);
			inheritedGitIgnoreContexts = inheritedGitIgnoreContexts.WithTrackedPathIndex(
				inheritedTrackedPathIndex);
		}

		var rootGitControlPaths = FindGitControlPaths(rootChildren);
		var rootGitIgnoreContexts = inheritedGitIgnoreContexts.EnterDirectory(
			rootPath,
			directoryRelativePath: string.Empty,
			rootGitControlPaths.GitIgnorePath,
			rootGitControlPaths.GitMetadataPath,
			discoveredGitIgnoreMatchers,
			discoveredGitTrackedPathIndexes,
			gitIgnoreLoadSession,
			cancellationToken,
			out var rootGitIgnoreReadFailed);
		if (rootGitIgnoreReadFailed)
		{
			MarkAccessDenied(entries, 0);
			return new ProjectTreeInventorySnapshot(
				entries,
				rootAccessDenied: false,
				hadAccessDenied: true,
				discoveredGitIgnoreMatchers.Items,
				discoveredGitTrackedPathIndexes,
				discoveredGitRepositoryRoots: discoveredGitRepositoryRoots);
		}
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
				discoveredGitIgnoreMatchers.Items,
				discoveredGitTrackedPathIndexes,
				discoveredGitRepositoryRoots: discoveredGitRepositoryRoots);
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
					gitIgnoreLoadSession,
					cancellationToken,
					beforeEnumeration);
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
					gitIgnoreLoadSession,
					parallelOptions.CancellationToken,
					beforeEnumeration);
			});
		}

		var mergedEntryCapacity = entries.Count;
		foreach (var result in subtreeResults)
		{
			cancellationToken.ThrowIfCancellationRequested();
			mergedEntryCapacity = checked(mergedEntryCapacity + Math.Max(0, result.Entries.Count - 1));
		}
		entries.EnsureCapacity(mergedEntryCapacity);

		foreach (var result in subtreeResults)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (result.HadAccessDenied)
				hadAccessDenied = true;
			if (result.HadScanFailure)
				hadScanFailure = true;

			MergeSubtree(entries, result, cancellationToken);
			foreach (var matcher in result.DiscoveredGitIgnoreMatchers)
			{
				cancellationToken.ThrowIfCancellationRequested();
				discoveredGitIgnoreMatchers.Add(matcher);
			}
			AppendRange(discoveredGitTrackedPathIndexes, result.DiscoveredGitTrackedPathIndexes, cancellationToken);
			AppendRange(discoveredGitRepositoryRoots, result.DiscoveredGitRepositoryRoots, cancellationToken);
		}

		var uniqueMatchers = MergeDiscoveredGitIgnoreMatchers(discoveredGitIgnoreMatchers.Items, cancellationToken);
		var uniqueTrackedPathIndexes = MergeDiscoveredGitTrackedPathIndexes(
			discoveredGitTrackedPathIndexes,
			cancellationToken);
		var uniqueRepositoryRoots = MergeDiscoveredGitRepositoryRoots(
			discoveredGitRepositoryRoots,
			cancellationToken);
		return new ProjectTreeInventorySnapshot(
			entries,
			rootAccessDenied,
			hadAccessDenied,
			uniqueMatchers,
			uniqueTrackedPathIndexes,
			hadScanFailure,
			uniqueRepositoryRoots);
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
		GitIgnoreMatcherLoadSession gitIgnoreLoadSession,
		CancellationToken cancellationToken,
		Action<FileSystemScanEnumerationPoint, string>? beforeEnumeration)
	{
		var entries = new List<ProjectTreeInventoryEntry>(capacity: 128)
		{
			rootEntry
		};
		var hadAccessDenied = false;
		var hadScanFailure = false;
		var discoveredGitIgnoreMatchers = new ScopedGitIgnoreMatcherAccumulator();
		var discoveredGitTrackedPathIndexes = new List<GitTrackedPathIndex>();
		var discoveredGitRepositoryRoots = new List<string>();
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
				beforeEnumeration?.Invoke(FileSystemScanEnumerationPoint.DirectoryDiscovery, parent.FullPath);
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
			catch (Exception exception) when (FileSystemScanner.IsExpectedFileSystemScanFailure(exception))
			{
				hadScanFailure = true;
				continue;
			}

			if (childEntries.Count == 0)
				continue;

			var gitControlPaths = FindGitControlPaths(childEntries);
			var gitIgnoreContexts = parentGitIgnoreContexts.EnterDirectory(
				parent.FullPath,
				parent.RelativePath,
				gitControlPaths.GitIgnorePath,
				gitControlPaths.GitMetadataPath,
				discoveredGitIgnoreMatchers,
				discoveredGitTrackedPathIndexes,
				gitIgnoreLoadSession,
				cancellationToken,
				out var gitIgnoreReadFailed);
			if (gitIgnoreContexts.Primary.IsOpaqueRepository(parent.FullPath) && !gitIgnoreReadFailed)
				continue;
			if (!string.IsNullOrWhiteSpace(gitControlPaths.GitMetadataPath) &&
			    !gitIgnoreContexts.Secondary.IsOpaqueRepository(parent.FullPath))
				discoveredGitRepositoryRoots.Add(parent.FullPath);
			if (gitIgnoreReadFailed)
			{
				MarkAccessDenied(entries, parentIndex);
				hadAccessDenied = true;
				continue;
			}

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
			hadScanFailure,
			discoveredGitIgnoreMatchers.Items,
			discoveredGitTrackedPathIndexes,
			discoveredGitRepositoryRoots);
	}

	private static void MergeSubtree(
		List<ProjectTreeInventoryEntry> targetEntries,
		SubtreeScanResult subtree,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
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
			cancellationToken.ThrowIfCancellationRequested();
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

		CancellationAwareSort.Sort(entries, ProjectInventoryEntryComparer.Instance, cancellationToken);
		return entries;
	}

	private static GitControlPaths FindGitControlPaths(IReadOnlyList<FileSystemTreeEntry> entries)
	{
		string? gitIgnorePath = null;
		string? gitIgnoreAliasPath = null;
		string? gitMetadataPath = null;
		string? gitMetadataAliasPath = null;
		foreach (var entry in entries)
		{
			var parentPath = Path.GetDirectoryName(entry.FullPath)!;
			if (!entry.IsDirectory &&
			    ProjectTreePathIdentity.CanonicalComparer.Equals(entry.Name, ".gitignore"))
			{
				gitIgnorePath = entry.FullPath;
			}
			else if (!entry.IsDirectory &&
			         gitIgnorePath is null &&
			         gitIgnoreAliasPath is null &&
			         OperatingSystem.IsWindows() &&
			         entry.Name.Equals(".gitignore", StringComparison.OrdinalIgnoreCase) &&
			         FileSystemEntryEnumerator.IsWindowsCompatibleControlAlias(
				         entry.Name,
				         ".gitignore",
				         File.Exists(Path.Combine(parentPath, ".gitignore"))))
			{
				gitIgnoreAliasPath = entry.FullPath;
			}

			if (ProjectTreePathIdentity.CanonicalComparer.Equals(entry.Name, ".git") &&
			    GitRepositoryBoundaryProbe.ExistsAt(parentPath))
			{
				gitMetadataPath = Path.Combine(parentPath, ".git");
			}
			else if (gitMetadataPath is null &&
			         gitMetadataAliasPath is null &&
			         OperatingSystem.IsWindows() &&
			         entry.Name.Equals(".git", StringComparison.OrdinalIgnoreCase) &&
			         FileSystemEntryEnumerator.IsWindowsCompatibleControlAlias(
				         entry.Name,
				         ".git",
				         GitRepositoryBoundaryProbe.ExistsAt(parentPath)))
			{
				gitMetadataAliasPath = Path.Combine(parentPath, ".git");
			}

			if (gitIgnorePath is not null && gitMetadataPath is not null)
				break;
		}

		return new GitControlPaths(
			gitIgnorePath ?? gitIgnoreAliasPath,
			gitMetadataPath ?? gitMetadataAliasPath);
	}

	private static IReadOnlyList<ScopedGitIgnoreMatcher> MergeDiscoveredGitIgnoreMatchers(
		IReadOnlyList<ScopedGitIgnoreMatcher> matchers,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if (matchers.Count <= 1)
			return matchers;

		var unique = new Dictionary<string, ScopedGitIgnoreMatcher>(
			ProjectTreePathIdentity.CanonicalComparer);
		foreach (var matcher in matchers)
		{
			cancellationToken.ThrowIfCancellationRequested();
			unique[matcher.ScopeRootPath] = matcher;
		}

		var merged = unique.Values.ToList();
		CancellationAwareSort.Sort(
			merged,
			static (left, right) =>
			{
				var depth = left.ScopeRootPath.Length.CompareTo(right.ScopeRootPath.Length);
				if (depth != 0)
					return depth;
				var platformOrder = PathComparer.Default.Compare(
					left.ScopeRootPath,
					right.ScopeRootPath);
				return platformOrder != 0
					? platformOrder
					: ProjectTreePathIdentity.CanonicalComparer.Compare(
						left.ScopeRootPath,
						right.ScopeRootPath);
			},
			cancellationToken);
		return merged;
	}

	private static IReadOnlyList<GitTrackedPathIndex> MergeDiscoveredGitTrackedPathIndexes(
		IReadOnlyList<GitTrackedPathIndex> indexes,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if (indexes.Count <= 1)
			return indexes;

		var unique = new Dictionary<string, GitTrackedPathIndex>(StringComparer.Ordinal);
		foreach (var index in indexes)
		{
			cancellationToken.ThrowIfCancellationRequested();
			unique[index.RepositoryRootPath] = index;
		}

		var merged = unique.Values.ToList();
		CancellationAwareSort.Sort(
			merged,
			static (left, right) =>
			{
				var depth = left.RepositoryRootPath.Length.CompareTo(right.RepositoryRootPath.Length);
				if (depth != 0)
					return depth;
				var platformOrder = PathComparer.Default.Compare(
					left.RepositoryRootPath,
					right.RepositoryRootPath);
				return platformOrder != 0
					? platformOrder
					: StringComparer.Ordinal.Compare(left.RepositoryRootPath, right.RepositoryRootPath);
			},
			cancellationToken);
		return merged;
	}

	private static IReadOnlyList<string> MergeDiscoveredGitRepositoryRoots(
		IReadOnlyList<string> repositoryRoots,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if (repositoryRoots.Count <= 1)
			return repositoryRoots;

		var unique = repositoryRoots.ToHashSet(StringComparer.Ordinal);
		var merged = unique.ToList();
		CancellationAwareSort.Sort(
			merged,
			static (left, right) =>
			{
				var depth = left.Length.CompareTo(right.Length);
				if (depth != 0)
					return depth;
				var platformOrder = PathComparer.Default.Compare(left, right);
				return platformOrder != 0
					? platformOrder
					: StringComparer.Ordinal.Compare(left, right);
			},
			cancellationToken);
		return merged;
	}

	private static void AppendRange<T>(
		List<T> target,
		IReadOnlyList<T> source,
		CancellationToken cancellationToken)
	{
		foreach (var item in source)
		{
			cancellationToken.ThrowIfCancellationRequested();
			target.Add(item);
		}
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
		bool HadScanFailure,
		IReadOnlyList<ScopedGitIgnoreMatcher> DiscoveredGitIgnoreMatchers,
		IReadOnlyList<GitTrackedPathIndex> DiscoveredGitTrackedPathIndexes,
		IReadOnlyList<string> DiscoveredGitRepositoryRoots);

	private readonly record struct GitControlPaths(
		string? GitIgnorePath,
		string? GitMetadataPath);
}

internal readonly record struct ProjectTreeGitIgnoreContexts(
	bool Enabled,
	bool ReportGitIgnoreReadFailures,
	IgnoreRules.GitIgnoreScanContext Primary,
	IgnoreRules.GitIgnoreScanContext Secondary,
	IReadOnlyList<ScopedGitIgnoreMatcher>? SeedMatchers)
{
	public static ProjectTreeGitIgnoreContexts Disabled => default;

	public static ProjectTreeGitIgnoreContexts Create(
		IgnoreRules.GitIgnoreScanContext primary,
		IgnoreRules.GitIgnoreScanContext secondary,
		bool reportGitIgnoreReadFailures,
		IReadOnlyList<ScopedGitIgnoreMatcher>? seedMatchers = null) =>
		new(Enabled: true, reportGitIgnoreReadFailures, primary, secondary, seedMatchers);

	public bool HasIgnoreRules =>
		Enabled && (Primary.HasIgnoreRules || Secondary.HasIgnoreRules);

	public bool RequiresTrackedPathIndex =>
		Enabled && (Primary.RequiresTrackedPathIndex || Secondary.RequiresTrackedPathIndex);

	public ProjectTreeGitIgnoreContexts WithTrackedPathIndex(GitTrackedPathIndex trackedPathIndex) =>
		this with
		{
			Primary = Primary.WithTrackedPathIndex(trackedPathIndex),
			Secondary = Secondary.WithTrackedPathIndex(trackedPathIndex)
		};

	public ProjectTreeGitIgnoreContexts EnterDirectory(
		string directoryPath,
		string directoryRelativePath,
		string? gitIgnorePath,
		string? gitMetadataPath,
		ScopedGitIgnoreMatcherAccumulator discoveredMatchers,
		List<GitTrackedPathIndex> discoveredTrackedPathIndexes,
		GitIgnoreMatcherLoadSession gitIgnoreLoadSession,
		CancellationToken cancellationToken,
		out bool gitIgnoreReadFailed)
	{
		gitIgnoreReadFailed = false;
		if (!Enabled || Secondary.IsOpaqueRepository(directoryPath))
			return this;

		var primaryContext = Primary;
		var secondaryContext = Secondary;
		var loadResult = gitIgnoreLoadSession.LoadScope(directoryPath, gitIgnorePath, gitMetadataPath,
			gitMetadataPath is null ? null : primaryContext.GetOwningRepository(directoryPath), cancellationToken);
		var matcher = loadResult.Matcher;
		gitIgnoreReadFailed = (ReportGitIgnoreReadFailures || matcher?.IsOpaqueRepository == true && Primary.GitFilteringEnabled) &&
		                     loadResult.Status == GitIgnoreMatcherLoadStatus.ReadFailure;

		var requiresTrackedPathIndex =
			primaryContext.RequiresTrackedPathIndex ||
			secondaryContext.RequiresTrackedPathIndex ||
			matcher is not null &&
			!ReferenceEquals(matcher.Matcher, GitIgnoreMatcher.Empty);
		var reachedRepositoryBoundary = !string.IsNullOrWhiteSpace(gitMetadataPath);
		if (matcher?.IsOpaqueRepository != true && requiresTrackedPathIndex && (reachedRepositoryBoundary || matcher is not null))
		{
			GitTrackedPathIndex? trackedPathIndex = null;
			var loadedTrackedPathIndex = reachedRepositoryBoundary
				? GitTrackedPathIndexCache.TryLoad(
					directoryPath,
					gitMetadataPath!,
					cancellationToken,
					out trackedPathIndex)
				: GitTrackedPathIndexCache.TryLoadNearest(
					directoryPath,
					cancellationToken,
					out trackedPathIndex);
			if (loadedTrackedPathIndex)
			{
				if (!primaryContext.ContainsTrackedPathIndex(trackedPathIndex.RepositoryRootPath) ||
				    !secondaryContext.ContainsTrackedPathIndex(trackedPathIndex.RepositoryRootPath))
				{
					discoveredTrackedPathIndexes.Add(trackedPathIndex);
				}

				primaryContext = primaryContext.WithTrackedPathIndex(trackedPathIndex);
				secondaryContext = secondaryContext.WithTrackedPathIndex(trackedPathIndex);
			}
			else if (reachedRepositoryBoundary)
			{
				// Do not let an ancestor repository index leak through a nested repository
				// whose own index cannot be read. The empty boundary is retained in the
				// inventory so direct and projected builds keep identical ownership.
				var unavailableBoundaryIndex = GitTrackedPathIndex.Unavailable(directoryPath);
				if (!primaryContext.ContainsTrackedPathIndex(directoryPath) ||
				    !secondaryContext.ContainsTrackedPathIndex(directoryPath))
				{
					discoveredTrackedPathIndexes.Add(unavailableBoundaryIndex);
				}
				primaryContext = primaryContext.WithTrackedPathIndex(unavailableBoundaryIndex);
				secondaryContext = secondaryContext.WithTrackedPathIndex(unavailableBoundaryIndex);
			}
		}

		if (matcher is null)
		{
			return this with
			{
				Primary = primaryContext,
				Secondary = secondaryContext
			};
		}

		discoveredMatchers.Add(matcher);
		return this with
		{
			Primary = primaryContext.WithScope(matcher, directoryRelativePath),
			Secondary = secondaryContext.WithScope(matcher, directoryRelativePath)
		};
	}
}
