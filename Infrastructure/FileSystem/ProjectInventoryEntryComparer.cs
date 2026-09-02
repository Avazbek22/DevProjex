namespace DevProjex.Infrastructure.FileSystem;

/// <summary>
/// Shared inventory order: directories first, then ordinal-ignore-case name sort
/// with an ordinal tie-breaker for case-distinct names on case-sensitive filesystems.
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

		return ProjectInventoryNameComparer.Compare(x.Name, y.Name);
	}
}

internal static class ProjectInventoryNameComparer
{
	public static int Compare(string left, string right)
	{
		var comparison = string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
		return comparison != 0
			? comparison
			: string.Compare(left, right, StringComparison.Ordinal);
	}
}
