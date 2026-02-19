namespace DevProjex.Infrastructure.FileSystem;

public sealed class TreeBuilder : ITreeBuilder
{
	// Pre-allocated comparer instance to avoid allocation per sort
	private static readonly FileSystemInfoComparer EntryComparer = new();

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

		FileSystemInfo[] entries;
		try
		{
			// Get entries and sort in-place - avoids LINQ allocations
			entries = new DirectoryInfo(path).GetFileSystemInfos();
			Array.Sort(entries, EntryComparer);
		}
		catch (UnauthorizedAccessException)
		{
			state.HadAccessDenied = true;
			if (isRoot) state.RootAccessDenied = true;
			parent.IsAccessDenied = true;
			return;
		}
		catch
		{
			return;
		}

		var children = (List<FileSystemNode>)parent.Children;
		var hasNameFilter = !string.IsNullOrWhiteSpace(options.NameFilter);

		foreach (var entry in entries)
		{
			cancellationToken.ThrowIfCancellationRequested();

			var name = entry.Name;
			bool isDir = IsDirectory(entry);

			if (isDir && isRoot && !options.AllowedRootFolders.Contains(name))
				continue;

			var ignore = options.IgnoreRules;

			if (isDir)
			{
				if (ShouldSkipDirectory(entry, ignore))
					continue;

				var dirNode = new FileSystemNode(
					name: name,
					fullPath: entry.FullName,
					isDirectory: true,
					isAccessDenied: false,
					children: new List<FileSystemNode>());

				BuildChildren(dirNode, entry.FullName, options, isRoot: false, state, cancellationToken);

				// Keep full directory context when extension/ignore filters remove all files.
				// Name filter remains strict to preserve intentional narrowing behavior.
				if (hasNameFilter)
				{
					bool hasMatchingChildren = dirNode.Children.Count > 0;
					bool matchesName = name.Contains(options.NameFilter!, StringComparison.OrdinalIgnoreCase);
					if (hasMatchingChildren || matchesName)
						children.Add(dirNode);
				}
				else
				{
					// Keep empty directories hidden when gitignore effectively ignores their content
					// (e.g. patterns like **/bin/* that may not ignore the directory entry itself).
					if (dirNode.Children.Count == 0 &&
					    !dirNode.IsAccessDenied &&
					    IsEffectivelyGitIgnoredDirectory(entry, ignore))
					{
						continue;
					}

					// Keep ignored directories out of UI when traversal found no visible descendants.
					if (IsTraversableGitIgnoredDirectory(entry, ignore) &&
					    dirNode.Children.Count == 0 &&
					    !dirNode.IsAccessDenied)
					{
						continue;
					}

					children.Add(dirNode);
				}
			}
			else
			{
				if (ShouldSkipFile(entry, ignore))
					continue;

				if (IsExtensionlessFileName(name))
				{
					// Extensionless files are intentionally controlled only by ignore options.
				}
				else
				{
					if (options.AllowedExtensions.Count == 0)
						continue;

					var ext = Path.GetExtension(name);
					if (!options.AllowedExtensions.Contains(ext))
						continue;
				}

				// Apply name filter for files
				if (hasNameFilter && !name.Contains(options.NameFilter!, StringComparison.OrdinalIgnoreCase))
					continue;

				children.Add(new FileSystemNode(
					name: name,
					fullPath: entry.FullName,
					isDirectory: false,
					isAccessDenied: false,
					children: FileSystemNode.EmptyChildren));
			}
		}
	}

	private static bool IsDirectory(FileSystemInfo entry)
	{
		try
		{
			return entry.Attributes.HasFlag(FileAttributes.Directory);
		}
		catch (IOException)
		{
			// Reserved Windows device names (nul, con, prn, etc.) throw IOException
			// Treat them as files (non-directories)
			return false;
		}
		catch (UnauthorizedAccessException)
		{
			return false;
		}
	}

	private static bool ShouldSkipDirectory(FileSystemInfo entry, IgnoreRules rules)
	{
		var matcher = rules.ResolveGitIgnoreMatcher(entry.FullName);
		if (!ReferenceEquals(matcher, GitIgnoreMatcher.Empty) &&
		    matcher.IsIgnored(entry.FullName, isDirectory: true, entry.Name))
		{
			if (!matcher.ShouldTraverseIgnoredDirectory(entry.FullName, entry.Name))
				return true;
		}

		if (rules.ShouldApplySmartIgnore(entry.FullName) && rules.SmartIgnoredFolders.Contains(entry.Name))
			return true;

		if (rules.IgnoreDotFolders && entry.Name.StartsWith(".", StringComparison.Ordinal))
			return true;

		if (rules.IgnoreHiddenFolders)
		{
			try
			{
				if (entry.Attributes.HasFlag(FileAttributes.Hidden))
					return true;
			}
			catch (IOException)
			{
				// Reserved Windows device names (nul, con, prn, etc.) throw IOException
				return true;
			}
			catch (UnauthorizedAccessException)
			{
				return true;
			}
		}

		return false;
	}

	private static bool IsTraversableGitIgnoredDirectory(FileSystemInfo entry, IgnoreRules rules)
	{
		var matcher = rules.ResolveGitIgnoreMatcher(entry.FullName);
		return !ReferenceEquals(matcher, GitIgnoreMatcher.Empty) &&
		       matcher.IsIgnored(entry.FullName, isDirectory: true, entry.Name) &&
		       matcher.ShouldTraverseIgnoredDirectory(entry.FullName, entry.Name);
	}

	private static bool IsEffectivelyGitIgnoredDirectory(FileSystemInfo entry, IgnoreRules rules)
	{
		var matcher = rules.ResolveGitIgnoreMatcher(entry.FullName);
		if (ReferenceEquals(matcher, GitIgnoreMatcher.Empty))
			return false;

		if (matcher.IsIgnored(entry.FullName, isDirectory: true, entry.Name))
			return true;

		const string probeName = "__devprojex_ignore_probe__";
		var probePath = Path.Combine(entry.FullName, probeName);
		return matcher.IsIgnored(probePath, isDirectory: false, probeName);
	}

	private static bool ShouldSkipFile(FileSystemInfo entry, IgnoreRules rules)
	{
		var matcher = rules.ResolveGitIgnoreMatcher(entry.FullName);
		if (!ReferenceEquals(matcher, GitIgnoreMatcher.Empty) &&
		    matcher.IsIgnored(entry.FullName, isDirectory: false, entry.Name))
			return true;

		if (rules.ShouldApplySmartIgnore(entry.FullName) && rules.SmartIgnoredFiles.Contains(entry.Name))
			return true;

		if (rules.IgnoreDotFiles && entry.Name.StartsWith(".", StringComparison.Ordinal))
			return true;

		if (rules.IgnoreExtensionlessFiles && IsExtensionlessFileName(entry.Name))
			return true;

		if (rules.IgnoreHiddenFiles)
		{
			try
			{
				if (entry.Attributes.HasFlag(FileAttributes.Hidden))
					return true;
			}
			catch (IOException)
			{
				// Reserved Windows device names (nul, con, prn, etc.) throw IOException
				return true;
			}
			catch (UnauthorizedAccessException)
			{
				return true;
			}
		}

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
		public bool RootAccessDenied { get; set; }
		public bool HadAccessDenied { get; set; }
	}

	/// <summary>
	/// High-performance comparer for FileSystemInfo entries.
	/// Sorts directories before files, then by name (case-insensitive).
	/// Uses inlined checks to avoid virtual method overhead.
	/// </summary>
	private sealed class FileSystemInfoComparer : IComparer<FileSystemInfo>
	{
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public int Compare(FileSystemInfo? x, FileSystemInfo? y)
		{
			if (ReferenceEquals(x, y)) return 0;
			if (x is null) return -1;
			if (y is null) return 1;

			// Directories first (inlined attribute check)
			bool xIsDir = IsDirectoryFast(x);
			bool yIsDir = IsDirectoryFast(y);

			if (xIsDir != yIsDir)
				return xIsDir ? -1 : 1;

			// Then by name, case-insensitive ordinal (fastest string comparison)
			return string.Compare(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private static bool IsDirectoryFast(FileSystemInfo entry)
		{
			try
			{
				return (entry.Attributes & FileAttributes.Directory) != 0;
			}
			catch
			{
				return false;
			}
		}
	}
}
