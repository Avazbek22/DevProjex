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
		for (var index = 0; index < 27; index++)
			Assert.False(budget.TryInclude($"file-{index:D2}.cs", 8 + index * 4));

		var report = budget.CreateReport();

		Assert.Equal(27, report.SkippedFileCount);
		Assert.Equal(25, report.LargestSkippedFiles.Count);
		Assert.Equal("file-26.cs", report.LargestSkippedFiles[0].Path);
		Assert.Equal(2, report.AdditionalSkippedFileCount);
	}
}
