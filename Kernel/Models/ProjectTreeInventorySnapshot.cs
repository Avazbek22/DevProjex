using System.Runtime.InteropServices;

namespace DevProjex.Kernel.Models;

/// <summary>
/// Immutable indexed snapshot of a project tree scan. Entries are stored as a flat
/// graph so projection layers can build different views without repeating filesystem IO.
/// </summary>
public sealed class ProjectTreeInventorySnapshot(
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
