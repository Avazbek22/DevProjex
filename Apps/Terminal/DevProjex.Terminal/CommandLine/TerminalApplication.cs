using System.CommandLine;
using System.CommandLine.Invocation;
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
			foreach (var error in parseResult.Errors)
				environment.Error.WriteLine(
					$"error[DPX-CLI-INVALID-SYNTAX]: {localization.Format(
						"Terminal.Error.InvalidSyntax",
						LocalizedParseError.Resolve(error.Message, localization))}");
			if (TryBuildSuggestion(root, arguments, out var suggestion))
				environment.Error.WriteLine(localization.Format("Terminal.Hint.DidYouMean", suggestion));
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
}
