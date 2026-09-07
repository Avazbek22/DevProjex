using System.CommandLine;
using System.CommandLine.Completions;

namespace DevProjex.Terminal.CommandLine;

public sealed class CommandHelpRenderer(
	ITerminalEnvironment environment,
	LocalizationService? localization = null)
{
	private readonly LocalizationService _localization = localization ?? new LocalizationService(
		new JsonLocalizationCatalog(),
		AppLanguageUtility.DetectSystemLanguage());

	public void Write(Command command, IReadOnlyList<string>? commandPath = null)
	{
		ArgumentNullException.ThrowIfNull(command);
		var path = commandPath is { Count: > 0 }
			? string.Join(' ', commandPath)
			: "devprojex";
		var output = environment.Output;
		var terminalWidth = Math.Max(1, environment.Width);
		var options = ResolveVisibleOptions(command);
		var usage = new StringBuilder(path);
		if (command.Subcommands.Any(static child => !child.Hidden))
			usage.Append(" <command>");
		foreach (var argument in command.Arguments)
		{
			var argumentName = FormatArgumentName(argument);
			usage.Append(argument.Arity.MinimumNumberOfValues == 0
				? $" [{argumentName}]"
				: $" <{argumentName}>");
		}
		if (options.Count > 0)
			usage.Append(" [options]");
		WriteSection(output, _localization["Terminal.Help.Usage"], terminalWidth);
		WriteIndented(output, usage.ToString(), terminalWidth);

		WriteSection(output, _localization["Terminal.Help.Description"], terminalWidth);
		WriteIndented(
			output,
			command.Description ?? _localization["Terminal.Help.DefaultDescription"],
			terminalWidth);

		var arguments = command.Arguments.ToArray();
		if (arguments.Length > 0)
		{
			WriteSection(output, _localization["Terminal.Help.Arguments"], terminalWidth);
			foreach (var argument in arguments)
				WriteItem(
					output,
					FormatArgumentName(argument),
					argument.Description ?? string.Empty,
					terminalWidth);
		}

		if (options.Count > 0)
		{
			WriteSection(output, _localization["Terminal.Help.Options"], terminalWidth);
			foreach (var option in options)
				WriteItem(
					output,
					FormatOptionNames(option),
					ResolveOptionDescription(option),
					terminalWidth);
		}

		var children = command.Subcommands.Where(static child => !child.Hidden).ToArray();
		if (children.Length > 0)
		{
			WriteSection(output, _localization["Terminal.Help.Commands"], terminalWidth);
			foreach (var child in children)
				WriteItem(output, FormatCommandNames(child), child.Description ?? string.Empty, terminalWidth);
		}

		WriteSection(output, _localization["Terminal.Help.Examples"], terminalWidth);
		foreach (var example in CliExamplesRegistry.Get(command, path))
			WriteIndented(output, example, terminalWidth);

		WriteSection(output, _localization["Terminal.Help.ExitCodes"], terminalWidth);
		foreach (var code in ResolveExitCodes(command))
		{
			var key = code switch
			{
				0 => "Terminal.Exit.Success",
				1 => "Terminal.Exit.Runtime",
				2 => "Terminal.Exit.Syntax",
				3 => "Terminal.Exit.Policy",
				4 => "Terminal.Exit.Conflict",
				5 => "Terminal.Exit.Desktop",
				130 => "Terminal.Exit.Canceled",
				_ => throw new ArgumentOutOfRangeException(nameof(code), code, null)
			};
			WriteItem(output, code.ToString(System.Globalization.CultureInfo.InvariantCulture),
				_localization[key], terminalWidth, 4);
		}
	}

	internal static IReadOnlyList<int> ResolveExitCodes(Command command)
	{
		if (command is RootCommand)
			return [0, 1, 2, 3, 4, 5, 130];

		var path = ResolveCommandPath(command);
		return path switch
		{
			"open" => [0, 1, 2, 3, 4, 5, 130],
			"analyze" or "related" or "tree" or "export context" or "export project" or
				"profile export" => [0, 1, 2, 3, 4, 130],
			"profile import" or "profile save" or
				"cache list" or "cache remove" or "cache clear" or "cache update" or
				"doctor" => [0, 1, 2, 3, 130],
			"ui list" => [0, 1, 2, 5, 130],
			"tui" => [0, 1, 2, 3, 130],
			"export" or "profile" or "cache" or "ui" => [0, 2, 130],
			"recent" or "profile show" or "profile validate" or "profile reset" or
				"cache path" or "completion" => [0, 1, 2, 130],
			_ when path.StartsWith("ui ", StringComparison.Ordinal) => [0, 1, 2, 3, 4, 5, 130],
			_ => [0, 2, 130]
		};
	}

	private static string ResolveCommandPath(Command command)
	{
		var segments = new Stack<string>();
		for (var current = command;
		     current is not RootCommand;
		     current = current.Parents.OfType<Command>().First())
		{
			segments.Push(current.Name);
		}
		return string.Join(' ', segments);
	}

	private static void WriteSection(TextWriter output, string title, int terminalWidth)
	{
		output.WriteLine();
		foreach (var line in TerminalCellWidth.Wrap(title, terminalWidth))
			output.WriteLine(line);
	}

	private string ResolveOptionDescription(Option option)
	{
		var description = option.Name switch
		{
			"--help" => _localization["Terminal.Option.Help"],
			"--version" => _localization["Terminal.Option.Version"],
			_ => option.Description ?? string.Empty
		};

		var metadata = new List<string>(3);
		if (option.Required || CliHelpMetadataRegistry.IsRequired(option))
			metadata.Add(_localization["Terminal.Help.Required"]);
		if (IsRepeatable(option))
			metadata.Add(_localization["Terminal.Help.Repeatable"]);
		if (CliHelpMetadataRegistry.TryGetDefaultDisplay(option, out var configuredDefault))
		{
			if (!string.IsNullOrWhiteSpace(configuredDefault))
			{
				metadata.Add(_localization.Format(
					"Terminal.Help.DefaultValue",
					configuredDefault));
			}
		}
		else if (option.HasDefaultValue && option.ValueType != typeof(bool))
		{
			var defaultValue = FormatDefaultValue(option, option.GetDefaultValue());
			if (!string.IsNullOrWhiteSpace(defaultValue))
			{
				metadata.Add(_localization.Format(
					"Terminal.Help.DefaultValue",
					defaultValue));
			}
		}

		return metadata.Count == 0
			? description
			: string.Join(' ', new[] { description }.Concat(metadata));
	}

	private static string FormatOptionNames(Option option)
	{
		var names = new[] { option.Name }
			.Concat(option.Aliases)
			.Where(static value => !string.IsNullOrWhiteSpace(value))
			.Distinct(StringComparer.Ordinal)
			.OrderBy(static value => value.StartsWith("--", StringComparison.Ordinal) ? 1 : 0)
			.ThenBy(static value => value, StringComparer.Ordinal)
			.ToArray();
		var valueName = ResolveValueName(option);
		return string.IsNullOrWhiteSpace(valueName)
			? string.Join(", ", names)
			: $"{string.Join(", ", names)} <{valueName}>";
	}

	private static string FormatCommandNames(Command command) =>
		string.Join(
			", ",
			new[] { command.Name }
				.Concat(command.Aliases)
				.Where(static value => !string.IsNullOrWhiteSpace(value))
				.Distinct(StringComparer.Ordinal));

	private static void WriteItem(
		TextWriter output,
		string name,
		string description,
		int terminalWidth = 80,
		int preferredNameColumnWidth = 28)
	{
		const int indentWidth = 2;
		var contentWidth = Math.Max(1, terminalWidth - indentWidth);
		var nameColumnWidth = Math.Min(preferredNameColumnWidth, contentWidth);
		var nameWidth = TerminalCellWidth.Measure(name);
		var descriptionWidth = contentWidth - nameColumnWidth;
		if (nameWidth < nameColumnWidth && descriptionWidth >= 12)
		{
			output.Write(new string(' ', indentWidth));
			output.Write(name);
			output.Write(new string(' ', nameColumnWidth - nameWidth));
			var lines = TerminalCellWidth.Wrap(description, descriptionWidth);
			output.WriteLine(lines[0]);
			foreach (var line in lines.Skip(1))
			{
				output.Write(new string(' ', indentWidth + nameColumnWidth));
				output.WriteLine(line);
			}
			return;
		}

		foreach (var line in TerminalCellWidth.Wrap(name, contentWidth))
		{
			output.Write(new string(' ', indentWidth));
			output.WriteLine(line);
		}
		WriteIndented(output, description, terminalWidth, indentWidth + 2);
	}

	private static void WriteIndented(
		TextWriter output,
		string value,
		int terminalWidth,
		int indentWidth = 2)
	{
		var effectiveIndent = Math.Min(Math.Max(0, indentWidth), Math.Max(0, terminalWidth - 1));
		var lineWidth = Math.Max(1, terminalWidth - effectiveIndent);
		foreach (var line in TerminalCellWidth.Wrap(value, lineWidth))
		{
			output.Write(new string(' ', effectiveIndent));
			output.WriteLine(line);
		}
	}

	private static string FormatArgumentName(Argument argument)
	{
		if (!string.IsNullOrWhiteSpace(argument.HelpName))
			return argument.HelpName;
		var completions = GetCompletionTokens(argument);
		if (completions.Count > 0)
			return string.Join('|', completions);
		return argument.Name;
	}

	private static string? ResolveValueName(Option option)
	{
		if (option.Arity.MaximumNumberOfValues == 0 || option.ValueType == typeof(bool))
			return null;

		if (!string.IsNullOrWhiteSpace(option.HelpName))
			return option.HelpName;

		var completions = GetCompletionTokens(option);
		if (completions.Count > 0)
			return option.Name == "--profile"
				? string.Join('|', completions.Concat(["FILE"]).Distinct(StringComparer.Ordinal))
				: string.Join('|', completions);
		return "VALUE";
	}

	private static IReadOnlyList<string> GetCompletionTokens(Symbol symbol) =>
		symbol.GetCompletions(CompletionContext.Empty)
			.Select(static item => item.InsertText)
			.Where(static value => !string.IsNullOrWhiteSpace(value))
			.Cast<string>()
			.Distinct(StringComparer.Ordinal)
			.ToArray();

	private static bool IsRepeatable(Option option) =>
		option.ValueType.IsArray ||
		option.Arity.MaximumNumberOfValues > 1;

	private static string? FormatDefaultValue(Option option, object? value)
	{
		if (value is null)
			return null;
		if (value is bool boolean)
			return boolean ? "true" : "false";

		var text = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
		if (string.IsNullOrWhiteSpace(text))
			return null;
		var normalized = new string(text
			.Where(static character => character != '-' && character != '_')
			.Select(static character => char.ToLowerInvariant(character))
			.ToArray());
		var canonicalTokens = ParseHelpNameTokens(option.HelpName)
			.Concat(GetCompletionTokens(option))
			.Distinct(StringComparer.Ordinal);
		var canonical = canonicalTokens.FirstOrDefault(token =>
			new string(token
				.Where(static character => character != '-' && character != '_')
				.Select(static character => char.ToLowerInvariant(character))
				.ToArray())
			.Equals(normalized, StringComparison.Ordinal));
		return canonical ?? text;
	}

	private static IReadOnlyList<Option> ResolveVisibleOptions(Command command) =>
		command.Options
			.Concat(EnumerateAncestors(command)
				.SelectMany(static parent => parent.Options)
				.Where(static option => option.Recursive))
			.Where(static option => !option.Hidden)
			.Distinct()
			.ToArray();

	private static IEnumerable<Command> EnumerateAncestors(Command command)
	{
		var current = command.Parents.OfType<Command>().FirstOrDefault();
		while (current is not null)
		{
			yield return current;
			current = current.Parents.OfType<Command>().FirstOrDefault();
		}
	}

	private static IEnumerable<string> ParseHelpNameTokens(string? helpName)
	{
		if (string.IsNullOrWhiteSpace(helpName))
			return [];
		return helpName
			.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.Where(static token => token.All(character =>
				char.IsLower(character) ||
				char.IsDigit(character) ||
				character is '-'));
	}

}
