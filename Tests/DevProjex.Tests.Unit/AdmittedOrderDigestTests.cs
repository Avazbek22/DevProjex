using DevProjex.Application.Context;

namespace DevProjex.Tests.Unit;

public sealed class AdmittedOrderDigestTests
{
	private const string Root = "/project";

	private static ProjectContextTokenBudgetAccumulator Budget(long maximum = 1_000_000) =>
		new(maximum, Root);

	private static string Source(string relativePath) =>
		Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));

	[Fact]
	public void TheDigestCoversEveryAdmittedFileInAdmissionOrder()
	{
		var first = Budget();
		first.TryInclude("a.cs", 4, sourcePath: Source("a.cs"));
		first.TryInclude("b.cs", 4, sourcePath: Source("b.cs"));

		var second = Budget();
		second.TryInclude("b.cs", 4, sourcePath: Source("b.cs"));
		second.TryInclude("a.cs", 4, sourcePath: Source("a.cs"));

		var same = Budget();
		same.TryInclude("a.cs", 4, sourcePath: Source("a.cs"));
		same.TryInclude("b.cs", 4, sourcePath: Source("b.cs"));

		Assert.Equal(first.CreateReport().IncludedOrderDigest, same.CreateReport().IncludedOrderDigest);
		// It is an order digest: the same set admitted in a different order is a different plan.
		Assert.NotEqual(first.CreateReport().IncludedOrderDigest, second.CreateReport().IncludedOrderDigest);
	}

	[Fact]
	public void SkippedFilesDoNotEnterTheDigest()
	{
		var withoutSkip = Budget(2);
		withoutSkip.TryInclude("small.cs", 4, sourcePath: Source("small.cs"));

		var withSkip = Budget(2);
		Assert.False(withSkip.TryInclude("large.cs", 400, sourcePath: Source("large.cs")));
		Assert.True(withSkip.TryInclude("small.cs", 4, sourcePath: Source("small.cs")));

		Assert.Equal(
			withoutSkip.CreateReport().IncludedOrderDigest,
			withSkip.CreateReport().IncludedOrderDigest);
	}

	[Fact]
	public void TheDigestIsTakenFromTheProjectRelativePathNotThePresentedPath()
	{
		// The printed path varies by format and view, and a redacted root can replace it entirely.
		// Two calls that admit the same files must still agree, so the digest uses the source path.
		var relative = Budget();
		relative.TryInclude("src/a.cs", 4, sourcePath: Source("src/a.cs"));

		var presentedDifferently = Budget();
		presentedDifferently.TryInclude(
			"https://example.invalid/repo/src/a.cs",
			4,
			sourcePath: Source("src/a.cs"));

		Assert.Equal(
			relative.CreateReport().IncludedOrderDigest,
			presentedDifferently.CreateReport().IncludedOrderDigest);
	}

	[Fact]
	public void TheDigestIsIndependentOfPathSeparatorForm()
	{
		var forward = Budget();
		forward.TryInclude("src/a.cs", 4, sourcePath: $"{Root}/src/a.cs");

		var platform = Budget();
		platform.TryInclude("src/a.cs", 4, sourcePath: Source("src/a.cs"));

		Assert.Equal(
			forward.CreateReport().IncludedOrderDigest,
			platform.CreateReport().IncludedOrderDigest);
	}

	[Fact]
	public void AnEmptyAdmissionHasAStableDigest()
	{
		Assert.Equal(Budget().CreateReport().IncludedOrderDigest, Budget().CreateReport().IncludedOrderDigest);
		Assert.False(string.IsNullOrEmpty(Budget().CreateReport().IncludedOrderDigest));
	}

	[Fact]
	public void AdmittedEntriesKeepAdmissionOrderWithTheirCostAndRank()
	{
		var budget = Budget();
		budget.TryInclude("first.cs", 8, priority: 1, sourcePath: Source("first.cs"));
		budget.TryInclude("second.cs", 4, priority: 2, hop: 3, sourcePath: Source("second.cs"));

		var report = budget.CreateReport();

		Assert.Equal(2, report.IncludedFileCount);
		Assert.Equal(2, report.IncludedFiles.Count);
		Assert.Equal("first.cs", report.IncludedFiles[0].Path);
		Assert.Equal(2, report.IncludedFiles[0].EstimatedTokens);
		Assert.Equal(1, report.IncludedFiles[0].Priority);
		Assert.Null(report.IncludedFiles[0].Hop);
		Assert.Equal("second.cs", report.IncludedFiles[1].Path);
		Assert.Equal(1, report.IncludedFiles[1].EstimatedTokens);
		Assert.Equal(3, report.IncludedFiles[1].Hop);
		Assert.Equal(0, report.AdditionalIncludedFileCount);
	}

	[Fact]
	public void TheRetainedPrefixIsBoundedAndTheRemainderIsCounted()
	{
		var budget = Budget();
		for (var index = 0; index < ProjectContextTokenBudgetAccumulator.MaximumReportedIncludedFiles + 25; index++)
			budget.TryInclude($"file{index}.cs", 4, sourcePath: Source($"file{index}.cs"));

		var report = budget.CreateReport();

		Assert.Equal(
			ProjectContextTokenBudgetAccumulator.MaximumReportedIncludedFiles + 25,
			report.IncludedFileCount);
		Assert.Equal(
			ProjectContextTokenBudgetAccumulator.MaximumReportedIncludedFiles,
			report.IncludedFiles.Count);
		Assert.Equal(25, report.AdditionalIncludedFileCount);
		// The prefix is cut, never the digest: it still covers the complete admitted list.
		Assert.Equal("file0.cs", report.IncludedFiles[0].Path);
	}

	[Fact]
	public void APrecomputedReportCarriesTheDigestItWasBuiltWith()
	{
		var budget = Budget();
		budget.TryInclude("a.cs", 4, sourcePath: Source("a.cs"));
		var original = budget.CreateReport();

		var replayed = new ProjectContextTokenBudgetAccumulator(original);
		replayed.TryInclude("ignored.cs", 4, sourcePath: Source("ignored.cs"));

		// A write that replays a completed admission must report the digest of that admission, not a
		// fresh one computed from whatever it happened to walk.
		Assert.Equal(original.IncludedOrderDigest, replayed.CreateReport().IncludedOrderDigest);
	}
}
