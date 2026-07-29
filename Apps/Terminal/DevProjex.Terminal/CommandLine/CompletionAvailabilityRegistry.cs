using System.CommandLine;
using System.Runtime.CompilerServices;

namespace DevProjex.Terminal.CommandLine;

internal static class CompletionAvailabilityRegistry
{
	private static readonly ConditionalWeakTable<Option, Rules> RulesByOption = new();

	public static void RegisterOption(
		Option option,
		Func<ParseResult, bool> isAvailable)
	{
		ArgumentNullException.ThrowIfNull(option);
		ArgumentNullException.ThrowIfNull(isAvailable);
		RulesByOption.GetOrCreateValue(option).OptionRules.Add(isAvailable);
	}

	public static void RegisterValue(
		Option option,
		Func<ParseResult, string, bool> isAvailable)
	{
		ArgumentNullException.ThrowIfNull(option);
		ArgumentNullException.ThrowIfNull(isAvailable);
		RulesByOption.GetOrCreateValue(option).ValueRules.Add(isAvailable);
	}

	public static bool IsOptionAvailable(Option option, ParseResult parseResult) =>
		!RulesByOption.TryGetValue(option, out var rules) ||
		rules.OptionRules.All(rule => rule(parseResult));

	public static bool IsValueAvailable(
		Option option,
		ParseResult parseResult,
		string value) =>
		!RulesByOption.TryGetValue(option, out var rules) ||
		rules.ValueRules.All(rule => rule(parseResult, value));

	private sealed class Rules
	{
		public List<Func<ParseResult, bool>> OptionRules { get; } = [];
		public List<Func<ParseResult, string, bool>> ValueRules { get; } = [];
	}
}
