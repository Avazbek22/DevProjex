using System.CommandLine;

namespace DevProjex.Terminal.CommandLine;

internal sealed class OutputOptions
{
	private readonly LocalizationService _localization;

	public OutputOptions(LocalizationService localization)
	{
		_localization = localization;
		Color = CliChoiceSymbols.Option(
			"--color",
			localization["Terminal.Option.Color"],
			TerminalColorMode.Auto,
			CliChoiceSets.ColorMode,
			localization);
		Progress = CliChoiceSymbols.Option(
			"--progress",
			localization["Terminal.Option.Progress"],
			TerminalProgressMode.Auto,
			CliChoiceSets.ProgressMode,
			localization);
		Verbosity = CliChoiceSymbols.Option(
			"--verbosity",
			localization["Terminal.Option.Verbosity"],
			TerminalVerbosity.Normal,
			CliChoiceSets.Verbosity,
			localization);
		Plain = new Option<bool>("--plain")
		{
			Description = localization["Terminal.Option.Plain"]
		};
	}

	public Option<TerminalColorMode> Color { get; }
	public Option<TerminalProgressMode> Progress { get; }
	public Option<TerminalVerbosity> Verbosity { get; }
	public Option<bool> Plain { get; }

	public void AddTo(Command command, bool includeProgress = true)
	{
		command.Options.Add(Color);
		if (includeProgress)
			command.Options.Add(Progress);
		command.Options.Add(Verbosity);
		command.Options.Add(Plain);
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
		command.Validators.Add(result =>
		{
			if (CliParseValue.TryGet(result, Plain, out var plain) &&
			    plain &&
			    CliParseValue.TryGet(result, Color, out var color) &&
			    color == TerminalColorMode.Always)
			{
				result.AddError(LocalizedParseError.Create(
					_localization["Terminal.Validation.PlainColorConflict"]));
			}
		});
	}

	public TerminalOutputOptions Get(ParseResult parseResult) =>
		new(
			parseResult.GetValue(Color),
			parseResult.GetValue(Progress),
			parseResult.GetValue(Verbosity),
			parseResult.GetValue(Plain));
}
