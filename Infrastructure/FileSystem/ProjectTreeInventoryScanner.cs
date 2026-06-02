namespace DevProjex.Infrastructure.FileSystem;

internal static class ProjectTreeInventoryScanner
{
	public static ProjectTreeInventorySnapshot Read(
		string rootPath,
		Func<FileSystemTreeEntry, bool, bool> shouldTraverseDirectory,
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

		var rootDirectoryChildren = AddProjectRootChildren(
			entries,
			rootChildren,
			shouldTraverseDirectory,
			cancellationToken);

		if (rootDirectoryChildren.Count == 0)
			return new ProjectTreeInventorySnapshot(entries, rootAccessDenied, hadAccessDenied);

		var subtreeResults = new SubtreeScanResult[rootDirectoryChildren.Count];
		var parallelOptions = ScanParallelismPolicy.CreateOptions(cancellationToken);
		Parallel.For(0, rootDirectoryChildren.Count, parallelOptions, index =>
		{
			var rootChildIndex = rootDirectoryChildren[index];
			subtreeResults[index] = ReadSubtree(
				rootChildIndex,
				entries[rootChildIndex],
				shouldTraverseDirectory,
				parallelOptions.CancellationToken);
		});

		foreach (var result in subtreeResults)
		{
			if (result.HadAccessDenied)
				hadAccessDenied = true;

			MergeSubtree(entries, result);
		}

		return new ProjectTreeInventorySnapshot(entries, rootAccessDenied, hadAccessDenied);
	}

	private static List<int> AddProjectRootChildren(
		List<ProjectTreeInventoryEntry> entries,
		IReadOnlyList<FileSystemTreeEntry> rootChildren,
		Func<FileSystemTreeEntry, bool, bool> shouldTraverseDirectory,
		CancellationToken cancellationToken)
	{
		var firstChildIndex = entries.Count;
		var directoryIndices = new List<int>();
		foreach (var child in rootChildren)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (child.IsDirectory && !shouldTraverseDirectory(child, true))
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
		Func<FileSystemTreeEntry, bool, bool> shouldTraverseDirectory,
		CancellationToken cancellationToken)
	{
		var entries = new List<ProjectTreeInventoryEntry>(capacity: 128)
		{
			rootEntry
		};
		var hadAccessDenied = false;
		var pendingDirectories = new Stack<int>();
		pendingDirectories.Push(0);

		while (pendingDirectories.Count > 0)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var parentIndex = pendingDirectories.Pop();
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

			var firstChildIndex = entries.Count;
			var childDirectoryIndices = new List<int>();
			foreach (var child in childEntries)
			{
				cancellationToken.ThrowIfCancellationRequested();
				if (child.IsDirectory && !shouldTraverseDirectory(child, false))
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
				pendingDirectories.Push(childDirectoryIndices[index]);
		}

		return new SubtreeScanResult(rootGlobalIndex, entries, hadAccessDenied);
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
		bool HadAccessDenied);
}
