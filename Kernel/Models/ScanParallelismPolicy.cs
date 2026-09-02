namespace DevProjex.Kernel.Models;

/// <summary>
/// Central scan fan-out policy for filesystem-heavy work.
/// The policy is intentionally based only on CPU capacity: scan callers should not
/// silently change behavior by project size, drive type, or current selection count.
/// </summary>
public static class ScanParallelismPolicy
{
	private const int MinimumDegreeOfParallelism = 4;

	public static int MaxDegreeOfParallelism { get; } = ResolveMaxDegreeOfParallelism();

	public static ParallelOptions CreateOptions(
		CancellationToken cancellationToken = default,
		int? maximumDegreeOfParallelism = null)
	{
		if (maximumDegreeOfParallelism is <= 0)
			throw new ArgumentOutOfRangeException(nameof(maximumDegreeOfParallelism));

		return new ParallelOptions
		{
			MaxDegreeOfParallelism = Math.Min(
				maximumDegreeOfParallelism ?? MaxDegreeOfParallelism,
				MaxDegreeOfParallelism),
			CancellationToken = cancellationToken
		};
	}

	public static int PartitionDegreeOfParallelism(int concurrentPartitions)
	{
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(concurrentPartitions);
		var activePartitions = Math.Min(concurrentPartitions, MaxDegreeOfParallelism);
		return Math.Max(1, MaxDegreeOfParallelism / activePartitions);
	}

	private static int ResolveMaxDegreeOfParallelism()
	{
		var processorCount = Environment.ProcessorCount;
		if (processorCount <= 0)
			return 1;

		return Math.Max(MinimumDegreeOfParallelism, processorCount);
	}
}
