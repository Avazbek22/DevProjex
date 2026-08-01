using System.CommandLine;
using System.CommandLine.Parsing;

namespace DevProjex.Terminal.CommandLine;

internal static class CliParseValue
{
	public static bool TryGet<T>(
		ParseResult parseResult,
		Option<T> option,
		out T value) =>
		TryGet(parseResult.GetResult(option), out value);

	public static bool TryGet<T>(
		CommandResult commandResult,
		Option<T> option,
		out T value) =>
		TryGet(commandResult.GetResult(option), out value);

	private static bool TryGet<T>(OptionResult? result, out T value)
	{
		value = default!;
		if (result is null ||
		    result.Errors.Any() ||
		    (!result.Implicit &&
		     result.Option?.Arity.MinimumNumberOfValues > 0 &&
		     result.Tokens.Count == 0))
		{
			return false;
		}

		try
		{
			value = result.GetValueOrDefault<T>()!;
			return true;
		}
		catch (InvalidOperationException)
		{
			value = default!;
			return false;
		}
	}
}
