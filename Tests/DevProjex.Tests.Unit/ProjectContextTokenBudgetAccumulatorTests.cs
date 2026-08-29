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
}
