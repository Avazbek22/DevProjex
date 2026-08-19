using System.Runtime.InteropServices;

namespace DevProjex.Kernel.Models;

/// <summary>
/// Immutable indexed snapshot of a project tree scan. Entries are stored as a flat
/// graph so projection layers can build different views without repeating filesystem IO.
/// </summary>
public sealed class ProjectTreeInventorySnapshot(
	List<ProjectTreeInventoryEntry> entries,
	bool rootAccessDenied,
	bool hadAccessDenied,
	IReadOnlyList<ScopedGitIgnoreMatcher>? discoveredGitIgnoreMatchers = null,
	IReadOnlyList<GitTrackedPathIndex>? discoveredGitTrackedPathIndexes = null,
	bool hadScanFailure = false)
{
	public IReadOnlyList<ProjectTreeInventoryEntry> Entries => entries;
	public bool RootAccessDenied { get; } = rootAccessDenied;
	public bool HadAccessDenied { get; } = hadAccessDenied;
	public bool HadScanFailure { get; } = hadScanFailure;

	// Inventory projections do not touch the filesystem again, so they must carry every
	// reachable per-directory rule that affected discovery and can affect later selections.
	public IReadOnlyList<ScopedGitIgnoreMatcher> DiscoveredGitIgnoreMatchers { get; } =
		discoveredGitIgnoreMatchers ?? [];

	public IReadOnlyList<GitTrackedPathIndex> DiscoveredGitTrackedPathIndexes { get; } =
		discoveredGitTrackedPathIndexes ?? [];

	public ProjectTreeInventoryEntry GetEntry(int index) => entries[index];

	public ref readonly ProjectTreeInventoryEntry GetEntryRef(int index)
	{
		// Entries are immutable after snapshot creation; returning by readonly ref avoids
		// copying the hot-path struct while keeping callers from mutating inventory state.
		return ref CollectionsMarshal.AsSpan(entries)[index];
	}

	public ReadOnlySpan<ProjectTreeInventoryEntry> GetChildren(int parentIndex)
	{
		ref readonly var parent = ref GetEntryRef(parentIndex);
		if (parent.ChildCount == 0)
			return [];

		return CollectionsMarshal.AsSpan(entries).Slice(parent.FirstChildIndex, parent.ChildCount);
	}
}
