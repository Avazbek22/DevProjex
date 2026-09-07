using Xunit;

namespace DevProjex.RankingEval.Tests;

public sealed class EvaluationMetricsTests
{
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
}
