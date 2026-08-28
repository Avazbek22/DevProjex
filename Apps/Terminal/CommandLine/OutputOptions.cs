using System.CommandLine;
using System.CommandLine.Parsing;

namespace DevProjex.Terminal.CommandLine;

internal sealed class OutputOptions
{
	private readonly LocalizationService _localization;
	private readonly TerminalProgressMode _defaultProgress;

	public OutputOptions(
		LocalizationService localization,
		ITerminalEnvironment? environment = null)
	{
		_localization = localization;
		var variables = environment?.Variables;
		var defaultColor = ResolveEnvironmentChoice(
			variables,
			"DEVPROJEX_COLOR",
			CliChoiceSets.ColorMode,
			TerminalColorMode.Auto);
		_defaultProgress = ResolveEnvironmentChoice(
			variables,
			"DEVPROJEX_PROGRESS",
			CliChoiceSets.ProgressMode,
			TerminalProgressMode.Auto);
		var defaultVerbosity = ResolveEnvironmentChoice(
			variables,
			"DEVPROJEX_VERBOSITY",
			CliChoiceSets.Verbosity,
			TerminalVerbosity.Normal);
		Color = CliChoiceSymbols.Option(
			"--color",
			localization["Terminal.Option.Color"],
			defaultColor,
			CliChoiceSets.ColorMode,
			localization);
		Progress = CliChoiceSymbols.Option(
			"--progress",
			localization["Terminal.Option.Progress"],
			_defaultProgress,
			CliChoiceSets.ProgressMode,
			localization);
		Verbosity = CliChoiceSymbols.Option(
			"--verbosity",
			localization["Terminal.Option.Verbosity"],
			defaultVerbosity,
			CliChoiceSets.Verbosity,
			localization);
		Quiet = new Option<bool>("-q", "--quiet")
		{
			Description = localization["Terminal.Option.Quiet"]
		};
		Plain = new Option<bool>("--plain")
		{
			Description = localization["Terminal.Option.Plain"]
		};
	}

	public Option<TerminalColorMode> Color { get; }
	public Option<TerminalProgressMode> Progress { get; }
	public Option<TerminalVerbosity> Verbosity { get; }
	public Option<bool> Quiet { get; }
	public Option<bool> Plain { get; }

	public void AddGlobalsTo(RootCommand root)
	{
		Color.Recursive = true;
		Verbosity.Recursive = true;
		Quiet.Recursive = true;
		Plain.Recursive = true;
		root.Options.Add(Color);
		root.Options.Add(Verbosity);
		root.Options.Add(Quiet);
		root.Options.Add(Plain);
		CompletionAvailabilityRegistry.RegisterOption(
			Plain,
			result =>
				!CliParseValue.TryGet(result, Color, out var color) ||
				color != TerminalColorMode.Always);
		CompletionAvailabilityRegistry.RegisterValue(
			Color,
			(result, value) =>
				!value.Equals("always", StringComparison.Ordinal) ||
				!CliParseValue.TryGet(result, Plain, out var plain) ||
				!plain);
	}

	public void AddValidatorsTo(Command command)
	{
		if (command.Subcommands.Count == 0)
		{
			command.Validators.Add(ValidatePresentationOptions);
			return;
		}

		foreach (var child in command.Subcommands)
			AddValidatorsTo(child);
	}

	private void ValidatePresentationOptions(CommandResult result)
	{
		if (result.GetValue(Quiet) && result.GetResult(Verbosity) is { Implicit: false })
		{
			result.AddError(LocalizedParseError.Create(
				_localization["Terminal.Validation.QuietVerbosityConflict"]));
		}
		if (result.GetValue(Plain) && result.GetValue(Color) == TerminalColorMode.Always)
		{
			result.AddError(LocalizedParseError.Create(
				_localization["Terminal.Validation.PlainColorConflict"]));
		}
	}

	public void AddProgressTo(Command command) => command.Options.Add(Progress);

	public TerminalOutputOptions Get(ParseResult parseResult) =>
		new(
			parseResult.GetValue(Color),
			parseResult.GetResult(Progress) is null
				? _defaultProgress
				: parseResult.GetValue(Progress),
			parseResult.GetValue(Quiet)
				? TerminalVerbosity.Quiet
				: parseResult.GetValue(Verbosity),
			parseResult.GetValue(Plain));

	private static T ResolveEnvironmentChoice<T>(
		IReadOnlyDictionary<string, string?>? variables,
		string name,
		CliChoiceSet<T> choices,
		T fallback)
		where T : struct =>
		variables is not null &&
		variables.TryGetValue(name, out var value) &&
		value is not null &&
		choices.TryParse(value, out var parsed)
			? parsed
			: fallback;
}
