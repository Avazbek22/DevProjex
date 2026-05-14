namespace DevProjex.Infrastructure.FileSystem;

/// <summary>
/// Directory-level inventory snapshot used by tree construction. It keeps the
/// filesystem read step isolated from filtering/projection without materializing
/// the whole project in memory before the UI tree is built.
/// </summary>
internal sealed record ProjectInventorySnapshot(
	IReadOnlyList<FileSystemTreeEntry> Entries,
	bool RootAccessDenied,
	bool HadAccessDenied)
{
	public static ProjectInventorySnapshot ReadDirectory(
		string path,
		string relativePath,
		bool isRoot,
		CancellationToken cancellationToken)
	{
		var entries = new List<FileSystemTreeEntry>(capacity: 32);
		try
		{
			foreach (var entry in FileSystemEntryEnumerator.EnumerateEntries(path, relativePath))
			{
				cancellationToken.ThrowIfCancellationRequested();
				entries.Add(entry);
			}

			entries.Sort(ProjectInventoryEntryComparer.Instance);
			return new ProjectInventorySnapshot(entries, RootAccessDenied: false, HadAccessDenied: false);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (UnauthorizedAccessException)
		{
			return new ProjectInventorySnapshot(
				entries,
				RootAccessDenied: isRoot,
				HadAccessDenied: true);
		}
		catch
		{
			return new ProjectInventorySnapshot(entries, RootAccessDenied: false, HadAccessDenied: false);
		}
	}
}

/// <summary>
/// Shared inventory order: directories first, then ordinal-ignore-case name sort.
/// This preserves the long-standing tree contract while keeping ordering close to
/// the filesystem snapshot source instead of duplicating sort logic in builders.
/// </summary>
internal sealed class ProjectInventoryEntryComparer : IComparer<FileSystemTreeEntry>
{
	public static readonly ProjectInventoryEntryComparer Instance = new();

	private ProjectInventoryEntryComparer()
	{
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public int Compare(FileSystemTreeEntry x, FileSystemTreeEntry y)
	{
		if (x.IsDirectory != y.IsDirectory)
			return x.IsDirectory ? -1 : 1;

		return string.Compare(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);
	}
}
