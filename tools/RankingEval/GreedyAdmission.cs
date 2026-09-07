using DevProjex.Application.Context;
using System.Reflection;

namespace DevProjex.RankingEval;

public sealed record GreedyAdmissionResult(
	IReadOnlySet<string> Admitted,
	long RemainingTokens);

public static class GreedyAdmission
{
	private static readonly Type AccumulatorType =
		typeof(ProjectContextTokenBudgetReport).Assembly.GetType(
			"DevProjex.Application.Context.ProjectContextTokenBudgetAccumulator",
			throwOnError: true)!;
	private static readonly ConstructorInfo Constructor = AccumulatorType.GetConstructor(
		BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
		binder: null,
		[typeof(long)],
		modifiers: null)!;
	private static readonly MethodInfo TryInclude = AccumulatorType.GetMethod(
		"TryInclude",
		BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;

	public static GreedyAdmissionResult Run(
		IReadOnlyList<string> orderedPaths,
		IReadOnlyDictionary<string, int> transformedCharacterCounts,
		long budget)
	{
		var accumulator = Constructor.Invoke([budget]);
		var admitted = new HashSet<string>(StringComparer.Ordinal);
		var used = 0L;
		for (var index = 0; index < orderedPaths.Count; index++)
		{
			var path = orderedPaths[index];
			var characters = transformedCharacterCounts.GetValueOrDefault(path);
			var included = (bool)TryInclude.Invoke(accumulator, [path, characters, index + 1])!;
			if (!included)
				continue;
			admitted.Add(path);
			used += (characters + 3L) / 4L;
		}
		return new GreedyAdmissionResult(admitted, budget - used);
	}
}
