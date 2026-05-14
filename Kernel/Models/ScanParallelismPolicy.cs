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

	public static ParallelOptions CreateOptions(CancellationToken cancellationToken = default) => new()
	{
		MaxDegreeOfParallelism = MaxDegreeOfParallelism,
		CancellationToken = cancellationToken
	};

	private static int ResolveMaxDegreeOfParallelism()
	{
		var processorCount = Environment.ProcessorCount;
		if (processorCount <= 0)
			return 1;

		return Math.Max(MinimumDegreeOfParallelism, processorCount);
	}
}
