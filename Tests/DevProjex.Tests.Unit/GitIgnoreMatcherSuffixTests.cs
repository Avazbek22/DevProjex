using System.Diagnostics;

namespace DevProjex.Tests.Unit;

public sealed class GitIgnoreMatcherSuffixTests(ITestOutputHelper output)
{
	[Theory]
	[InlineData(false, false)]
	[InlineData(false, true)]
	[InlineData(true, false)]
	[InlineData(true, true)]
	public void AsciiSuffixMatchesTheLegacyRegexAcrossNameBoundaries(bool ignoreCase, bool normalizeUnicode)
	{
		var semantics = new GitPathComparisonSemantics(ignoreCase, normalizeUnicode);
		var patterns = new[] { "*.log", "*.LOG", "*.tar.gz", "*.a+b", "*.a(b)", "*.a$b", "*.a]b", "*.txt\n" };
		var prefixes = new[] { "", "file", "資料😀", "cafe\u0301", "\n", "file\n", "file\r\n", "dir/", "dir\\" };
		foreach (var pattern in patterns)
		{
			var matcher = GitIgnoreMatcher.Build("/repo", [pattern], semantics);
			var legacy = GitIgnoreMatcher.Build("/repo", [ForceRegex(pattern)], semantics);
			foreach (var prefix in prefixes)
			{
				foreach (var ending in new[] { "", "\n", "\n\n", "\r\n", "extra" })
				{
					var name = prefix + pattern[1..] + ending;
					foreach (var isDirectory in new[] { false, true })
					{
						Assert.Equal(legacy.EvaluateRelativeRulesOnlyNormalized("file", isDirectory, name),
							matcher.EvaluateRelativeRulesOnlyNormalized("file", isDirectory, name));
					}
				}
			}
		}
	}

	[Theory]
	[InlineData("file.log", true)]
	[InlineData(".log", true)]
	[InlineData("資料😀.log", true)]
	[InlineData("line\nbreak.log", true)]
	[InlineData("file.log\n", true)]
	[InlineData("file.log\n\n", false)]
	[InlineData("file.log\r\n", false)]
	[InlineData("folder/file.log", false)]
	[InlineData("file.logger", false)]
	[InlineData("file.LOG", false)]
	public void SuffixKeepsExistingNameAndTerminalLineFeedSemantics(string name, bool ignored)
	{
		var matcher = GitIgnoreMatcher.Build("/repo", ["*.log"], new(false, false));

		Assert.Equal(ignored, matcher.EvaluateRelativeRulesOnlyNormalized("file", false, name).IsIgnored);
	}

	[Fact]
	public void NegationAndRuleOrderKeepLastMatchAndIgnoredAncestorSemantics()
	{
		var patterns = new[] { "*.log", "!*.keep.log", "blocked/", "*.private.keep.log" };
		var matcher = GitIgnoreMatcher.Build("/repo", patterns, new(false, false));
		var legacy = GitIgnoreMatcher.Build("/repo", patterns.Select(ForceRegex).ToArray(), new(false, false));
		foreach (var path in new[]
		{
			"trace.log", "trace.keep.log", "trace.private.keep.log", "nested/trace.keep.log",
			"blocked/trace.keep.log", "資料😀/trace.log", "folder.log"
		})
		{
			foreach (var isDirectory in new[] { false, true })
				Assert.Equal(legacy.EvaluateRelative(path, isDirectory, Path.GetFileName(path)),
					matcher.EvaluateRelative(path, isDirectory, Path.GetFileName(path)));
		}
		Assert.False(matcher.EvaluateRelative("trace.keep.log", false, "trace.keep.log").IsIgnored);
		Assert.True(matcher.EvaluateRelative("blocked/trace.keep.log", false, "trace.keep.log").IsIgnored);
	}

	[Theory]
	[InlineData(false, "*.log", "file.LOG", false)]
	[InlineData(true, "*.log", "file.LOG", true)]
	[InlineData(true, "*.log", "İ.LOG", true)]
	[InlineData(true, "*.key", "file.KEY", false)]
	public void SuffixUsesOnlyConfiguredAsciiCaseFold(bool ignoreCase, string pattern, string name, bool ignored)
	{
		var matcher = GitIgnoreMatcher.Build("/repo", [pattern], new(ignoreCase, false));

		Assert.Equal(ignored, matcher.EvaluateRelative(name, false, name).IsIgnored);
	}

	[Theory]
	[InlineData("*.log/", "file.log", false, false)]
	[InlineData("*.log/", "file.log", true, true)]
	[InlineData("/*.log", "nested/file.log", false, false)]
	[InlineData("nested/*.log", "nested/file.log", false, true)]
	[InlineData("*.[Ll]og", "file.Log", false, true)]
	[InlineData("*.lo?", "file.log", false, true)]
	[InlineData("*.l*g", "file.log", false, true)]
	[InlineData(@"*\.log", "file.log", false, true)]
	[InlineData(@"*.lo\?", "file.lo?", false, true)]
	[InlineData("*.ログ", "file.ログ", false, true)]
	[InlineData("prefix*", "prefix.log", false, true)]
	[InlineData("*~", "backup~", false, true)]
	[InlineData("*.", "file.", false, true)]
	public void PatternsOutsideTheNarrowSuffixShapeKeepTheirFallback(
		string pattern, string path, bool isDirectory, bool ignored)
	{
		var matcher = GitIgnoreMatcher.Build("/repo", [pattern], new(false, false));

		Assert.Equal(ignored, matcher.EvaluateRelative(path, isDirectory, Path.GetFileName(path)).IsIgnored);
	}

	[Fact]
	[Trait("Category", "LocalPerformance")]
	public void MeasureCurrentRootSuffixAndRemainingConstruction()
	{
		var requestedRoot = Environment.GetEnvironmentVariable("DEVPROJEX_GUI_BENCHMARK_ROOT");
		Assert.SkipWhen(string.IsNullOrWhiteSpace(requestedRoot), "Set DEVPROJEX_GUI_BENCHMARK_ROOT for read-only profiling.");
		var projectRoot = Path.GetFullPath(requestedRoot!);
		var lines = File.ReadAllLines(Path.Combine(projectRoot, ".gitignore"));
		var suffixLines = lines.Where(IsSuffixCandidate).ToArray();
		var remainingLines = lines.Where(line => !IsSuffixCandidate(line)).ToArray();
		var semantics = GitConfigPathComparisonSemanticsResolver.Instance.Resolve(projectRoot);
		Assert.True(semantics.IsAuthoritative);
		foreach (var (scenario, subset) in new[]
		{
			("suffix", suffixLines), ("remaining", remainingLines), ("all", lines)
		})
		{
			var forcedRegex = subset.Select(ForceRegex).ToArray();
			_ = GitIgnoreMatcher.Build(projectRoot, subset, semantics);
			_ = GitIgnoreMatcher.Build(projectRoot, forcedRegex, semantics);
			var current = new List<BuildSample>();
			var legacy = new List<BuildSample>();
			for (var run = 0; run < 7; run++)
			{
				legacy.Add(Measure(projectRoot, forcedRegex, semantics));
				current.Add(Measure(projectRoot, subset, semantics));
			}
			output.WriteLine(JsonSerializer.Serialize(new
			{
				Scenario = scenario,
				SourceLines = subset.Length,
				LegacyMedianMilliseconds = legacy.Select(sample => sample.Milliseconds).Order().ElementAt(3),
				CurrentMedianMilliseconds = current.Select(sample => sample.Milliseconds).Order().ElementAt(3),
				LegacyMedianAllocatedBytes = legacy.Select(sample => sample.AllocatedBytes).Order().ElementAt(3),
				CurrentMedianAllocatedBytes = current.Select(sample => sample.AllocatedBytes).Order().ElementAt(3)
			}));
		}
	}

	[Fact]
	[Trait("Category", "LocalPerformance")]
	public void MeasureCurrentRootGitIgnoreRuleConstruction()
	{
		var requestedRoot = Environment.GetEnvironmentVariable("DEVPROJEX_GUI_BENCHMARK_ROOT");
		Assert.SkipWhen(string.IsNullOrWhiteSpace(requestedRoot), "Set DEVPROJEX_GUI_BENCHMARK_ROOT for read-only profiling.");
		var projectRoot = Path.GetFullPath(requestedRoot!);
		var lines = File.ReadAllLines(Path.Combine(projectRoot, ".gitignore"));
		var semantics = GitConfigPathComparisonSemanticsResolver.Instance.Resolve(projectRoot);
		Assert.True(semantics.IsAuthoritative);
		var measurements = new List<(int Line, string Pattern, double Milliseconds, long AllocatedBytes)>();
		for (var index = 0; index < lines.Length; index++)
		{
			var sample = Measure(projectRoot, [lines[index]], semantics);
			measurements.Add((index + 1, lines[index], sample.Milliseconds, sample.AllocatedBytes));
		}
		output.WriteLine(JsonSerializer.Serialize(new
		{
			SourceLines = lines.Length,
			TotalPerRuleAllocatedBytes = measurements.Sum(static sample => sample.AllocatedBytes),
			TopRules = measurements
				.OrderByDescending(static sample => sample.AllocatedBytes)
				.Take(16)
				.Select(static sample => new
				{
					sample.Line,
					sample.Pattern,
					sample.Milliseconds,
					sample.AllocatedBytes
				})
				.ToArray()
		}));
	}

	private static BuildSample Measure(string root, string[] patterns, GitPathComparisonSemantics semantics)
	{
		var before = GC.GetAllocatedBytesForCurrentThread();
		var timer = Stopwatch.StartNew();
		var matcher = GitIgnoreMatcher.Build(root, patterns, semantics);
		timer.Stop();
		var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
		GC.KeepAlive(matcher);
		return new BuildSample(timer.Elapsed.TotalMilliseconds, allocated);
	}

	private static bool IsSuffixCandidate(string source)
	{
		var pattern = source.TrimEnd(' ').AsSpan();
		if (pattern.StartsWith("!", StringComparison.Ordinal))
			pattern = pattern[1..];
		if (!pattern.StartsWith("*.", StringComparison.Ordinal) || pattern.Length <= 2)
			return false;
		foreach (var character in pattern[1..])
		{
			if (character > 0x7f || character is '/' or '\\' or '*' or '?' or '[')
				return false;
		}
		return true;
	}

	// Escaping the literal dot selects the unchanged regex path with the same wildmatch meaning.
	private static string ForceRegex(string pattern) => !IsSuffixCandidate(pattern)
		? pattern
		: pattern.Insert(pattern[0] == '!' ? 2 : 1, "\\");

	private readonly record struct BuildSample(double Milliseconds, long AllocatedBytes);
}
