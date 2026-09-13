using DevProjex.Application.Ranking;
using System.Diagnostics;
using Xunit;

namespace DevProjex.RankingEval.Tests;

public sealed class EvaluationMetricsTests
{
	[Fact]
	public async Task IndexMeasurementRejectsUnknownModeBeforeOpeningTheRepository()
	{
		var registry = new EvaluationRegistry
		{
			Protocol = "fixture",
			Selection = new RegistrySelection { Budgets = [4_000] },
			Orders = [],
			Performance = new RegistryPerformance { Repetitions = 5 },
			Repositories =
			[
				new RegistryRepository
				{
					Id = "fixture",
					Url = "https://example.invalid/fixture.git",
					Commit = new string('0', 40),
					Tasks = []
				}
			]
		};

		await Assert.ThrowsAsync<ArgumentException>(() => EvaluationRunner.MeasureIndexOneAsync(
			registry,
			"fixture",
			Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
			Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
			"unknown",
			TestContext.Current.CancellationToken));
	}

	[Fact]
	public async Task BoundedProcessRunnerKillsAHungChildWhenCancelled()
	{
		var start = new ProcessStartInfo("dotnet")
		{
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			CreateNoWindow = true
		};
		start.ArgumentList.Add(typeof(EvaluationRunner).Assembly.Location);
		start.ArgumentList.Add("hang");
		var started = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
		using var cancellation = new CancellationTokenSource();
		var run = BoundedProcessRunner.RunAsync(
			start,
			TimeSpan.FromMinutes(1),
			maximumOutputCharacters: 1024,
			cancellation.Token,
			processId => started.TrySetResult(processId));
		var processId = await started.Task.WaitAsync(
			TimeSpan.FromSeconds(10),
			TestContext.Current.CancellationToken);

		cancellation.Cancel();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
		Assert.Throws<ArgumentException>(() => Process.GetProcessById(processId));
	}

	[Fact]
	public void PersonalizedPageRankUsesGraphSeedsFromAMixedSupportedSet()
	{
		var report = Ranking("README.md", "A.cs", "B.cs");
		var graph = Graph(["A.cs", "B.cs"], [[1], [0]]);
		var result = EvaluationFocusComparators.PersonalizedPageRank(
			report,
			[
				new FocusRankingSeedRequest("README.md", report.Entries[0].FullPath),
				new FocusRankingSeedRequest("A.cs", report.Entries[1].FullPath)
			],
			graph,
			TestContext.Current.CancellationToken);

		Assert.True(result.Available);
		Assert.Equal(["README.md", "A.cs", "B.cs"], result.Paths);
	}

	[Fact]
	public void PersonalizedPageRankTreatsANonGraphOnlySeedAsUnavailable()
	{
		var report = Ranking("README.md", "A.cs");
		var result = EvaluationFocusComparators.PersonalizedPageRank(
			report,
			[new FocusRankingSeedRequest("README.md", report.Entries[0].FullPath)],
			Graph(["A.cs"], [[]]),
			TestContext.Current.CancellationToken);

		Assert.False(result.Available);
		Assert.Empty(result.Paths);
	}

	[Fact]
	public void PersonalizedPageRankKeepsASupportedIsolatedSeedAvailable()
	{
		var report = Ranking("B.cs", "A.cs");
		var result = EvaluationFocusComparators.PersonalizedPageRank(
			report,
			[new FocusRankingSeedRequest("A.cs", report.Entries[1].FullPath)],
			Graph(["A.cs", "B.cs"], [[], []]),
			TestContext.Current.CancellationToken);

		Assert.True(result.Available);
		Assert.Equal("A.cs", result.Paths[0]);
	}

	[Fact]
	public void SeedOnlyTaskReportsRecallNewAsNotApplicable()
	{
		var result = EvaluationMetrics.Calculate(
			[["seed.cs"]],
			["seed.cs"],
			new HashSet<string>(["seed.cs"], StringComparer.Ordinal),
			new HashSet<string>(["seed.cs"], StringComparer.Ordinal),
			new Dictionary<string, long> { ["seed.cs"] = 10 },
			budget: 20,
			remainingAfterSeeds: 10);

		Assert.Null(result.RecallNew);
		Assert.True(result.AllRequired);
	}

	[Fact]
	public void BestSetUsesHighestRecallAndPrimaryBreaksATie()
	{
		var result = EvaluationMetrics.Calculate(
			[["seed.cs", "primary.cs"], ["seed.cs", "alternative.cs"]],
			["seed.cs"],
			new HashSet<string>(["seed.cs"], StringComparer.Ordinal),
			new HashSet<string>(["seed.cs"], StringComparer.Ordinal),
			Costs(("seed.cs", 10), ("primary.cs", 10), ("alternative.cs", 10)),
			budget: 20,
			remainingAfterSeeds: 10);

		Assert.Equal(["seed.cs", "primary.cs"], result.BestSufficientSet);
		Assert.Equal(0, result.RecallNew);
	}

	[Fact]
	public void OracleAfterSeedsUsesActualSeedAdmissionAndRemainingBudget()
	{
		var characters = new Dictionary<string, int>
		{
			["oversized.cs"] = 100,
			["small.cs"] = 20,
			["required.cs"] = 20
		};
		var seedPass = GreedyAdmission.Run(["oversized.cs", "small.cs"], characters, budget: 10);
		var result = EvaluationMetrics.Calculate(
			[["small.cs", "required.cs"]],
			["oversized.cs", "small.cs"],
			seedPass.Admitted,
			seedPass.Admitted,
			Costs(("oversized.cs", 25), ("small.cs", 5), ("required.cs", 5)),
			budget: 10,
			remainingAfterSeeds: seedPass.RemainingTokens);

		Assert.DoesNotContain("oversized.cs", seedPass.Admitted);
		Assert.Contains("small.cs", seedPass.Admitted);
		Assert.True(result.OracleAfterSeeds);
	}

	[Fact]
	public void MetricCalculationIsDeterministicAcrossSetImplementations()
	{
		var costs = Costs(("seed.cs", 1), ("a.cs", 2), ("b.cs", 3), ("noise.cs", 5));
		var first = EvaluationMetrics.Calculate(
			[["seed.cs", "a.cs", "b.cs"]],
			["seed.cs"],
			new HashSet<string>(["noise.cs", "a.cs", "seed.cs"], StringComparer.Ordinal),
			new HashSet<string>(["seed.cs"], StringComparer.Ordinal),
			costs,
			10,
			9);
		var second = EvaluationMetrics.Calculate(
			[["seed.cs", "a.cs", "b.cs"]],
			["seed.cs"],
			new SortedSet<string>(["seed.cs", "a.cs", "noise.cs"], StringComparer.Ordinal),
			new SortedSet<string>(["seed.cs"], StringComparer.Ordinal),
			costs,
			10,
			9);

		Assert.Equal(first.RecallNew, second.RecallNew);
		Assert.Equal(first.AllRequired, second.AllRequired);
		Assert.Equal(first.IrrelevantTokenShare, second.IrrelevantTokenShare);
		Assert.Equal(first.Oracle, second.Oracle);
		Assert.Equal(first.OracleAfterSeeds, second.OracleAfterSeeds);
		Assert.Equal(first.BestSufficientSet, second.BestSufficientSet);
	}

	private static IReadOnlyDictionary<string, long> Costs(params (string Path, long Tokens)[] values) =>
		values.ToDictionary(static value => value.Path, static value => value.Tokens, StringComparer.Ordinal);

	private static ImportanceRankingReport Ranking(params string[] paths)
	{
		var root = Path.GetFullPath(Path.Combine("ranking-eval-tests", Guid.NewGuid().ToString("N")));
		var entries = paths.Select((path, index) => new ImportanceRankingEntry(
			Path.Combine(root, path),
			path,
			index + 1,
			1d - index * 0.1,
			0,
			0,
			null,
			null,
			ImportanceFileRole.Source,
			true,
			false)).ToArray();
		return new ImportanceRankingReport(
			ImportanceRankingService.AlgorithmId,
			entries,
			entries,
			entries.Length,
			entries.Length,
			0,
			1,
			200,
			0,
			ProjectGitHistoryUnavailableReason.NotRepository,
			false,
			ImportanceRankingService.GraphVariant);
	}

	private static EvaluationRankingGraph Graph(string[] paths, int[][] undirected) =>
		new(
			paths,
			paths.Select((path, index) => (path, index)).ToDictionary(
				static item => item.path,
				static item => item.index,
				StringComparer.Ordinal),
			undirected);
}
