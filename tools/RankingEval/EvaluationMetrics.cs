namespace DevProjex.RankingEval;

public sealed record EvaluationMetricResult(
	double? RecallNew,
	bool AllRequired,
	double IrrelevantTokenShare,
	bool Oracle,
	bool OracleAfterSeeds,
	IReadOnlyList<string> BestSufficientSet);

public static class EvaluationMetrics
{
	public static EvaluationMetricResult Calculate(
		IReadOnlyList<IReadOnlyList<string>> sufficientSets,
		IReadOnlyList<string> seeds,
		IReadOnlySet<string> admitted,
		IReadOnlySet<string> admittedSeeds,
		IReadOnlyDictionary<string, long> tokenCosts,
		long budget,
		long remainingAfterSeeds)
	{
		ArgumentNullException.ThrowIfNull(sufficientSets);
		if (sufficientSets.Count == 0)
			throw new ArgumentException("At least one sufficient set is required.", nameof(sufficientSets));
		var comparer = StringComparer.Ordinal;
		var seedSet = seeds.ToHashSet(comparer);
		var bestIndex = 0;
		var bestFraction = -1d;
		for (var index = 0; index < sufficientSets.Count; index++)
		{
			var set = sufficientSets[index];
			var fraction = set.Count == 0 ? 1 : (double)set.Count(admitted.Contains) / set.Count;
			if (fraction > bestFraction)
			{
				bestFraction = fraction;
				bestIndex = index;
			}
		}

		var best = sufficientSets[bestIndex];
		var newFiles = best.Where(path => !seedSet.Contains(path)).ToArray();
		var recallNew = newFiles.Length == 0
			? (double?)null
			: (double)newFiles.Count(admitted.Contains) / newFiles.Length;
		var allRequired = sufficientSets.Any(set => set.All(admitted.Contains));
		var admittedTokenCount = admitted.Sum(path => tokenCosts.GetValueOrDefault(path));
		var relevantTokenCount = best.Where(admitted.Contains).Sum(path => tokenCosts.GetValueOrDefault(path));
		var irrelevant = admittedTokenCount == 0
			? 0
			: Math.Clamp((double)(admittedTokenCount - relevantTokenCount) / admittedTokenCount, 0, 1);
		var oracle = sufficientSets.Any(set => set.Sum(path => tokenCosts.GetValueOrDefault(path)) <= budget);
		var oracleAfterSeeds = sufficientSets.Any(set =>
		{
			if (set.Any(path => seedSet.Contains(path) && !admittedSeeds.Contains(path)))
				return false;
			var remainingRequired = set
				.Where(path => !admittedSeeds.Contains(path))
				.Sum(path => tokenCosts.GetValueOrDefault(path));
			return remainingRequired <= remainingAfterSeeds;
		});
		return new EvaluationMetricResult(
			recallNew,
			allRequired,
			irrelevant,
			oracle,
			oracleAfterSeeds,
			best);
	}
}
