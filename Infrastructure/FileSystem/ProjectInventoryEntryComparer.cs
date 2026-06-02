namespace DevProjex.Infrastructure.FileSystem;

/// <summary>
/// Shared inventory order: directories first, then ordinal-ignore-case name sort.
/// This preserves the long-standing tree contract while keeping order fixed at
/// the inventory boundary instead of duplicating sort logic in each projector.
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
