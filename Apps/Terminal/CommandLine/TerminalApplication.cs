using System.CommandLine;
using System.CommandLine.Help;
using System.CommandLine.Parsing;
using DevProjex.Terminal.Execution;
using DevProjex.Terminal.Rendering;
using DevProjex.Terminal.Tui;

namespace DevProjex.Terminal.CommandLine;

public sealed class TerminalApplication
{
	private readonly ITerminalEnvironment environment;
	private readonly TerminalServiceFactory? serviceFactory;
	private readonly IDeveloperCommandRunner? developerCommandRunner;
	private readonly ITerminalOperationObserver operationObserver;

	public TerminalApplication(
		ITerminalEnvironment environment,
		TerminalServiceFactory? serviceFactory = null,
		IDeveloperCommandRunner? developerCommandRunner = null)
		: this(
			environment,
			serviceFactory,
			developerCommandRunner,
			NullTerminalOperationObserver.Instance)
	{
	}

	internal TerminalApplication(
		ITerminalEnvironment environment,
		TerminalServiceFactory? serviceFactory,
		IDeveloperCommandRunner? developerCommandRunner,
		ITerminalOperationObserver operationObserver)
	{
		this.environment = environment ??
			throw new ArgumentNullException(nameof(environment));
		this.serviceFactory = serviceFactory;
		this.developerCommandRunner = developerCommandRunner;
		this.operationObserver = operationObserver ??
			throw new ArgumentNullException(nameof(operationObserver));
	}

	public async Task<int> RunAsync(
		IReadOnlyList<string> arguments,
		CancellationToken cancellationToken = default)
	{
		try
		{
			return await RunCoreAsync(arguments, cancellationToken).ConfigureAwait(false);
		}
		catch (TerminalBrokenPipeException)
		{
			return CommandLineExitCodes.Success;
		}
	}

	private async Task<int> RunCoreAsync(
		IReadOnlyList<string> arguments,
		CancellationToken cancellationToken)
	{
		var runtimeEnvironment = new DiagnosticPipeSafeTerminalEnvironment(environment);
		var localization = new LocalizationService(
			new JsonLocalizationCatalog(),
			TerminalLanguageResolver.Resolve(arguments, environment.Variables));
		if (LegacyCliSyntaxDetector.TryDetect(arguments, out var migration))
		{
			runtimeEnvironment.Error.WriteLine(TerminalErrorHeader.Format(localization, "DPX-CLI-LEGACY-SYNTAX"));
			runtimeEnvironment.Error.WriteLine(localization["Terminal.Error.LegacySyntax"]);
			runtimeEnvironment.Error.WriteLine(localization["Terminal.Label.NewCommand"]);
			foreach (var line in CliArgumentVectorFormatter
				         .Format(migration.ReplacementArguments)
				         .Split(Environment.NewLine))
			{
				runtimeEnvironment.Error.WriteLine($"  {line}");
			}
			return CommandLineExitCodes.UsageError;
		}
		var implicitTuiInvocation = IsImplicitTuiInvocation(arguments) &&
			runtimeEnvironment.IsInputInteractive &&
			runtimeEnvironment.IsOutputInteractive &&
			!runtimeEnvironment.IsTermDumb;
		var root = new DevProjexCommandTree(
			runtimeEnvironment,
			serviceFactory ?? CreateDefaultServiceFactory(),
			developerCommandRunner,
			implicitTuiInvocation,
			localization,
			operationObserver).Build();
		if (implicitTuiInvocation)
		{
			arguments = ["tui", .. arguments];
		}
		else if (arguments.Count == 0)
		{
			return WriteHelp(localization, root);
		}

		var parseResult = root.Parse(
			arguments.ToArray(),
			new ParserConfiguration
			{
				EnablePosixBundling = false
			});
		if (CliInputIntegrityGuard.TryFindError(
			    arguments,
			    root,
			    parseResult,
			    out var inputIntegrityError))
		{
			var (code, messageKey) = inputIntegrityError.Kind switch
			{
				CliInputIntegrityErrorKind.MissingOptionValue => (
					"DPX-CLI-MISSING-VALUE",
					"Terminal.Error.MissingValue"),
				CliInputIntegrityErrorKind.UnexpectedFlagValue => (
					"DPX-CLI-INVALID-SYNTAX",
					"Terminal.Error.OptionDoesNotTakeValue"),
				CliInputIntegrityErrorKind.EmptyArgument => (
					"DPX-CLI-INVALID-SYNTAX",
					"Terminal.Error.EmptyArgument"),
				_ => throw new ArgumentOutOfRangeException()
			};
			runtimeEnvironment.Error.WriteLine(
				$"{TerminalErrorHeader.Format(localization, code)} " +
				localization.Format(
					messageKey,
					inputIntegrityError.SymbolName));
			runtimeEnvironment.Error.WriteLine(localization["Terminal.Hint.Help"]);
			return CommandLineExitCodes.UsageError;
		}
		if (parseResult.Errors.Count > 0 ||
		    parseResult.UnmatchedTokens.Count > 0 ||
		    GetOptionLikeProjectTokens(parseResult).Count > 0)
		{
			var errors = PresentParseErrors(parseResult, localization);
			foreach (var error in errors)
			{
				runtimeEnvironment.Error.WriteLine(
					$"{TerminalErrorHeader.Format(localization, error.Code)} " +
					TerminalTextEscaping.EscapeSingleLine(error.Message));
			}
			if (TryBuildSuggestion(root, parseResult, out var suggestion))
				runtimeEnvironment.Error.WriteLine(localization.Format("Terminal.Hint.DidYouMean", suggestion));
			else
				runtimeEnvironment.Error.WriteLine(localization["Terminal.Hint.Help"]);
			return CommandLineExitCodes.UsageError;
		}
		if (parseResult.Action is HelpAction)
		{
			var command = parseResult.CommandResult.Command;
			return WriteHelp(
				localization,
				command,
				ResolveCommandPath(root, command));
		}

		var configuration = new InvocationConfiguration
		{
			Output = runtimeEnvironment.Output,
			Error = runtimeEnvironment.Error,
			EnableDefaultExceptionHandler = false
		};
		try
		{
			return await parseResult.InvokeAsync(configuration, cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			runtimeEnvironment.Error.WriteLine(
				$"{TerminalErrorHeader.Format(localization, "DPX-CLI-CANCELED")} {localization["Terminal.Error.Canceled"]}");
			return CommandLineExitCodes.Canceled;
		}
		catch (Exception exception) when (exception is not TerminalBrokenPipeException)
		{
			runtimeEnvironment.Error.WriteLine(
				$"{TerminalErrorHeader.Format(localization, "DPX-CLI-UNEXPECTED")} {localization["Terminal.Error.Unexpected"]}");
			if (IsDiagnosticVerbosity(parseResult))
			{
				runtimeEnvironment.Error.WriteLine(
					$"{localization["Terminal.Label.Exception"]}: {exception.GetType().FullName}");
				runtimeEnvironment.Error.WriteLine(exception.StackTrace);
			}
			return CommandLineExitCodes.RuntimeError;
		}
	}

	private int WriteHelp(
		LocalizationService localization,
		Command command,
		IReadOnlyList<string>? commandPath = null)
	{
		try
		{
			new CommandHelpRenderer(environment, localization).Write(command, commandPath);
		}
		catch (TerminalBrokenPipeException)
		{
			// A closed stdout consumer is a successful pipeline termination.
		}

		return CommandLineExitCodes.Success;
	}

	private static IReadOnlyList<PresentedParseError> PresentParseErrors(
		ParseResult parseResult,
		LocalizationService localization)
	{
		if (TryFindUnknownOption(parseResult, out var unknownOption))
		{
			return
			[
				new PresentedParseError(
					"DPX-CLI-UNKNOWN-OPTION",
					localization.Format("Terminal.Error.UnknownOption", unknownOption))
			];
		}

		if (TryFindUnknownCommand(parseResult, out var unknownCommand))
		{
			return
			[
				new PresentedParseError(
					"DPX-CLI-UNKNOWN-COMMAND",
					localization.Format("Terminal.Error.UnknownCommand", unknownCommand))
			];
		}

		var presented = new List<PresentedParseError>();
		var unexpectedArguments = new Queue<string>(parseResult.UnmatchedTokens);
		foreach (var error in parseResult.Errors)
		{
			PresentedParseError item;
			if (IsMissingOptionValue(error) &&
			    TryResolveMissingValueOption(error, out var optionName))
			{
				item = new PresentedParseError(
					"DPX-CLI-MISSING-VALUE",
					localization.Format(
						"Terminal.Error.MissingValue",
						optionName));
			}
			else if (error.Message.StartsWith(LocalizedParseError.Prefix, StringComparison.Ordinal))
			{
				var localized = LocalizedParseError.Resolve(error.Message, localization);
				var explicitCode = LocalizedParseError.ResolveCode(error.Message);
				item = explicitCode is not null
					? new PresentedParseError(explicitCode, localized)
					: new PresentedParseError(
						"DPX-CLI-INVALID-VALUE",
						localization.Format("Terminal.Error.InvalidSyntax", localized));
			}
			else
			{
				item = DescribeParserError(error, unexpectedArguments, localization);
			}

			if (!presented.Contains(item))
				presented.Add(item);
		}

		if (presented.Count == 0)
		{
			presented.Add(new PresentedParseError(
				"DPX-CLI-INVALID-SYNTAX",
				localization["Terminal.Error.ParserRejected"]));
		}

		return presented;
	}

	// System.CommandLine words its own errors in the process UI culture rather than the
	// selected language, so each known shape is described again with the symbol it concerns.
	private static PresentedParseError DescribeParserError(
		ParseError error,
		Queue<string> unexpectedArguments,
		LocalizationService localization)
	{
		switch (error.SymbolResult)
		{
			case ArgumentResult { Tokens.Count: 0, Parent: CommandResult } missing:
				return new PresentedParseError(
					"DPX-CLI-MISSING-VALUE",
					localization.Format("Terminal.Error.MissingArgument", missing.Argument.Name));
			case ArgumentResult { Tokens.Count: > 0 } argument:
				return new PresentedParseError(
					"DPX-CLI-INVALID-VALUE",
					localization.Format(
						"Terminal.Error.InvalidOptionValue",
						argument.Argument.Name,
						argument.Tokens[0].Value));
			case OptionResult option when option.Tokens.Count > option.Option.Arity.MaximumNumberOfValues:
				return new PresentedParseError(
					"DPX-CLI-INVALID-SYNTAX",
					localization.Format("Terminal.Error.OptionSingleValue", option.Option.Name));
			case OptionResult { Tokens.Count: > 0 } option:
				return new PresentedParseError(
					"DPX-CLI-INVALID-VALUE",
					localization.Format(
						"Terminal.Error.InvalidOptionValue",
						option.Option.Name,
						option.Tokens[0].Value));
			case CommandResult when unexpectedArguments.TryDequeue(out var argument):
				return new PresentedParseError(
					"DPX-CLI-INVALID-SYNTAX",
					localization.Format("Terminal.Error.UnexpectedArgument", argument));
			default:
				return new PresentedParseError(
					"DPX-CLI-INVALID-SYNTAX",
					localization["Terminal.Error.ParserRejected"]);
		}
	}

	private static bool IsMissingOptionValue(ParseError error) =>
		error.SymbolResult is OptionResult optionResult &&
		optionResult.Option.Arity.MinimumNumberOfValues > 0 &&
		optionResult.Tokens.Count == 0;

	private static bool TryResolveMissingValueOption(
		ParseError error,
		out string optionName)
	{
		if (error.SymbolResult is OptionResult optionResult)
		{
			optionName = optionResult.Option.Name;
			return true;
		}

		optionName = string.Empty;
		return false;
	}

	private static bool TryFindUnknownOption(
		ParseResult parseResult,
		out string option)
	{
		option = GetUnrecognizedTokensBeforeDelimiter(parseResult)
			.Select(static token => token.Value)
			.FirstOrDefault(static value =>
				value != "-" &&
				value.StartsWith("-", StringComparison.Ordinal))
			?.Split('=', 2)[0] ?? string.Empty;
		return option.Length > 0;
	}

	private static bool TryFindUnknownCommand(
		ParseResult parseResult,
		out string command)
	{
		command = string.Empty;
		var current = parseResult.CommandResult.Command;
		var candidate = GetUnmatchedTokensBeforeDelimiter(parseResult)
			.FirstOrDefault(static token =>
				!token.Value.StartsWith("-", StringComparison.Ordinal));
		if (candidate is null)
			return false;
		if (current.Subcommands.Any(static child => !child.Hidden) ||
		    AppearsBeforeResolvedCommand(parseResult, candidate))
		{
			command = candidate.Value;
			return true;
		}

		return false;
	}

	private static bool AppearsBeforeResolvedCommand(
		ParseResult parseResult,
		Token unmatchedToken)
	{
		var commandToken = parseResult.CommandResult.IdentifierToken;
		if (commandToken is null)
			return false;

		var unmatchedIndex = -1;
		var commandIndex = -1;
		for (var index = 0; index < parseResult.Tokens.Count; index++)
		{
			var token = parseResult.Tokens[index];
			if (unmatchedIndex < 0 &&
			    ReferenceEquals(token, unmatchedToken))
			{
				unmatchedIndex = index;
			}
			if (token.Type == TokenType.Command &&
			    token.Value.Equals(commandToken.Value, StringComparison.Ordinal))
			{
				commandIndex = index;
			}
		}

		return unmatchedIndex >= 0 &&
		       commandIndex >= 0 &&
		       unmatchedIndex < commandIndex;
	}

	private static IReadOnlyList<Token> GetUnmatchedTokensBeforeDelimiter(
		ParseResult parseResult)
	{
		var matchedTokens = new HashSet<Token>(ReferenceEqualityComparer.Instance);
		CollectMatchedTokens(parseResult.RootCommandResult, matchedTokens);
		var unmatchedCounts = parseResult.UnmatchedTokens
			.GroupBy(static value => value, StringComparer.Ordinal)
			.ToDictionary(
				static group => group.Key,
				static group => group.Count(),
				StringComparer.Ordinal);
		var result = new List<Token>();
		foreach (var token in parseResult.Tokens)
		{
			if (token.Type == TokenType.DoubleDash)
				break;
			if (token.Type != TokenType.Argument ||
			    matchedTokens.Contains(token) ||
			    !unmatchedCounts.TryGetValue(token.Value, out var remaining))
			{
				continue;
			}

			result.Add(token);
			if (remaining == 1)
				unmatchedCounts.Remove(token.Value);
			else
				unmatchedCounts[token.Value] = remaining - 1;
		}

		return result;
	}

	private static IReadOnlyList<Token> GetUnrecognizedTokensBeforeDelimiter(
		ParseResult parseResult)
	{
		var unmatched = GetUnmatchedTokensBeforeDelimiter(parseResult);
		var optionLikeProjects = GetOptionLikeProjectTokens(parseResult);
		if (optionLikeProjects.Count == 0)
			return unmatched;

		var unrecognized = new HashSet<Token>(
			unmatched.Concat(optionLikeProjects),
			ReferenceEqualityComparer.Instance);
		return parseResult.Tokens
			.Where(unrecognized.Contains)
			.ToArray();
	}

	// PROJECT is optional, so the parser binds an unknown option such as `tree --bogus` to it.
	// A dash-prefixed PROJECT before `--` is therefore reported as an unknown option; real
	// paths that start with a dash stay reachable after `--` or as `./-name`, and a lone `-`
	// remains a value.
	private static IReadOnlyList<Token> GetOptionLikeProjectTokens(ParseResult parseResult)
	{
		var delimiterIndex = -1;
		for (var index = 0; index < parseResult.Tokens.Count; index++)
		{
			if (parseResult.Tokens[index].Type == TokenType.DoubleDash)
			{
				delimiterIndex = index;
				break;
			}
		}

		var result = new List<Token>();
		foreach (var child in parseResult.CommandResult.Children)
		{
			if (child is not ArgumentResult { Argument.Name: DevProjexCommandTree.ProjectArgumentName } project)
				continue;
			foreach (var token in project.Tokens)
			{
				if (token.Value.Length < 2 || token.Value[0] != '-')
					continue;
				if (delimiterIndex >= 0 &&
				    IndexOfToken(parseResult.Tokens, token) > delimiterIndex)
				{
					continue;
				}

				result.Add(token);
			}
		}

		return result;
	}

	private static int IndexOfToken(IReadOnlyList<Token> tokens, Token token)
	{
		for (var index = 0; index < tokens.Count; index++)
		{
			if (ReferenceEquals(tokens[index], token))
				return index;
		}

		return -1;
	}

	private static void CollectMatchedTokens(
		CommandResult commandResult,
		ISet<Token> matchedTokens)
	{
		foreach (var token in commandResult.Tokens)
			matchedTokens.Add(token);
		if (commandResult.IdentifierToken is not null)
			matchedTokens.Add(commandResult.IdentifierToken);

		foreach (var child in commandResult.Children)
		{
			foreach (var token in child.Tokens)
				matchedTokens.Add(token);
			if (child is CommandResult childCommand)
				CollectMatchedTokens(childCommand, matchedTokens);
			else if (child is OptionResult { IdentifierToken: not null } optionResult)
				matchedTokens.Add(optionResult.IdentifierToken);
		}
	}

	private TerminalServiceFactory CreateDefaultServiceFactory()
		=> TerminalServiceFactory.FromEnvironment(
			environment.Variables,
			TerminalHostCapabilities.Desktop);

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

	private static bool TryBuildSuggestion(
		RootCommand root,
		ParseResult parseResult,
		out string suggestion)
	{
		suggestion = string.Empty;
		var unmatchedTokens = GetUnrecognizedTokensBeforeDelimiter(parseResult);
		if (unmatchedTokens.Count == 0)
			return false;

		var current = parseResult.CommandResult.Command;
		var commandPath = ResolveCommandPath(root, current);
		if (TryFindUnknownCommand(parseResult, out var unmatchedCommand))
		{
			var child = FindClosest(
				unmatchedCommand,
				current.Subcommands
					.Where(static command => !command.Hidden)
					.Select(static command => command.Name));
			if (child is not null)
			{
				var prefix = string.Join(' ', commandPath);
				suggestion = $"{prefix} {child}";
				return true;
			}
		}

		var inheritedOptions = ReferenceEquals(current, root)
			? root.Options
			: root.Options.Where(static option => option.Recursive);
		var knownOptions = inheritedOptions
			.Concat(current.Options)
			.Where(static option => !option.Hidden)
			.SelectMany(static option => new[] { option.Name }.Concat(option.Aliases))
			.Distinct(StringComparer.Ordinal)
			.ToArray();
		foreach (var unmatchedToken in unmatchedTokens)
		{
			var token = unmatchedToken.Value;
			if (!token.StartsWith('-'))
				continue;
			var optionToken = token.Split('=', 2)[0];
			if (knownOptions.Contains(optionToken, StringComparer.Ordinal))
				continue;
			// Every short option is one edit away from every other one (`-v` from `-?`), so a
			// short token is only matched when it differs from a known one by letter case.
			var option = optionToken.Length == 2 && optionToken[1] != '-'
				? knownOptions.FirstOrDefault(candidate =>
					string.Equals(candidate, optionToken, StringComparison.OrdinalIgnoreCase))
				: FindClosest(optionToken, knownOptions);
			if (option is null)
				continue;
			var prefix = string.Join(' ', commandPath);
			suggestion = $"{prefix} {option}";
			return true;
		}

		return false;
	}

	private static IReadOnlyList<string> ResolveCommandPath(
		RootCommand root,
		Command target)
	{
		var path = new List<string> { "devprojex" };
		if (ReferenceEquals(root, target))
			return path;
		if (TryAppendPath(root, target, path))
			return path;

		throw new InvalidOperationException($"Command is not part of the root tree: {target.Name}");
	}

	private static bool TryAppendPath(
		Command current,
		Command target,
		ICollection<string> path)
	{
		foreach (var child in current.Subcommands)
		{
			path.Add(child.Name);
			if (ReferenceEquals(child, target) || TryAppendPath(child, target, path))
				return true;
			path.Remove(child.Name);
		}

		return false;
	}

	private static bool IsDiagnosticVerbosity(ParseResult parseResult)
	{
		var option = parseResult.RootCommandResult.Command.Options
			.OfType<Option<TerminalVerbosity>>()
			.SingleOrDefault(static candidate => candidate.Name == "--verbosity");
		return option is not null &&
		       parseResult.GetValue(option) == TerminalVerbosity.Diagnostic;
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
