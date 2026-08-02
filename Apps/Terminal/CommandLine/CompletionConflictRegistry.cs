using System.CommandLine;
using System.Runtime.CompilerServices;

namespace DevProjex.Terminal.CommandLine;

internal static class CompletionConflictRegistry
{
	private static readonly ConditionalWeakTable<Option, HashSet<Option>> Conflicts = new();

	public static void RegisterMutual(Option left, Option right)
	{
		ArgumentNullException.ThrowIfNull(left);
		ArgumentNullException.ThrowIfNull(right);
		Conflicts.GetOrCreateValue(left).Add(right);
		Conflicts.GetOrCreateValue(right).Add(left);
	}

	public static bool HasExplicitConflict(Option option, ParseResult parseResult)
	{
		ArgumentNullException.ThrowIfNull(option);
		ArgumentNullException.ThrowIfNull(parseResult);
		return Conflicts.TryGetValue(option, out var conflicts) &&
		       conflicts.Any(conflict =>
			       parseResult.GetResult(conflict) is { Implicit: false });
	}
}
