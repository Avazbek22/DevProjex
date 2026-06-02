using System.Runtime.InteropServices;

namespace DevProjex.Infrastructure.FileSystem;

/// <summary>
/// Immutable snapshot of a tree scan. Entries are stored as an indexed graph
/// instead of nested objects so large projects do not allocate a temporary node
/// hierarchy before the final FileSystemNode tree is projected.
/// </summary>
internal sealed class ProjectTreeInventorySnapshot(
	List<ProjectTreeInventoryEntry> entries,
	bool rootAccessDenied,
	bool hadAccessDenied)
{
	public IReadOnlyList<ProjectTreeInventoryEntry> Entries => entries;
	public bool RootAccessDenied { get; } = rootAccessDenied;
	public bool HadAccessDenied { get; } = hadAccessDenied;

	public ProjectTreeInventoryEntry GetEntry(int index) => entries[index];

	public ReadOnlySpan<ProjectTreeInventoryEntry> GetChildren(int parentIndex)
	{
		var parent = entries[parentIndex];
		if (parent.ChildCount == 0)
			return [];

		return CollectionsMarshal.AsSpan(entries).Slice(parent.FirstChildIndex, parent.ChildCount);
	}
}
