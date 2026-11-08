namespace DevProjex.Application.Dependencies;

/// <summary>
/// The files a set of changed files affects, kept to what can be stated exactly: the non-test files
/// that import a changed file directly, and the tests that import a changed file directly or are
/// named after one. Each list is capped; the counts are before the cap.
/// </summary>
public sealed record DependencyChangeImpact(
	IReadOnlyList<string> Dependents,
	int DependentCount,
	IReadOnlyList<string> Tests,
	int TestCount)
{
	public const int MaximumSeeds = 16;
	public const int MaximumListedFiles = 5;

	private static readonly HashSet<string> PackageEntryFileNames = new(StringComparer.Ordinal)
	{
		"__init__.py", "__init__.pyi",
		"index.ts", "index.tsx", "index.mts", "index.cts", "index.d.ts",
		"index.js", "index.jsx", "index.mjs", "index.cjs",
		"mod.rs"
	};

	public static DependencyChangeImpact None { get; } = new([], 0, [], 0);

	public bool IsEmpty => DependentCount == 0 && TestCount == 0;

	/// <summary>
	/// Relates the changed files that have supported facts in <paramref name="index"/>. More than
	/// <see cref="MaximumSeeds"/> such files is too broad a change to summarize, and yields
	/// <see cref="None"/>. Every named file is one of <paramref name="candidateFiles"/>. Tests named
	/// after a changed file come before tests that only import one.
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
		var namedTests = new SortedSet<string>(StringComparer.Ordinal);
		var importingTests = new SortedSet<string>(StringComparer.Ordinal);
		foreach (var seed in seeds)
		{
			foreach (var importer in DirectImporters(index, seed))
			{
				if (changed.Contains(importer.Key) || !allowed.Contains(importer.Key))
					continue;
				if (DependencyTestFiles.IsTestPath(importer.Key))
					importingTests.Add(importer.Key);
				else if (!IsPackageReExport(importer.Key, importer.Value))
					dependents.Add(importer.Key);
			}
			foreach (var test in DependencyTestFiles.Find(seed, candidateFiles, index: null, int.MaxValue))
			{
				if (!changed.Contains(test))
					namedTests.Add(test);
			}
		}
		importingTests.ExceptWith(namedTests);
		var tests = namedTests.Concat(importingTests).ToArray();
		return new DependencyChangeImpact(
			dependents.Take(MaximumListedFiles).ToArray(),
			dependents.Count,
			tests.Take(MaximumListedFiles).ToArray(),
			tests.Length);
	}

	// Each file that imports the target itself, with the edges that do so. A name that reaches the
	// target only through another module's re-exports is not a direct import.
	private static Dictionary<string, List<DependencyEdge>> DirectImporters(
		DependencyIndexSnapshot index,
		string targetPath)
	{
		var importers = new Dictionary<string, List<DependencyEdge>>(StringComparer.Ordinal);
		if (!index.EdgesByTarget.TryGetValue(targetPath, out var edges))
			return importers;
		foreach (var edge in edges)
		{
			if (edge.Status != ResolutionStatus.Resolved || edge.ThroughReExport ||
				string.Equals(edge.Source, targetPath, StringComparison.Ordinal))
				continue;
			if (!importers.TryGetValue(edge.Source, out var sourceEdges))
				importers.Add(edge.Source, sourceEdges = []);
			sourceEdges.Add(edge);
		}
		return importers;
	}

	// A package entry file such as __init__.py, index.ts or mod.rs that only imports the target,
	// without referring to anything it declares, passes the target on rather than using it.
	private static bool IsPackageReExport(string path, IReadOnlyList<DependencyEdge> edges) =>
		PackageEntryFileNames.Contains(path[(path.LastIndexOf('/') + 1)..]) &&
		edges.All(static edge => edge.Layer == EvidenceLayer.ExplicitImport);
}
