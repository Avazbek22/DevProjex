namespace DevProjex.Tests.Unit;

public sealed class ProjectTreeInventoryMergeCapacityPerformanceTests(ITestOutputHelper output)
{
	[Fact]
	[Trait("Category", "LocalPerformance")]
	public void ExactCapacityMergePreservesSequenceAndAvoidsIntermediateGrowthArrays()
	{
		if (!string.Equals(
			    Environment.GetEnvironmentVariable("DEVPROJEX_RUN_LARGE_PERF_TESTS"),
			    "1",
			    StringComparison.Ordinal))
		{
			Assert.Skip("Set DEVPROJEX_RUN_LARGE_PERF_TESTS=1 for the pre-release performance gate.");
		}

		const int subtreeCount = 8;
		const int appendedEntriesPerSubtree = 12_500;
		var subtrees = CreateSubtrees(subtreeCount, appendedEntriesPerSubtree);

		_ = MergeWithLegacyGrowth(subtrees);
		_ = MergeWithExactCapacity(subtrees);

		var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
		var legacy = MergeWithLegacyGrowth(subtrees);
		var legacyAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

		allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
		var capacityAware = MergeWithExactCapacity(subtrees);
		var capacityAwareAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

		Assert.Equal(100_009, legacy.Count);
		Assert.Equal(legacy, capacityAware);
		Assert.Equal(ComputeSequenceHash(legacy), ComputeSequenceHash(capacityAware));
		Assert.True(
			capacityAwareAllocatedBytes * 5 < legacyAllocatedBytes * 3,
			$"Exact-capacity merge allocated {capacityAwareAllocatedBytes:N0} B; " +
			$"legacy growth allocated {legacyAllocatedBytes:N0} B.");

		output.WriteLine(
			$"Inventory merge allocations: legacy {legacyAllocatedBytes:N0} B, " +
			$"exact capacity {capacityAwareAllocatedBytes:N0} B.");
	}

	private static ProjectTreeInventoryEntry[][] CreateSubtrees(
		int subtreeCount,
		int appendedEntriesPerSubtree)
	{
		var subtrees = new ProjectTreeInventoryEntry[subtreeCount][];
		var sequence = 0;
		for (var subtreeIndex = 0; subtreeIndex < subtreeCount; subtreeIndex++)
		{
			var subtree = new ProjectTreeInventoryEntry[appendedEntriesPerSubtree + 1];
			for (var entryIndex = 0; entryIndex < subtree.Length; entryIndex++)
			{
				subtree[entryIndex] = CreateEntry(sequence++);
			}
			subtrees[subtreeIndex] = subtree;
		}

		return subtrees;
	}

	private static List<ProjectTreeInventoryEntry> MergeWithLegacyGrowth(
		IReadOnlyList<ProjectTreeInventoryEntry[]> subtrees)
	{
		var entries = CreateTargetRoots(subtrees.Count);
		AppendSubtrees(entries, subtrees);
		return entries;
	}

	private static List<ProjectTreeInventoryEntry> MergeWithExactCapacity(
		IReadOnlyList<ProjectTreeInventoryEntry[]> subtrees)
	{
		var entries = CreateTargetRoots(subtrees.Count);
		var mergedEntryCapacity = entries.Count;
		foreach (var subtree in subtrees)
			mergedEntryCapacity = checked(mergedEntryCapacity + Math.Max(0, subtree.Length - 1));
		entries.EnsureCapacity(mergedEntryCapacity);
		AppendSubtrees(entries, subtrees);
		return entries;
	}

	private static List<ProjectTreeInventoryEntry> CreateTargetRoots(int subtreeCount)
	{
		var entries = new List<ProjectTreeInventoryEntry>(capacity: 256)
		{
			CreateEntry(-1)
		};
		for (var index = 0; index < subtreeCount; index++)
			entries.Add(CreateEntry(-index - 2));
		return entries;
	}

	private static void AppendSubtrees(
		List<ProjectTreeInventoryEntry> target,
		IReadOnlyList<ProjectTreeInventoryEntry[]> subtrees)
	{
		foreach (var subtree in subtrees)
		{
			for (var index = 1; index < subtree.Length; index++)
				target.Add(subtree[index]);
		}
	}

	private static ProjectTreeInventoryEntry CreateEntry(int sequence) =>
		new(
			name: "entry",
			fullPath: "C:/repo/entry",
			relativePath: "entry",
			parentIndex: sequence,
			isDirectory: (sequence & 7) == 0,
			isHidden: (sequence & 15) == 0,
			length: sequence)
		{
			FirstChildIndex = sequence + 1,
			ChildCount = sequence & 3,
			IsAccessDenied = (sequence & 31) == 0
		};

	private static ulong ComputeSequenceHash(IEnumerable<ProjectTreeInventoryEntry> entries)
	{
		const ulong offsetBasis = 14695981039346656037;
		const ulong prime = 1099511628211;
		var hash = offsetBasis;
		foreach (var entry in entries)
		{
			hash = (hash ^ unchecked((uint)entry.ParentIndex)) * prime;
			hash = (hash ^ unchecked((uint)entry.FirstChildIndex)) * prime;
			hash = (hash ^ unchecked((uint)entry.ChildCount)) * prime;
			hash = (hash ^ unchecked((ulong)entry.Length)) * prime;
			hash = (hash ^ (entry.IsDirectory ? 1UL : 0UL)) * prime;
			hash = (hash ^ (entry.IsHidden ? 1UL : 0UL)) * prime;
			hash = (hash ^ (entry.IsAccessDenied ? 1UL : 0UL)) * prime;
		}

		return hash;
	}
}
