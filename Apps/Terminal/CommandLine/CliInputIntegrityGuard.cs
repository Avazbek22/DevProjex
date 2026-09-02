using System.CommandLine;

namespace DevProjex.Terminal.CommandLine;

internal static class CliInputIntegrityGuard
{
	// System.CommandLine 2.0.10 drops an empty inline value and can bind the next argv token.
	// This guard validates only that lexical boundary; command and option resolution stay model-owned.
	public static bool TryFindError(
		IReadOnlyList<string> arguments,
		RootCommand root,
		ParseResult parseResult,
		out CliInputIntegrityError error)
	{
		ArgumentNullException.ThrowIfNull(arguments);
		ArgumentNullException.ThrowIfNull(root);
		ArgumentNullException.ThrowIfNull(parseResult);

		var applicableOptions = GetApplicableOptions(
				root,
				parseResult.CommandResult.Command)
			.SelectMany(static option =>
				new[] { option.Name }
					.Concat(option.Aliases)
					.Select(identifier => (Identifier: identifier, Option: option)))
			.GroupBy(static item => item.Identifier, StringComparer.Ordinal)
			.ToDictionary(
				static group => group.Key,
				static group => group.First().Option,
				StringComparer.Ordinal);

		for (var index = 0; index < arguments.Count; index++)
		{
			var argument = arguments[index];
			if (argument == "--")
				break;
			if (!applicableOptions.TryGetValue(argument, out var option) ||
			    option.Arity.MinimumNumberOfValues == 0)
			{
				continue;
			}

			if (index + 1 >= arguments.Count ||
			    arguments[index + 1] == "--" ||
			    applicableOptions.ContainsKey(arguments[index + 1]))
			{
				error = new CliInputIntegrityError(
					CliInputIntegrityErrorKind.MissingOptionValue,
					option.Name);
				return true;
			}
		}

		foreach (var argument in arguments)
		{
			if (argument == "--")
				break;
			if (argument.Length < 2 || argument[^1] != '=')
				continue;

			var identifier = argument[..^1];
			if (!applicableOptions.TryGetValue(identifier, out var option))
				continue;

			error = new CliInputIntegrityError(
				option.Arity.MinimumNumberOfValues > 0
					? CliInputIntegrityErrorKind.MissingOptionValue
					: CliInputIntegrityErrorKind.UnexpectedFlagValue,
				option.Name);
			return true;
		}

		foreach (var option in applicableOptions.Values.Distinct())
		{
			if (option.Arity.MinimumNumberOfValues == 0)
				continue;
			var result = parseResult.GetResult(option);
			if (result?.Tokens.Any(static token => token.Value.Length == 0) != true)
				continue;

			error = new CliInputIntegrityError(
				CliInputIntegrityErrorKind.MissingOptionValue,
				option.Name);
			return true;
		}

		var command = parseResult.CommandResult.Command;
		if (!command.Hidden)
		{
			foreach (var argument in command.Arguments)
			{
				var result = parseResult.GetResult(argument);
				if (result?.Tokens.Any(static token => token.Value.Length == 0) != true)
					continue;

				error = new CliInputIntegrityError(
					CliInputIntegrityErrorKind.EmptyArgument,
					argument.Name);
				return true;
			}
		}

		error = default;
		return false;
	}

	private static IEnumerable<Option> GetApplicableOptions(
		RootCommand root,
		Command target)
	{
		var path = new List<Command>();
		if (!TryFindCommandPath(root, target, path))
			throw new InvalidOperationException(
				$"Command is not part of the root tree: {target.Name}");

		for (var index = 0; index < path.Count; index++)
		{
			var isTarget = index == path.Count - 1;
			foreach (var option in path[index].Options)
			{
				if (isTarget || option.Recursive)
					yield return option;
			}
		}
	}

	private static bool TryFindCommandPath(
		Command current,
		Command target,
		ICollection<Command> path)
	{
		path.Add(current);
		if (ReferenceEquals(current, target))
			return true;

		foreach (var child in current.Subcommands)
		{
			if (TryFindCommandPath(child, target, path))
				return true;
		}

		path.Remove(current);
		return false;
	}
}

internal enum CliInputIntegrityErrorKind
{
	MissingOptionValue,
	UnexpectedFlagValue,
	EmptyArgument
}

internal readonly record struct CliInputIntegrityError(
	CliInputIntegrityErrorKind Kind,
	string SymbolName);
