using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using DevProjex.Terminal.Execution;

namespace DevProjex.Terminal.CommandLine;

public sealed class TerminalApplication(
	ITerminalEnvironment environment,
	TerminalServiceFactory? serviceFactory = null,
	IDeveloperCommandRunner? developerCommandRunner = null)
{
	public async Task<int> RunAsync(
		IReadOnlyList<string> arguments,
		CancellationToken cancellationToken = default)
	{
		var localization = new LocalizationService(
			new JsonLocalizationCatalog(),
			TerminalLanguageResolver.Resolve(arguments));
		if (LegacyCliSyntaxDetector.TryDetect(arguments, out var migration))
		{
			environment.Error.WriteLine("error[DPX-CLI-LEGACY-SYNTAX]:");
			environment.Error.WriteLine(localization["Terminal.Error.LegacySyntax"]);
			environment.Error.WriteLine(localization["Terminal.Label.NewCommand"]);
			environment.Error.WriteLine($"  {migration.Replacement}");
			return CommandLineExitCodes.UsageError;
		}
		if (ContainsMalformedVersionAlias(arguments))
		{
			environment.Error.WriteLine(
				$"error[DPX-CLI-UNKNOWN-OPTION]: " +
				localization.Format("Terminal.Error.UnknownOption", "-version"));
			environment.Error.WriteLine(
				localization.Format(
					"Terminal.Hint.DidYouMean",
					"devprojex --version"));
			return CommandLineExitCodes.UsageError;
		}

		var implicitTuiInvocation = IsImplicitTuiInvocation(arguments) &&
		                            environment.IsInputInteractive &&
		                            environment.IsOutputInteractive &&
		                            !environment.IsTermDumb;
		var root = new DevProjexCommandTree(
			environment,
			serviceFactory ?? CreateDefaultServiceFactory(),
			developerCommandRunner,
			implicitTuiInvocation,
			localization).Build();
		if (implicitTuiInvocation)
		{
			arguments = ["tui", .. arguments];
		}
		else if (arguments.Count == 0)
		{
			new CommandHelpRenderer(environment, localization).Write(root);
			return CommandLineExitCodes.Success;
		}

		if (ContainsHelpTokenBeforeDelimiter(arguments))
		{
			var (command, path) = ResolveHelpTarget(root, arguments);
			new CommandHelpRenderer(environment, localization).Write(command, path);
			return CommandLineExitCodes.Success;
		}

		var parseResult = root.Parse(arguments.ToArray());
		if (parseResult.Errors.Count > 0)
		{
			var errors = PresentParseErrors(root, arguments, parseResult.Errors, localization);
			foreach (var error in errors)
				environment.Error.WriteLine($"error[{error.Code}]: {error.Message}");
			if (TryBuildSuggestion(root, arguments, out var suggestion))
				environment.Error.WriteLine(localization.Format("Terminal.Hint.DidYouMean", suggestion));
			else
				environment.Error.WriteLine(localization["Terminal.Hint.Help"]);
			return CommandLineExitCodes.UsageError;
		}

		var configuration = new InvocationConfiguration
		{
			Output = environment.Output,
			Error = environment.Error,
			EnableDefaultExceptionHandler = false
		};
		try
		{
			return await parseResult.InvokeAsync(configuration, cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			environment.Error.WriteLine(
				$"error[DPX-CLI-CANCELED]: {localization["Terminal.Error.Canceled"]}");
			return CommandLineExitCodes.Canceled;
		}
		catch (Exception exception)
		{
			environment.Error.WriteLine(
				$"error[DPX-CLI-UNEXPECTED]: {localization["Terminal.Error.Unexpected"]}");
			if (arguments.Contains("--verbosity", StringComparer.Ordinal) &&
			    arguments.Contains("diagnostic", StringComparer.OrdinalIgnoreCase))
			{
				environment.Error.WriteLine(
					$"{localization["Terminal.Label.Exception"]}: {exception.GetType().FullName}");
				environment.Error.WriteLine(exception.StackTrace);
			}
			return CommandLineExitCodes.RuntimeError;
		}
	}

	private static IReadOnlyList<PresentedParseError> PresentParseErrors(
		RootCommand root,
		IReadOnlyList<string> arguments,
		IReadOnlyList<ParseError> parseErrors,
		LocalizationService localization)
	{
		if (TryFindUnknownOption(root, arguments, out var unknownOption))
		{
			return
			[
				new PresentedParseError(
					"DPX-CLI-UNKNOWN-OPTION",
					localization.Format("Terminal.Error.UnknownOption", unknownOption))
			];
		}

		if (TryFindUnknownCommand(root, arguments, out var unknownCommand))
		{
			return
			[
				new PresentedParseError(
					"DPX-CLI-UNKNOWN-COMMAND",
					localization.Format("Terminal.Error.UnknownCommand", unknownCommand))
			];
		}

		var presented = new List<PresentedParseError>();
		foreach (var error in parseErrors)
		{
			PresentedParseError item;
			if (IsMissingOptionValue(error) &&
			    TryResolveMissingValueOption(error, arguments, out var optionName))
			{
				item = new PresentedParseError(
					"DPX-CLI-MISSING-VALUE",
					localization.Format(
						"Terminal.Error.MissingValue",
						optionName));
			}
			else
			{
				var localized = LocalizedParseError.Resolve(error.Message, localization);
				item = new PresentedParseError(
					error.Message.StartsWith(LocalizedParseError.Prefix, StringComparison.Ordinal)
						? "DPX-CLI-INVALID-VALUE"
						: "DPX-CLI-INVALID-SYNTAX",
					localization.Format("Terminal.Error.InvalidSyntax", localized));
			}

			if (!presented.Contains(item))
				presented.Add(item);
		}

		return presented;
	}

	private static bool ContainsMalformedVersionAlias(
		IReadOnlyList<string> arguments) =>
		arguments
			.TakeWhile(static token => token != "--")
			.Contains("-version", StringComparer.Ordinal);

	private static bool IsMissingOptionValue(ParseError error) =>
		error.SymbolResult is OptionResult optionResult &&
		optionResult.Option.Arity.MinimumNumberOfValues > 0 &&
		optionResult.Tokens.Count == 0;

	private static bool TryResolveMissingValueOption(
		ParseError error,
		IReadOnlyList<string> arguments,
		out string optionName)
	{
		if (error.SymbolResult is OptionResult optionResult)
		{
			optionName = optionResult.Option.Name;
			return true;
		}

		optionName = arguments
			.TakeWhile(static value => value != "--")
			.LastOrDefault(static value => value.StartsWith("--", StringComparison.Ordinal))
			?.Split('=', 2)[0] ?? string.Empty;
		return optionName.Length > 0;
	}

	private static bool TryFindUnknownOption(
		RootCommand root,
		IReadOnlyList<string> arguments,
		out string option)
	{
		option = string.Empty;
		var current = ResolveCommand(root, arguments);
		var knownOptions = root.Options
			.Concat(current.Options)
			.SelectMany(static known => new[] { known.Name }.Concat(known.Aliases))
			.ToHashSet(StringComparer.Ordinal);
		foreach (var token in arguments.TakeWhile(static token => token != "--"))
		{
			if (token == "-" || !token.StartsWith("-", StringComparison.Ordinal))
				continue;
			var candidate = token.Split('=', 2)[0];
			if (knownOptions.Contains(candidate))
				continue;
			option = candidate;
			return true;
		}
		return false;
	}

	private static bool TryFindUnknownCommand(
		RootCommand root,
		IReadOnlyList<string> arguments,
		out string command)
	{
		command = string.Empty;
		Command current = root;
		foreach (var token in arguments.TakeWhile(static token => token != "--"))
		{
			if (token.StartsWith("-", StringComparison.Ordinal))
				break;
			var child = current.Subcommands.FirstOrDefault(candidate =>
				!candidate.Hidden &&
				candidate.Name.Equals(token, StringComparison.Ordinal));
			if (child is not null)
			{
				current = child;
				continue;
			}
			if (current.Subcommands.Any(static candidate => !candidate.Hidden))
			{
				command = token;
				return true;
			}
			break;
		}
		return false;
	}

	private static Command ResolveCommand(RootCommand root, IReadOnlyList<string> arguments)
	{
		Command current = root;
		foreach (var token in arguments.TakeWhile(static token => token != "--"))
		{
			if (token.StartsWith("-", StringComparison.Ordinal))
				break;
			var child = current.Subcommands.FirstOrDefault(candidate =>
				!candidate.Hidden &&
				candidate.Name.Equals(token, StringComparison.Ordinal));
			if (child is null)
				break;
			current = child;
		}
		return current;
	}

	private static bool IsHelpToken(string value) =>
		value is "--help" or "-h" or "-?" or "/h" or "/?";

	private TerminalServiceFactory CreateDefaultServiceFactory()
	{
		if (!environment.Variables.TryGetValue(
			    InvocationEnvironment.InternalDataRootVariable,
			    out var value) ||
		    string.IsNullOrWhiteSpace(value) ||
		    !Path.IsPathFullyQualified(value))
		{
			return new TerminalServiceFactory();
		}

		var dataRoot = Path.GetFullPath(value);
		return new TerminalServiceFactory(() => dataRoot);
	}

	private static bool IsImplicitTuiInvocation(IReadOnlyList<string> arguments)
	{
		if (arguments.Count == 0)
			return true;
		if (arguments.Count == 1)
			return arguments[0].StartsWith("--language=", StringComparison.Ordinal);
		return arguments.Count == 2 &&
		       arguments[0] == "--language" &&
		       !string.IsNullOrWhiteSpace(arguments[1]);
	}

	private static bool ContainsHelpTokenBeforeDelimiter(IReadOnlyList<string> arguments)
	{
		foreach (var argument in arguments)
		{
			if (argument == "--")
				return false;
			if (IsHelpToken(argument))
				return true;
		}

		return false;
	}

	private static (Command Command, IReadOnlyList<string> Path) ResolveHelpTarget(
		RootCommand root,
		IReadOnlyList<string> arguments)
	{
		Command current = root;
		var path = new List<string> { "devprojex" };
		foreach (var token in arguments)
		{
			if (IsHelpToken(token))
				break;
			var child = current.Subcommands.FirstOrDefault(command =>
				!command.Hidden &&
				command.Name.Equals(token, StringComparison.Ordinal));
			if (child is null)
				continue;
			current = child;
			path.Add(child.Name);
		}
		return (current, path);
	}

	private static bool TryBuildSuggestion(
		RootCommand root,
		IReadOnlyList<string> arguments,
		out string suggestion)
	{
		suggestion = string.Empty;
		var first = arguments.FirstOrDefault(static value => value != "--");
		if (string.IsNullOrWhiteSpace(first))
			return false;

		var commandPath = new List<string>();
		Command current = root;
		string? unmatchedCommand = null;
		foreach (var token in arguments)
		{
			if (token == "--" || token.StartsWith('-'))
				break;
			var child = current.Subcommands.FirstOrDefault(command =>
				!command.Hidden &&
				command.Name.Equals(token, StringComparison.Ordinal));
			if (child is null)
			{
				unmatchedCommand = token;
				break;
			}
			current = child;
			commandPath.Add(child.Name);
		}

		if (commandPath.Count == 0 && !first.StartsWith('-'))
		{
			var command = FindClosest(
				first,
				root.Subcommands
					.Where(static item => !item.Hidden)
					.Select(static item => item.Name));
			if (command is null)
				return false;
			suggestion = $"devprojex {command}";
			return true;
		}

		if (unmatchedCommand is not null && current.Subcommands.Any(static command => !command.Hidden))
		{
			var child = FindClosest(
				unmatchedCommand,
				current.Subcommands
					.Where(static command => !command.Hidden)
					.Select(static command => command.Name));
			if (child is not null)
			{
				var prefix = commandPath.Count == 0
					? "devprojex"
					: $"devprojex {string.Join(' ', commandPath)}";
				suggestion = $"{prefix} {child}";
				return true;
			}
		}

		var knownOptions = root.Options
			.Concat(current.Options)
			.Where(static option => !option.Hidden)
			.SelectMany(static option => new[] { option.Name }.Concat(option.Aliases))
			.Distinct(StringComparer.Ordinal)
			.ToArray();
		foreach (var token in arguments.TakeWhile(static token => token != "--"))
		{
			if (!token.StartsWith('-'))
				continue;
			var optionToken = token.Split('=', 2)[0];
			if (knownOptions.Contains(optionToken, StringComparer.Ordinal))
				continue;
			var option = FindClosest(optionToken, knownOptions);
			if (option is null)
				continue;
			var prefix = commandPath.Count == 0
				? "devprojex"
				: $"devprojex {string.Join(' ', commandPath)}";
			suggestion = $"{prefix} {option}";
			return true;
		}

		return false;
	}

	private static string? FindClosest(string value, IEnumerable<string> candidates)
	{
		var best = candidates
			.Distinct(StringComparer.Ordinal)
			.Select(candidate => new
			{
				Candidate = candidate,
				Distance = EditDistance(value, candidate)
			})
			.OrderBy(static item => item.Distance)
			.ThenBy(static item => item.Candidate, StringComparer.Ordinal)
			.FirstOrDefault();
		var maximumDistance = Math.Max(1, value.Length / 3);
		return best is not null && best.Distance <= maximumDistance
			? best.Candidate
			: null;
	}

	private static int EditDistance(string left, string right)
	{
		var previous = new int[right.Length + 1];
		var current = new int[right.Length + 1];
		for (var column = 0; column <= right.Length; column++)
			previous[column] = column;

		for (var row = 1; row <= left.Length; row++)
		{
			current[0] = row;
			for (var column = 1; column <= right.Length; column++)
			{
				var substitution = previous[column - 1] +
				                   (char.ToLowerInvariant(left[row - 1]) ==
				                    char.ToLowerInvariant(right[column - 1])
					                   ? 0
					                   : 1);
				current[column] = Math.Min(
					Math.Min(previous[column] + 1, current[column - 1] + 1),
					substitution);
			}

			(previous, current) = (current, previous);
		}

		return previous[right.Length];
	}

	private sealed record PresentedParseError(string Code, string Message);
}
