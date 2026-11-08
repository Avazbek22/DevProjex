namespace DevProjex.Application.Dependencies;

/// <summary>
/// The files a set of changed files affects: resolved dependents that are not tests, and the tests
/// that cover the changed files. Each list is ordinal and capped; the counts are before the cap.
/// </summary>
public sealed record DependencyChangeImpact(
	IReadOnlyList<string> Dependents,
	int DependentCount,
	IReadOnlyList<string> Tests,
	int TestCount)
{
	public const int MaximumSeeds = 16;
	public const int MaximumListedFiles = 10;

	public static DependencyChangeImpact None { get; } = new([], 0, [], 0);

	public bool IsEmpty => DependentCount == 0 && TestCount == 0;

	/// <summary>
	/// Relates the changed files that have supported facts in <paramref name="index"/>. More than
	/// <see cref="MaximumSeeds"/> such files is too broad a change to summarize, and yields
	/// <see cref="None"/>. Every named file is one of <paramref name="candidateFiles"/>.
	/// </summary>
	public static DependencyChangeImpact Compute(
		DependencyIndexSnapshot index,
		IReadOnlyCollection<string> changedFiles,
		IReadOnlyCollection<string> candidateFiles)
	{
		ArgumentNullException.ThrowIfNull(index);
		ArgumentNullException.ThrowIfNull(changedFiles);
		ArgumentNullException.ThrowIfNull(candidateFiles);
		var changed = changedFiles.ToHashSet(StringComparer.Ordinal);
		var seeds = changed
			.Where(path => index.FileByPath.TryGetValue(path, out var facts) &&
				facts.Status == DependencyFileStatus.Supported)
			.Order(StringComparer.Ordinal)
			.ToArray();
		if (seeds.Length is 0 or > MaximumSeeds)
			return None;

		var allowed = candidateFiles as IReadOnlySet<string> ?? candidateFiles.ToHashSet(StringComparer.Ordinal);
		var dependents = new SortedSet<string>(StringComparer.Ordinal);
		var tests = new SortedSet<string>(StringComparer.Ordinal);
		foreach (var seed in seeds)
		{
			foreach (var dependent in DependencyTestFiles.ResolvedDependents(index, seed))
			{
				if (changed.Contains(dependent) || !allowed.Contains(dependent))
					continue;
				if (DependencyTestFiles.IsTestPath(dependent))
					tests.Add(dependent);
				else
					dependents.Add(dependent);
			}
			foreach (var test in DependencyTestFiles.Find(seed, candidateFiles, index: null, int.MaxValue))
				tests.Add(test);
		}
		return new DependencyChangeImpact(
			dependents.Take(MaximumListedFiles).ToArray(),
			dependents.Count,
			tests.Take(MaximumListedFiles).ToArray(),
			tests.Count);
	}
}
