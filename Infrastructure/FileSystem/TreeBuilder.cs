namespace DevProjex.Infrastructure.FileSystem;

public sealed class TreeBuilder : ITreeBuilder, IProjectTreeInventoryBuilder, IProjectTreeCompositeInventoryBuilder
{
	private const int RootProjectionParallelThreshold = 24;

	public TreeBuildResult Build(string rootPath, TreeFilterOptions options, CancellationToken cancellationToken = default)
	{
		var inventory = ReadInventory(rootPath, options, cancellationToken);
		return Build(inventory, options, cancellationToken);
	}

	public ProjectTreeInventorySnapshot ReadInventory(
		string rootPath,
		TreeFilterOptions options,
		CancellationToken cancellationToken = default)
	{
		options = WithExactRootFolderIdentity(options);
		var gitIgnoreContext = options.IgnoreRules.CreateGitIgnoreScanContext(rootPath);
		return ProjectTreeInventoryScanner.Read(
			rootPath,
			ProjectTreeGitIgnoreContexts.Create(
				gitIgnoreContext,
				gitIgnoreContext,
				RequiresWorkingTreeGitIgnore(options.IgnoreRules),
				options.IgnoreRules.ScopedGitIgnoreMatchers),
			(entry, isProjectRootChild, contexts) => ShouldTraverseDirectoryInInventory(
				entry,
				isProjectRootChild,
				options,
				contexts.Primary),
			cancellationToken);
	}

	public ProjectTreeInventorySnapshot ReadCompositeInventory(
		string rootPath,
		IReadOnlySet<string> allowedRootFolders,
		IgnoreRules discoveryRules,
		IgnoreRules projectionRules,
		CancellationToken cancellationToken = default)
	{
		allowedRootFolders = ToExactRootFolderSet(allowedRootFolders);
		var discoveryGitIgnoreContext = discoveryRules.CreateGitIgnoreScanContext(rootPath);
		var projectionGitIgnoreContext = projectionRules.CreateGitIgnoreScanContext(rootPath);
		return ProjectTreeInventoryScanner.Read(
			rootPath,
			ProjectTreeGitIgnoreContexts.Create(
				discoveryGitIgnoreContext,
				projectionGitIgnoreContext,
				RequiresWorkingTreeGitIgnore(discoveryRules) ||
				RequiresWorkingTreeGitIgnore(projectionRules),
				MergeGitIgnoreMatcherSeeds(discoveryRules, projectionRules)),
			(entry, isProjectRootChild, contexts) =>
			{
				if (isProjectRootChild && !allowedRootFolders.Contains(entry.Name))
					return false;

				var discoveryGitIgnore = contexts.Primary.Evaluate(
					entry.FullPath,
					entry.RelativePath,
					isDirectory: true,
					entry.Name);
				if (!ShouldSkipDirectory(entry, discoveryRules, discoveryGitIgnore))
					return true;

				var projectionGitIgnore = contexts.Secondary.Evaluate(
					entry.FullPath,
					entry.RelativePath,
					isDirectory: true,
					entry.Name);
				return !ShouldSkipDirectory(entry, projectionRules, projectionGitIgnore);
			},
			cancellationToken);
	}

	private static bool RequiresWorkingTreeGitIgnore(IgnoreRules rules) =>
		rules.EnableGitIgnoreTraversal && !rules.UseTrackedGitFilesOnly;

	private static TreeFilterOptions WithExactRootFolderIdentity(TreeFilterOptions options)
	{
		ArgumentNullException.ThrowIfNull(options);
		var exactRoots = ToExactRootFolderSet(options.AllowedRootFolders);
		return ReferenceEquals(exactRoots, options.AllowedRootFolders)
			? options
			: options with { AllowedRootFolders = exactRoots };
	}

	private static IReadOnlySet<string> ToExactRootFolderSet(IReadOnlySet<string> rootFolders)
	{
		ArgumentNullException.ThrowIfNull(rootFolders);
		if (rootFolders is HashSet<string> set &&
		    ReferenceEquals(set.Comparer, ProjectTreePathIdentity.CanonicalComparer))
		{
			return rootFolders;
		}

		return rootFolders.ToHashSet(ProjectTreePathIdentity.CanonicalComparer);
	}

	private static IReadOnlyList<ScopedGitIgnoreMatcher> MergeGitIgnoreMatcherSeeds(
		IgnoreRules discoveryRules,
		IgnoreRules projectionRules)
	{
		if (ReferenceEquals(discoveryRules, projectionRules) ||
		    ReferenceEquals(
			    discoveryRules.ScopedGitIgnoreMatchers,
			    projectionRules.ScopedGitIgnoreMatchers) ||
		    projectionRules.ScopedGitIgnoreMatchers.Count == 0)
		{
			return discoveryRules.ScopedGitIgnoreMatchers;
		}
		if (discoveryRules.ScopedGitIgnoreMatchers.Count == 0)
			return projectionRules.ScopedGitIgnoreMatchers;

		var merged = new List<ScopedGitIgnoreMatcher>(
			discoveryRules.ScopedGitIgnoreMatchers.Count + projectionRules.ScopedGitIgnoreMatchers.Count);
		var seenScopes = new HashSet<string>(ProjectTreePathIdentity.CanonicalComparer);
		foreach (var matcher in discoveryRules.ScopedGitIgnoreMatchers)
		{
			if (seenScopes.Add(matcher.ScopeRootPath))
				merged.Add(matcher);
		}
		foreach (var matcher in projectionRules.ScopedGitIgnoreMatchers)
		{
			if (seenScopes.Add(matcher.ScopeRootPath))
				merged.Add(matcher);
		}

		return merged;
	}

	public TreeBuildResult Build(
		ProjectTreeInventorySnapshot inventory,
		TreeFilterOptions options,
		CancellationToken cancellationToken = default)
	{
		options = WithExactRootFolderIdentity(options);
		var allowedExtensions = new AllowedExtensionLookup(options.AllowedExtensions);
		ref readonly var rootEntry = ref inventory.GetEntryRef(0);
		var gitIgnoreContext = options.IgnoreRules.CreateGitIgnoreScanContext(
			rootEntry.FullPath,
			inventory.DiscoveredGitIgnoreMatchers,
			inventory.DiscoveredGitTrackedPathIndexes);
		var hasNameFilter = !string.IsNullOrWhiteSpace(options.NameFilter);
		var root = new FileSystemNode(
			name: rootEntry.Name,
			fullPath: rootEntry.FullPath,
			isDirectory: true,
			isAccessDenied: rootEntry.IsAccessDenied,
			children: new List<FileSystemNode>(rootEntry.ChildCount));

		ProjectChildren(
			inventory,
			parentNode: root,
			parentIndex: 0,
			options,
			allowedExtensions,
			gitIgnoreContext,
			hasNameFilter,
			cancellationToken);

		return new TreeBuildResult(
			root,
			inventory.RootAccessDenied,
			inventory.HadAccessDenied,
			inventory.HadScanFailure);
	}

	private static bool ShouldTraverseDirectoryInInventory(
		FileSystemTreeEntry entry,
		bool isProjectRootChild,
		TreeFilterOptions options,
		IgnoreRules.GitIgnoreScanContext gitIgnoreContext)
	{
		if (isProjectRootChild && !options.AllowedRootFolders.Contains(entry.Name))
			return false;

		var ignore = options.IgnoreRules;
		var directoryGitIgnore = gitIgnoreContext.Evaluate(entry.FullPath, entry.RelativePath, isDirectory: true, entry.Name);

		return !ShouldSkipDirectory(entry, ignore, directoryGitIgnore);
	}

	private static void ProjectChildren(
		ProjectTreeInventorySnapshot inventory,
		FileSystemNode parentNode,
		int parentIndex,
		TreeFilterOptions options,
		AllowedExtensionLookup allowedExtensions,
		IgnoreRules.GitIgnoreScanContext gitIgnoreContext,
		bool hasNameFilter,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();

		var children = (List<FileSystemNode>)parentNode.Children;
		ref readonly var parentEntry = ref inventory.GetEntryRef(parentIndex);
		if (parentEntry.IsAccessDenied)
			return;

		var childEntries = inventory.GetChildren(parentIndex);
		if (childEntries.Length == 0)
			return;
		var shouldApplySmartIgnoreForFiles = options.IgnoreRules.ShouldApplySmartIgnore(
			parentEntry.FullPath,
			isDirectory: true);

		if (parentIndex == 0)
		{
			ProjectRootChildrenInParallel(
				inventory,
				childEntries,
				children,
				options,
				allowedExtensions,
				gitIgnoreContext,
				hasNameFilter,
				shouldApplySmartIgnoreForFiles,
				cancellationToken);
			return;
		}

		for (var offset = 0; offset < childEntries.Length; offset++)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var childIndex = parentEntry.FirstChildIndex + offset;
			var node = ProjectNode(
				inventory,
				childIndex,
				options,
				allowedExtensions,
				gitIgnoreContext,
				hasNameFilter,
				shouldApplySmartIgnoreForFiles,
				cancellationToken);
			if (node is not null)
				children.Add(node);
		}
	}

	private static void ProjectRootChildrenInParallel(
		ProjectTreeInventorySnapshot inventory,
		ReadOnlySpan<ProjectTreeInventoryEntry> childEntries,
		List<FileSystemNode> children,
		TreeFilterOptions options,
		AllowedExtensionLookup allowedExtensions,
		IgnoreRules.GitIgnoreScanContext gitIgnoreContext,
		bool hasNameFilter,
		bool shouldApplySmartIgnoreForFiles,
		CancellationToken cancellationToken)
	{
		var firstChildIndex = inventory.GetEntry(0).FirstChildIndex;
		if (childEntries.Length < RootProjectionParallelThreshold)
		{
			for (var offset = 0; offset < childEntries.Length; offset++)
			{
				cancellationToken.ThrowIfCancellationRequested();
				var childIndex = firstChildIndex + offset;
				if (!IsAllowedRootChild(inventory, childIndex, options))
					continue;

				var node = ProjectNode(
					inventory,
					childIndex,
					options,
					allowedExtensions,
					gitIgnoreContext,
					hasNameFilter,
					shouldApplySmartIgnoreForFiles,
					cancellationToken);
				if (node is not null)
					children.Add(node);
			}

			return;
		}

		var nodes = new FileSystemNode?[childEntries.Length];
		var parallelOptions = ScanParallelismPolicy.CreateOptions(cancellationToken);

		Parallel.For(0, childEntries.Length, parallelOptions, offset =>
		{
			var childIndex = firstChildIndex + offset;
			if (!IsAllowedRootChild(inventory, childIndex, options))
				return;

			nodes[offset] = ProjectNode(
				inventory,
				childIndex,
				options,
				allowedExtensions,
				gitIgnoreContext,
				hasNameFilter,
				shouldApplySmartIgnoreForFiles,
				parallelOptions.CancellationToken);
		});

		for (var i = 0; i < nodes.Length; i++)
		{
			var node = nodes[i];
			if (node is not null)
				children.Add(node);
		}
	}

	private static bool IsAllowedRootChild(
		ProjectTreeInventorySnapshot inventory,
		int entryIndex,
		TreeFilterOptions options)
	{
		ref readonly var entry = ref inventory.GetEntryRef(entryIndex);
		return !entry.IsDirectory || options.AllowedRootFolders.Contains(entry.Name);
	}

	private static FileSystemNode? ProjectNode(
		ProjectTreeInventorySnapshot inventory,
		int entryIndex,
		TreeFilterOptions options,
		AllowedExtensionLookup allowedExtensions,
		IgnoreRules.GitIgnoreScanContext gitIgnoreContext,
		bool hasNameFilter,
		bool shouldApplySmartIgnoreForFiles,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();

		ref readonly var entry = ref inventory.GetEntryRef(entryIndex);
		if (entry.IsDirectory)
			return ProjectDirectory(
				inventory,
				entryIndex,
				options,
				allowedExtensions,
				gitIgnoreContext,
				hasNameFilter,
				cancellationToken);

		return ProjectFile(
			in entry,
			options,
			allowedExtensions,
			gitIgnoreContext,
			hasNameFilter,
			shouldApplySmartIgnoreForFiles);
	}

	private static FileSystemNode? ProjectDirectory(
		ProjectTreeInventorySnapshot inventory,
		int entryIndex,
		TreeFilterOptions options,
		AllowedExtensionLookup allowedExtensions,
		IgnoreRules.GitIgnoreScanContext gitIgnoreContext,
		bool hasNameFilter,
		CancellationToken cancellationToken)
	{
		var rootFrame = CreateDirectoryProjectionFrame(
			inventory,
			entryIndex,
			options,
			gitIgnoreContext);
		if (rootFrame is null)
			return null;

		var pending = new List<DirectoryProjectionFrame> { rootFrame.Value };
		while (pending.Count > 0)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var frameIndex = pending.Count - 1;
			var frame = pending[frameIndex];
			if (!frame.Entry.IsAccessDenied && frame.NextChildOffset < frame.Entry.ChildCount)
			{
				var childIndex = frame.Entry.FirstChildIndex + frame.NextChildOffset++;
				pending[frameIndex] = frame;
				ref readonly var childEntry = ref inventory.GetEntryRef(childIndex);
				if (childEntry.IsDirectory)
				{
					var childFrame = CreateDirectoryProjectionFrame(
						inventory,
						childIndex,
						options,
						gitIgnoreContext);
					if (childFrame is not null)
						pending.Add(childFrame.Value);
				}
				else
				{
					var child = ProjectFile(
						in childEntry,
						options,
						allowedExtensions,
						gitIgnoreContext,
						hasNameFilter,
						frame.ShouldApplySmartIgnoreForFiles);
					if (child is not null)
					{
						frame.AddChild(child);
						pending[frameIndex] = frame;
					}
				}

				continue;
			}

			pending.RemoveAt(frameIndex);
			var projected = FinalizeDirectoryProjection(frame, options, hasNameFilter);
			if (pending.Count == 0)
				return projected;
			if (projected is not null)
			{
				var parentIndex = pending.Count - 1;
				var parent = pending[parentIndex];
				parent.AddChild(projected);
				pending[parentIndex] = parent;
			}
		}

		return null;
	}

	private static DirectoryProjectionFrame? CreateDirectoryProjectionFrame(
		ProjectTreeInventorySnapshot inventory,
		int entryIndex,
		TreeFilterOptions options,
		IgnoreRules.GitIgnoreScanContext gitIgnoreContext)
	{
		var entry = inventory.GetEntry(entryIndex);
		var directoryGitIgnore = gitIgnoreContext.Evaluate(entry.FullPath, entry.RelativePath, isDirectory: true, entry.Name);
		if (ShouldSkipDirectory(in entry, options.IgnoreRules, directoryGitIgnore))
			return null;

		return new DirectoryProjectionFrame(
			entry,
			directoryGitIgnore,
			options.IgnoreRules.ShouldApplySmartIgnore(entry.FullPath, isDirectory: true));
	}

	private static FileSystemNode? FinalizeDirectoryProjection(
		DirectoryProjectionFrame frame,
		TreeFilterOptions options,
		bool hasNameFilter)
	{
		var entry = frame.Entry;
		var directory = new FileSystemNode(
			entry.Name,
			entry.FullPath,
			isDirectory: true,
			entry.IsAccessDenied,
			frame.Children);

		if (options.IgnoreRules.IgnoreEmptyFolders &&
		    directory.Children.Count == 0 &&
		    !directory.IsAccessDenied)
		{
			return null;
		}

		if (hasNameFilter)
		{
			var matchesName = entry.Name.Contains(options.NameFilter!, StringComparison.OrdinalIgnoreCase);
			return directory.Children.Count > 0 || matchesName ? directory : null;
		}

		if (frame.DirectoryGitIgnore.IsIgnored &&
		    frame.DirectoryGitIgnore.ShouldTraverseIgnoredDirectory &&
		    directory.Children.Count == 0 &&
		    !directory.IsAccessDenied)
		{
			return null;
		}

		return directory;
	}

	private static FileSystemNode? ProjectFile(
		in ProjectTreeInventoryEntry entry,
		TreeFilterOptions options,
		AllowedExtensionLookup allowedExtensions,
		IgnoreRules.GitIgnoreScanContext gitIgnoreContext,
		bool hasNameFilter,
		bool shouldApplySmartIgnoreForFiles)
	{
		var ignore = options.IgnoreRules;
		var fileGitIgnore = gitIgnoreContext.Evaluate(entry.FullPath, entry.RelativePath, isDirectory: false, entry.Name);
		if (ShouldSkipFile(entry, ignore, shouldApplySmartIgnoreForFiles, fileGitIgnore))
			return null;

		if (IsExtensionlessFileName(entry.Name))
		{
			// Extensionless files are controlled only by ignore options.
		}
		else
		{
			if (allowedExtensions.IsEmpty)
				return null;

			if (!allowedExtensions.AllowsFileName(entry.Name))
				return null;
		}

		if (hasNameFilter && !entry.Name.Contains(options.NameFilter!, StringComparison.OrdinalIgnoreCase))
			return null;

		return new FileSystemNode(
			name: entry.Name,
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
		return ShouldSkipDirectoryCore(entry.FullPath, entry.Name, entry.IsHidden, rules, gitIgnoreEvaluation);
	}

	private static bool ShouldSkipDirectory(
		in ProjectTreeInventoryEntry entry,
		IgnoreRules rules,
		in IgnoreRules.GitIgnoreEvaluation gitIgnoreEvaluation)
	{
		return ShouldSkipDirectoryCore(entry.FullPath, entry.Name, entry.IsHidden, rules, gitIgnoreEvaluation);
	}

	private static bool ShouldSkipDirectoryCore(
		string fullPath,
		string name,
		bool isHidden,
		IgnoreRules rules,
		in IgnoreRules.GitIgnoreEvaluation gitIgnoreEvaluation)
	{
		return IgnoreDecisionEngine
			.EvaluateDirectory(fullPath, name, isHidden, rules, gitIgnoreEvaluation)
			.IsIgnored;
	}

	private static bool ShouldSkipFile(
		in ProjectTreeInventoryEntry entry,
		IgnoreRules rules,
		bool shouldApplySmartIgnore,
		in IgnoreRules.GitIgnoreEvaluation gitIgnoreEvaluation)
	{
		return IgnoreDecisionEngine
			.EvaluateFile(
				entry.FullPath,
				entry.Name,
				entry.IsHidden,
				entry.Length,
				rules,
				shouldApplySmartIgnore,
				gitIgnoreEvaluation)
			.IsIgnored;
	}

	private static bool IsExtensionlessFileName(string fileName) =>
		IgnoreRuleSemantics.IsExtensionlessFileName(fileName);

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

	private struct DirectoryProjectionFrame
	{
		private List<FileSystemNode>? _children;
		private readonly int _childCapacity;

		public DirectoryProjectionFrame(
			ProjectTreeInventoryEntry entry,
			IgnoreRules.GitIgnoreEvaluation directoryGitIgnore,
			bool shouldApplySmartIgnoreForFiles)
		{
			Entry = entry;
			DirectoryGitIgnore = directoryGitIgnore;
			ShouldApplySmartIgnoreForFiles = shouldApplySmartIgnoreForFiles;
			_childCapacity = entry.ChildCount;
		}

		public ProjectTreeInventoryEntry Entry { get; }
		public IgnoreRules.GitIgnoreEvaluation DirectoryGitIgnore { get; }
		public bool ShouldApplySmartIgnoreForFiles { get; }
		public IReadOnlyList<FileSystemNode> Children => _children ?? FileSystemNode.EmptyChildren;
		public int NextChildOffset { get; set; }

		public void AddChild(FileSystemNode child) =>
			(_children ??= new List<FileSystemNode>(_childCapacity)).Add(child);
	}
}
