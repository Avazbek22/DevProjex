using DevProjex.Mcp;

namespace DevProjex.Tests.Unit;

public sealed class McpSearchInspectionBudgetTests
{
	private const long Budget = 1_000_000;

	[Fact]
	public void SelectionThatFitsWholeIsAdmittedWithoutClassifyingAnything()
	{
		var sizes = new Dictionary<string, long?> { ["a.bin"] = 600_000, ["b.cs"] = 400_000 };

		var admission = McpSearchInspectionBudget.Admit(
			[.. sizes.Keys],
			path => sizes[path],
			(_, _, _) => throw new InvalidOperationException("A fitting selection needs no classification."),
			Budget,
			TestContext.Current.CancellationToken);

		Assert.Equal(["a.bin", "b.cs"], admission.Files);
		Assert.False(admission.BudgetReached);
	}

	[Fact]
	public void BinaryAssetsAheadOfTextDoNotSpendTheTextBudget()
	{
		var sizes = new Dictionary<string, long?>
		{
			["assets/a.asset"] = 900_000,
			["assets/b.asset"] = 900_000,
			["src/App.cs"] = 800_000,
			["src/Tail.cs"] = 150_000
		};
		var probed = new List<string>();

		var admission = McpSearchInspectionBudget.Admit(
			[.. sizes.Keys],
			path => sizes[path],
			(path, _, probeContent) =>
			{
				if (probeContent)
					probed.Add(path);
				return !path.EndsWith(".asset", StringComparison.Ordinal);
			},
			Budget,
			TestContext.Current.CancellationToken);

		Assert.Equal(sizes.Keys, admission.Files);
		Assert.False(admission.BudgetReached);
		Assert.Equal(sizes.Keys, probed);
	}

	[Fact]
	public void SourceThatDoesNotFitIsSkippedAndLaterSourcesThatFitAreStillInspected()
	{
		var sizes = new Dictionary<string, long?>
		{
			["a.txt"] = 700_000,
			["b.txt"] = 400_000,
			["c.txt"] = 200_000,
			["d.txt"] = 100_000
		};

		var admission = McpSearchInspectionBudget.Admit(
			[.. sizes.Keys],
			path => sizes[path],
			(_, _, _) => true,
			Budget,
			TestContext.Current.CancellationToken);

		Assert.Equal(["a.txt", "c.txt", "d.txt"], admission.Files);
		Assert.True(admission.BudgetReached);
	}

	[Fact]
	public void SmallSourcesAreChargedBySizeWithoutOpeningThem()
	{
		var small = McpSearchInspectionBudget.ContentProbeMinimumBytes - 1;
		var files = Enumerable.Range(0, 20).Select(static index => $"f{index:D2}.txt").ToArray();
		var probes = 0;

		var admission = McpSearchInspectionBudget.Admit(
			files,
			_ => small,
			(_, _, probeContent) =>
			{
				if (probeContent)
					probes++;
				return true;
			},
			small * 10,
			TestContext.Current.CancellationToken);

		Assert.Equal(files.Take(10), admission.Files);
		Assert.True(admission.BudgetReached);
		Assert.Equal(0, probes);
	}

	[Fact]
	public void SourceWithoutAKnownSizeIsSkippedAsUnbounded()
	{
		var sizes = new Dictionary<string, long?> { ["a.txt"] = 10, ["gone.txt"] = null, ["b.txt"] = -1, ["c.txt"] = 20 };

		var admission = McpSearchInspectionBudget.Admit(
			[.. sizes.Keys],
			path => sizes[path],
			(_, _, _) => true,
			Budget,
			TestContext.Current.CancellationToken);

		Assert.Equal(["a.txt", "c.txt"], admission.Files);
		Assert.True(admission.BudgetReached);
	}

	[Fact]
	public void SourceThePipelineWillNotDecodeIsAdmittedFreeWhateverItsSize()
	{
		var sizes = new Dictionary<string, long?> { ["huge.log"] = Budget * 10, ["App.cs"] = Budget };

		var admission = McpSearchInspectionBudget.Admit(
			[.. sizes.Keys],
			path => sizes[path],
			(_, size, _) => size <= Budget,
			Budget,
			TestContext.Current.CancellationToken);

		Assert.Equal(["huge.log", "App.cs"], admission.Files);
		Assert.False(admission.BudgetReached);
	}
}
