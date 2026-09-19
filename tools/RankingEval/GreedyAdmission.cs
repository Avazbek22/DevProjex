using DevProjex.Application.Context;

namespace DevProjex.RankingEval;

public sealed record GreedyAdmissionResult(
	IReadOnlySet<string> Admitted,
	long RemainingTokens);

public static class GreedyAdmission
{
	public static GreedyAdmissionResult Run(
		IReadOnlyList<string> orderedPaths,
		IReadOnlyDictionary<string, int> transformedCharacterCounts,
		long budget)
	{
		var accumulator = new ProjectContextTokenBudgetAccumulator(budget);
		var admitted = new HashSet<string>(StringComparer.Ordinal);
		var used = 0L;
		for (var index = 0; index < orderedPaths.Count; index++)
		{
			var path = orderedPaths[index];
			var characters = transformedCharacterCounts.GetValueOrDefault(path);
			var included = accumulator.TryInclude(path, characters, index + 1);
			if (!included)
				continue;
			admitted.Add(path);
			used += (characters + 3L) / 4L;
		}
		return new GreedyAdmissionResult(admitted, budget - used);
	}
}
