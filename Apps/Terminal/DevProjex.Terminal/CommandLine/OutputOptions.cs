using System.CommandLine;

namespace DevProjex.Terminal.CommandLine;

internal sealed class OutputOptions
{
	public OutputOptions(LocalizationService localization)
	{
		Color = Choice(localization, "--color", localization["Terminal.Option.Color"], "auto", "always", "never");
		Progress = Choice(localization, "--progress", localization["Terminal.Option.Progress"], "auto", "always", "never");
		Verbosity = Choice(
			localization,
			"--verbosity",
			localization["Terminal.Option.Verbosity"],
			"normal",
			"quiet",
			"minimal",
			"detailed",
			"diagnostic");
		Plain = new Option<bool>("--plain")
		{
			Description = localization["Terminal.Option.Plain"]
		};
	}

	public Option<string> Color { get; }
	public Option<string> Progress { get; }
	public Option<string> Verbosity { get; }
	public Option<bool> Plain { get; }

	public void AddTo(Command command, bool includeProgress = true)
	{
		command.Options.Add(Color);
		if (includeProgress)
			command.Options.Add(Progress);
		command.Options.Add(Verbosity);
		command.Options.Add(Plain);
	}

	public TerminalOutputOptions Get(ParseResult parseResult) =>
		new(
			CliValueParser.ParseColor(parseResult.GetValue(Color) ?? "auto"),
			CliValueParser.ParseProgress(parseResult.GetValue(Progress) ?? "auto"),
			CliValueParser.ParseVerbosity(parseResult.GetValue(Verbosity) ?? "normal"),
			parseResult.GetValue(Plain));

	private static Option<string> Choice(
		LocalizationService localization,
		string name,
		string description,
		string defaultValue,
		params string[] additionalValues)
	{
		var values = new[] { defaultValue }.Concat(additionalValues).Distinct(StringComparer.Ordinal).ToArray();
		var option = new Option<string>(name)
		{
			Description = description,
			DefaultValueFactory = _ => defaultValue
		};
		option.Validators.Add(result =>
		{
			var value = result.GetValueOrDefault<string>();
			if (value is null || !values.Contains(value, StringComparer.OrdinalIgnoreCase))
				result.AddError(LocalizedParseError.Create(localization.Format(
					"Terminal.Validation.Choice",
					name,
					string.Join(", ", values))));
		});
		option.CompletionSources.Add(values);
		return option;
	}
}
