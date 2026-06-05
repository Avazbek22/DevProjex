namespace DevProjex.Infrastructure.FileSystem;

public sealed class TreeBuilder : ITreeBuilder, IProjectTreeInventoryBuilder
{
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
		var gitIgnoreContext = options.IgnoreRules.CreateGitIgnoreScanContext(rootPath);
		return ProjectTreeInventoryScanner.Read(
			rootPath,
			(entry, isProjectRootChild) => ShouldTraverseDirectoryInInventory(
				entry,
				isProjectRootChild,
				options,
				gitIgnoreContext),
			cancellationToken);
	}

	public TreeBuildResult Build(
		ProjectTreeInventorySnapshot inventory,
		TreeFilterOptions options,
		CancellationToken cancellationToken = default)
	{
		var allowedExtensions = new AllowedExtensionLookup(options.AllowedExtensions);
		var rootEntry = inventory.GetEntry(0);
		var gitIgnoreContext = options.IgnoreRules.CreateGitIgnoreScanContext(rootEntry.FullPath);
		var hasNameFilter = !string.IsNullOrWhiteSpace(options.NameFilter);
		var root = new FileSystemNode(
			name: rootEntry.Name,
			fullPath: rootEntry.FullPath,
			isDirectory: true,
			isAccessDenied: rootEntry.IsAccessDenied,
			children: new List<FileSystemNode>());

		ProjectChildren(
			inventory,
			parentNode: root,
			parentIndex: 0,
			options,
			allowedExtensions,
			gitIgnoreContext,
			hasNameFilter,
			cancellationToken);

		return new TreeBuildResult(root, inventory.RootAccessDenied, inventory.HadAccessDenied);
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
		var directoryGitIgnore = ignore.UseGitIgnore
			? gitIgnoreContext.Evaluate(entry.FullPath, entry.RelativePath, isDirectory: true, entry.Name)
			: IgnoreRules.GitIgnoreEvaluation.NotIgnored;

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
		var parentEntry = inventory.GetEntry(parentIndex);
		if (parentEntry.IsAccessDenied)
			return;

		var childEntries = inventory.GetChildren(parentIndex);
		if (childEntries.Length == 0)
			return;

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
		CancellationToken cancellationToken)
	{
		var nodes = new FileSystemNode?[childEntries.Length];
		var firstChildIndex = inventory.GetEntry(0).FirstChildIndex;
		var parallelOptions = ScanParallelismPolicy.CreateOptions(cancellationToken);

		Parallel.For(0, childEntries.Length, parallelOptions, offset =>
		{
			nodes[offset] = ProjectNode(
				inventory,
				firstChildIndex + offset,
				options,
				allowedExtensions,
				gitIgnoreContext,
				hasNameFilter,
				parallelOptions.CancellationToken);
		});

		for (var i = 0; i < nodes.Length; i++)
		{
			var node = nodes[i];
			if (node is not null)
				children.Add(node);
		}
	}

	private static FileSystemNode? ProjectNode(
		ProjectTreeInventorySnapshot inventory,
		int entryIndex,
		TreeFilterOptions options,
		AllowedExtensionLookup allowedExtensions,
		IgnoreRules.GitIgnoreScanContext gitIgnoreContext,
		bool hasNameFilter,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();

		var entry = inventory.GetEntry(entryIndex);
		if (entry.IsDirectory)
			return ProjectDirectory(
				inventory,
				entryIndex,
				entry,
				options,
				allowedExtensions,
				gitIgnoreContext,
				hasNameFilter,
				cancellationToken);

		return ProjectFile(
			inventory,
			entry,
			options,
			allowedExtensions,
			gitIgnoreContext,
			hasNameFilter);
	}

	private static FileSystemNode? ProjectDirectory(
		ProjectTreeInventorySnapshot inventory,
		int entryIndex,
		ProjectTreeInventoryEntry entry,
		TreeFilterOptions options,
		AllowedExtensionLookup allowedExtensions,
		IgnoreRules.GitIgnoreScanContext gitIgnoreContext,
		bool hasNameFilter,
		CancellationToken cancellationToken)
	{
		var dirNode = new FileSystemNode(
			name: entry.Name,
			fullPath: entry.FullPath,
			isDirectory: true,
			isAccessDenied: entry.IsAccessDenied,
			children: new List<FileSystemNode>());

		ProjectChildren(
			inventory,
			dirNode,
			entryIndex,
			options,
			allowedExtensions,
			gitIgnoreContext,
			hasNameFilter,
			cancellationToken);

		if (options.IgnoreRules.IgnoreEmptyFolders &&
		    dirNode.Children.Count == 0 &&
		    !dirNode.IsAccessDenied)
		{
			return null;
		}

		if (hasNameFilter)
		{
			var hasMatchingChildren = dirNode.Children.Count > 0;
			var matchesName = entry.Name.Contains(options.NameFilter!, StringComparison.OrdinalIgnoreCase);
			return hasMatchingChildren || matchesName ? dirNode : null;
		}

		var directoryGitIgnore = options.IgnoreRules.UseGitIgnore
			? gitIgnoreContext.Evaluate(entry.FullPath, entry.RelativePath, isDirectory: true, entry.Name)
			: IgnoreRules.GitIgnoreEvaluation.NotIgnored;
		if (directoryGitIgnore.IsIgnored &&
		    directoryGitIgnore.ShouldTraverseIgnoredDirectory &&
		    dirNode.Children.Count == 0 &&
		    !dirNode.IsAccessDenied)
		{
			return null;
		}

		return dirNode;
	}

	private static FileSystemNode? ProjectFile(
		ProjectTreeInventorySnapshot inventory,
		ProjectTreeInventoryEntry entry,
		TreeFilterOptions options,
		AllowedExtensionLookup allowedExtensions,
		IgnoreRules.GitIgnoreScanContext gitIgnoreContext,
		bool hasNameFilter)
	{
		var ignore = options.IgnoreRules;
		var fileGitIgnore = ignore.UseGitIgnore
			? gitIgnoreContext.Evaluate(entry.FullPath, entry.RelativePath, isDirectory: false, entry.Name)
			: IgnoreRules.GitIgnoreEvaluation.NotIgnored;
		var parentEntry = inventory.GetEntry(entry.ParentIndex);
		var shouldApplySmartIgnoreForFiles = ignore.ShouldApplySmartIgnore(parentEntry.FullPath, isDirectory: true);

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
		{
			return true;
		}

		return false;
	}

	private static bool ShouldSkipFile(
		ProjectTreeInventoryEntry entry,
		IgnoreRules rules,
		bool shouldApplySmartIgnore,
		in IgnoreRules.GitIgnoreEvaluation gitIgnoreEvaluation)
	{
		if (gitIgnoreEvaluation.IsIgnored)
			return true;

		if (rules.IsSmartIgnoredFile(entry.FullPath, entry.Name, shouldApplySmartIgnore))
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
		{
			return true;
		}

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
