using DevProjex.Application.Context;

namespace DevProjex.Tests.Unit;

public sealed class ProjectContextTokenBudgetAccumulatorTests
{
	[Fact]
	public void TryInclude_UsesExactRemainingBudgetAndContinuesAfterSkip()
	{
		var budget = new ProjectContextTokenBudgetAccumulator(3);

		Assert.False(budget.TryInclude("large.cs", 16));
		Assert.True(budget.TryInclude("exact.cs", 12));

		var report = budget.CreateReport();
		Assert.Equal(1, report.IncludedFileCount);
		Assert.Equal(1, report.SkippedFileCount);
		Assert.Equal(3, report.IncludedEstimatedTokens);
		Assert.Equal(4, report.SkippedEstimatedTokens);
	}

	[Fact]
	public void CreateReport_RepresentsEmptyInput()
	{
		var report = new ProjectContextTokenBudgetAccumulator(1).CreateReport();

		Assert.Equal(1, report.MaximumEstimatedTokens);
		Assert.Equal(0, report.IncludedFileCount);
		Assert.Equal(0, report.SkippedFileCount);
		Assert.Empty(report.LargestSkippedFiles);
		Assert.Empty(report.RankedSkippedFiles!);
	}

	[Fact]
	public void CreateReport_SortsAndCapsSkippedFiles()
	{
		var budget = new ProjectContextTokenBudgetAccumulator(1);
		const int skippedFileCount = 10_000;
		for (var index = 0; index < skippedFileCount; index++)
			Assert.False(budget.TryInclude($"file-{index:D2}.cs", 8 + index * 4));

		var report = budget.CreateReport();

		Assert.Equal(skippedFileCount, report.SkippedFileCount);
		Assert.Equal(25, report.LargestSkippedFiles.Count);
		Assert.Equal("file-9999.cs", report.LargestSkippedFiles[0].Path);
		Assert.Equal(skippedFileCount - 25, report.AdditionalSkippedFileCount);
	}

	[Fact]
	public void CreateReport_OrdersEqualEstimatesByPath()
	{
		var budget = new ProjectContextTokenBudgetAccumulator(1);
		Assert.False(budget.TryInclude("z-last.cs", 8));
		Assert.False(budget.TryInclude("a-first.cs", 8));

		Assert.Equal(
			["a-first.cs", "z-last.cs"],
			budget.CreateReport().LargestSkippedFiles.Select(static file => file.Path));
	}

	[Fact]
	public void CreateReport_OrdersCaseDistinctEqualEstimatesByCanonicalIdentity()
	{
		var budget = new ProjectContextTokenBudgetAccumulator(1);
		Assert.False(budget.TryInclude("foo.cs", 8));
		Assert.False(budget.TryInclude("Foo.cs", 8));

		Assert.Equal(
			["Foo.cs", "foo.cs"],
			budget.CreateReport().LargestSkippedFiles.Select(static file => file.Path));
	}

	[Fact]
	public void CreateReport_KeepsHighestPrioritySkipsSeparateFromLargestSkips()
	{
		var budget = new ProjectContextTokenBudgetAccumulator(1);
		for (var priority = 1; priority <= 30; priority++)
		{
			Assert.False(budget.TryInclude(
				$"priority-{priority:D2}.cs",
				transformedCharacterCount: 8 + priority * 4,
				priority: priority));
		}

		var report = budget.CreateReport();
		var ranked = Assert.IsAssignableFrom<IReadOnlyList<ProjectContextTokenBudgetSkippedFile>>(
			report.RankedSkippedFiles);

		Assert.Equal("priority-30.cs", report.LargestSkippedFiles[0].Path);
		Assert.Equal(Enumerable.Range(1, 10), ranked.Select(static file => file.Priority.GetValueOrDefault()));
		Assert.Equal("priority-01.cs", ranked[0].Path);
	}
}
