namespace DevProjex.Tests.Unit;

public sealed class GitIgnoreScanContextPathCompositionTests(ITestOutputHelper output)
{
	public static TheoryData<string, string, bool, string[]> PathCases => new()
	{
		{ string.Empty, "root.log", false, ["*.log"] },
		{ "src/module", "cache/item.tmp", false, ["*.tmp"] },
		{ "generated", string.Empty, true, ["/generated/"] },
		{ "nested", $"{new string('a', 600)}/artifact.bin", false, ["*.bin"] }
	};

	[Theory]
	[MemberData(nameof(PathCases))]
	public void Evaluate_ComposedRelativePathMatchesLegacyStringProjection(
		string baseRelativePath,
		string scanRelativePath,
		bool isDirectory,
		string[] patterns)
	{
		var rootPath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "devprojex-ignore-span-parity"));
		var scanRootPath = CombinePlatformPath(rootPath, baseRelativePath);
		var fullPath = CombinePlatformPath(scanRootPath, scanRelativePath);
		var name = scanRelativePath.Length == 0
			? Path.GetFileName(scanRootPath)
			: Path.GetFileName(scanRelativePath);
		var matcher = GitIgnoreMatcher.Build(rootPath, patterns);
		var rules = CreateRules(matcher);
		var context = rules.CreateGitIgnoreScanContext(scanRootPath);
		var legacyRelativePath = CombineRelativePath(baseRelativePath, scanRelativePath);

		var expected = EvaluateLegacy(matcher, legacyRelativePath, isDirectory, name);
		var actual = context.Evaluate(fullPath, scanRelativePath, isDirectory, name);

		Assert.Equal(expected, actual);
	}

	[Fact]
	[Trait("Category", "LocalPerformance")]
	public void ComposedRelativePathBenchmarkPreservesDecisionsAndReducesAllocations()
	{
		if (!string.Equals(
			    Environment.GetEnvironmentVariable("DEVPROJEX_RUN_LARGE_PERF_TESTS"),
			    "1",
			    StringComparison.Ordinal))
		{
			Assert.Skip("Set DEVPROJEX_RUN_LARGE_PERF_TESTS=1 for the pre-release performance gate.");
		}

		const int pathCount = 100_000;
		const string baseRelativePath = "src";
		var rootPath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "devprojex-ignore-span-benchmark"));
		var scanRootPath = Path.Combine(rootPath, baseRelativePath);
		var matcher = GitIgnoreMatcher.Build(rootPath, ["*.tmp", "!keep.tmp"]);
		var context = CreateRules(matcher).CreateGitIgnoreScanContext(scanRootPath);
		var paths = new BenchmarkPath[pathCount];
		for (var index = 0; index < paths.Length; index++)
		{
			var name = index % 97 == 0 ? "keep.tmp" : $"file-{index}.tmp";
			var relativePath = $"module-{index % 128}/{name}";
			paths[index] = new BenchmarkPath(
				Path.Combine(scanRootPath, relativePath.Replace('/', Path.DirectorySeparatorChar)),
				relativePath,
				name);
		}

		_ = MeasureLegacy(matcher, baseRelativePath, paths);
		_ = MeasureOptimized(context, paths);

		var legacy = MeasureLegacy(matcher, baseRelativePath, paths);
		var optimized = MeasureOptimized(context, paths);

		Assert.Equal(legacy.DecisionChecksum, optimized.DecisionChecksum);
		Assert.True(
			optimized.AllocatedBytes < legacy.AllocatedBytes / 4,
			$"Expected composed-path allocations below 25% of baseline, but measured " +
			$"{optimized.AllocatedBytes:N0} B versus {legacy.AllocatedBytes:N0} B.");
		output.WriteLine(
			$"GitIgnoreScanContext composed paths: legacy {legacy.AllocatedBytes:N0} B, " +
			$"span {optimized.AllocatedBytes:N0} B for {pathCount:N0} evaluations.");
	}

	private static Measurement MeasureLegacy(
		GitIgnoreMatcher matcher,
		string baseRelativePath,
		IReadOnlyList<BenchmarkPath> paths)
	{
		var checksum = 0;
		var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
		foreach (var path in paths)
		{
			var matcherRelativePath = $"{baseRelativePath}/{path.RelativePath}";
			var decision = EvaluateLegacy(
				matcher,
				matcherRelativePath,
				isDirectory: false,
				path.Name);
			checksum = AddDecisionToChecksum(checksum, decision);
		}

		return new Measurement(
			checksum,
			GC.GetAllocatedBytesForCurrentThread() - allocatedBefore);
	}

	private static Measurement MeasureOptimized(
		IgnoreRules.GitIgnoreScanContext context,
		IReadOnlyList<BenchmarkPath> paths)
	{
		var checksum = 0;
		var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
		foreach (var path in paths)
		{
			var decision = context.Evaluate(
				path.FullPath,
				path.RelativePath,
				isDirectory: false,
				path.Name);
			checksum = AddDecisionToChecksum(checksum, decision);
		}

		return new Measurement(
			checksum,
			GC.GetAllocatedBytesForCurrentThread() - allocatedBefore);
	}

	private static IgnoreRules.GitIgnoreEvaluation EvaluateLegacy(
		GitIgnoreMatcher matcher,
		string relativePath,
		bool isDirectory,
		string name)
	{
		if (relativePath.Length == 0)
			return IgnoreRules.GitIgnoreEvaluation.NotIgnored;

		var evaluation = matcher.EvaluateRelativeNormalized(relativePath.AsSpan(), isDirectory, name);
		if (!evaluation.HasMatch || !evaluation.IsIgnored)
			return IgnoreRules.GitIgnoreEvaluation.NotIgnored;
		if (!isDirectory)
			return new IgnoreRules.GitIgnoreEvaluation(IsIgnored: true, ShouldTraverseIgnoredDirectory: false);

		return new IgnoreRules.GitIgnoreEvaluation(
			IsIgnored: true,
			ShouldTraverseIgnoredDirectory: matcher.ShouldTraverseIgnoredDirectoryRelativeNormalized(
				relativePath.AsSpan(),
				name));
	}

	private static int AddDecisionToChecksum(
		int checksum,
		IgnoreRules.GitIgnoreEvaluation decision) =>
		unchecked(
			checksum * 31 +
			(decision.IsIgnored ? 1 : 0) +
			(decision.ShouldTraverseIgnoredDirectory ? 2 : 0));

	private static IgnoreRules CreateRules(GitIgnoreMatcher matcher) =>
		new(
			IgnoreHiddenFolders: false,
			IgnoreHiddenFiles: false,
			IgnoreDotFolders: false,
			IgnoreDotFiles: false,
			SmartIgnoredFolders: new HashSet<string>(StringComparer.Ordinal),
			SmartIgnoredFiles: new HashSet<string>(StringComparer.Ordinal))
		{
			UseGitIgnore = true,
			GitIgnoreMatcher = matcher
		};

	private static string CombineRelativePath(string baseRelativePath, string scanRelativePath)
	{
		if (baseRelativePath.Length == 0)
			return scanRelativePath;
		if (scanRelativePath.Length == 0)
			return baseRelativePath;
		return $"{baseRelativePath}/{scanRelativePath}";
	}

	private static string CombinePlatformPath(string rootPath, string relativePath) =>
		relativePath.Length == 0
			? rootPath
			: Path.Combine(rootPath, relativePath.Replace('/', Path.DirectorySeparatorChar));

	private readonly record struct BenchmarkPath(
		string FullPath,
		string RelativePath,
		string Name);

	private readonly record struct Measurement(
		int DecisionChecksum,
		long AllocatedBytes);
}
