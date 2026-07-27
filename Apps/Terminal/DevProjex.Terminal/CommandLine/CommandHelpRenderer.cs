using System.CommandLine;

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
		WriteSection(output, _localization["Terminal.Help.Usage"]);
		output.Write("  ");
		output.Write(path);
		if (command.Subcommands.Any(static child => !child.Hidden))
			output.Write(" <command>");
		foreach (var argument in command.Arguments)
			output.Write(argument.Arity.MinimumNumberOfValues == 0
				? $" [{argument.Name}]"
				: $" <{argument.Name}>");
		if (command.Options.Any(static option => !option.Hidden))
			output.Write(" [options]");
		output.WriteLine();

		WriteSection(output, _localization["Terminal.Help.Description"]);
		output.Write("  ");
		output.WriteLine(command.Description ?? _localization["Terminal.Help.DefaultDescription"]);

		var arguments = command.Arguments.ToArray();
		if (arguments.Length > 0)
		{
			WriteSection(output, _localization["Terminal.Help.Arguments"]);
			foreach (var argument in arguments)
				WriteItem(output, argument.Name, argument.Description ?? string.Empty);
		}

		var options = command.Options.Where(static option => !option.Hidden).ToArray();
		var includesLanguage = options.Any(static option => option.Name == "--language");
		if (options.Length > 0 || !includesLanguage)
		{
			WriteSection(output, _localization["Terminal.Help.Options"]);
			foreach (var option in options)
				WriteItem(
					output,
					FormatOptionNames(option),
					option.Description ?? string.Empty,
					environment.Width);
			if (!includesLanguage)
			{
				WriteItem(
					output,
					"--language",
					_localization["Terminal.Option.Language"],
					environment.Width);
			}
		}

		var children = command.Subcommands.Where(static child => !child.Hidden).ToArray();
		if (children.Length > 0)
		{
			WriteSection(output, _localization["Terminal.Help.Commands"]);
			foreach (var child in children)
				WriteItem(output, child.Name, child.Description ?? string.Empty, environment.Width);
		}

		WriteSection(output, _localization["Terminal.Help.Examples"]);
		foreach (var example in ResolveExamples(path))
			output.WriteLine($"  {example}");

		WriteSection(output, _localization["Terminal.Help.ExitCodes"]);
		output.WriteLine($"  0   {_localization["Terminal.Exit.Success"]}");
		output.WriteLine($"  1   {_localization["Terminal.Exit.Runtime"]}");
		output.WriteLine($"  2   {_localization["Terminal.Exit.Syntax"]}");
		output.WriteLine($"  3   {_localization["Terminal.Exit.Policy"]}");
		output.WriteLine($"  4   {_localization["Terminal.Exit.Conflict"]}");
		output.WriteLine($"  5   {_localization["Terminal.Exit.Desktop"]}");
		output.WriteLine($"  130 {_localization["Terminal.Exit.Canceled"]}");
	}

	private static void WriteSection(TextWriter output, string title)
	{
		output.WriteLine();
		output.WriteLine(title);
	}

	private static string FormatOptionNames(Option option)
	{
		var names = new[] { option.Name }
			.Concat(option.Aliases)
			.Where(static value => !string.IsNullOrWhiteSpace(value))
			.Distinct(StringComparer.Ordinal)
			.ToArray();
		return string.Join(", ", names);
	}

	private static void WriteItem(
		TextWriter output,
		string name,
		string description,
		int terminalWidth = 80)
	{
		const int nameColumnWidth = 28;
		output.Write("  ");
		output.Write(name);
		if (name.Length < nameColumnWidth)
			output.Write(new string(' ', nameColumnWidth - name.Length));
		else
		{
			output.WriteLine();
			output.Write(new string(' ', nameColumnWidth + 2));
		}

		var descriptionWidth = Math.Max(20, terminalWidth - nameColumnWidth - 4);
		var lines = Wrap(description, descriptionWidth);
		output.WriteLine(lines[0]);
		foreach (var line in lines.Skip(1))
		{
			output.Write(new string(' ', nameColumnWidth + 2));
			output.WriteLine(line);
		}
	}

	private static IReadOnlyList<string> Wrap(string value, int width)
	{
		if (string.IsNullOrWhiteSpace(value))
			return [string.Empty];

		var lines = new List<string>();
		var current = new StringBuilder();
		foreach (var word in value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
		{
			if (current.Length > 0 && current.Length + 1 + word.Length > width)
			{
				lines.Add(current.ToString());
				current.Clear();
			}

			if (current.Length > 0)
				current.Append(' ');
			current.Append(word);
		}

		if (current.Length > 0)
			lines.Add(current.ToString());
		return lines.Count == 0 ? [string.Empty] : lines;
	}

	private static IReadOnlyList<string> ResolveExamples(string path) =>
		path switch
		{
			"devprojex" =>
			[
				"devprojex",
				"devprojex analyze .",
				"devprojex export context . -o context.md"
			],
			"devprojex tui" => ["devprojex tui .", "devprojex tui . --screen inline"],
			"devprojex open" => ["devprojex open . --preview", "devprojex open --last"],
			"devprojex analyze" => ["devprojex analyze .", "devprojex analyze . --format json -o -"],
			"devprojex export" => ["devprojex export context .", "devprojex export project . --as zip -o project.zip"],
			"devprojex export context" =>
			[
				"devprojex export context . --view tree-content --format markdown -o context.md",
				"devprojex export context . --format json -o -"
			],
			"devprojex export project" =>
			[
				"devprojex export project . --as folder -o ./submission",
				"devprojex export project . --as zip -o ./submission.zip"
			],
			"devprojex profile" => ["devprojex profile show .", "devprojex profile validate ./.devprojex/profile.json"],
			"devprojex ui" => ["devprojex ui list", "devprojex ui status"],
			"devprojex doctor" => ["devprojex doctor", "devprojex doctor --format json"],
			"devprojex completion" => ["devprojex completion powershell", "devprojex completion bash"],
			_ => [$"{path} --help"]
		};
}
