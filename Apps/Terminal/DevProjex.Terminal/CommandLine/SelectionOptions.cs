using System.CommandLine;

namespace DevProjex.Terminal.CommandLine;

internal sealed class SelectionOptions
{
	public Option<string> Profile { get; }

	public Option<string[]> Roots { get; }
	public Option<string[]> Extensions { get; }
	public Option<string[]> SelectedPaths { get; }
	public Option<string?> GitMode { get; }
	public Option<string[]> Exclusions { get; }

	public SelectionOptions(
		LocalizationService localization,
		string defaultProfile = "standard")
	{
		Profile = new Option<string>("--profile")
		{
			Description = localization["Terminal.Option.Profile"],
			DefaultValueFactory = _ => defaultProfile
		};
		Roots = Repeatable("--root", localization["Terminal.Option.Root"]);
		Extensions = Repeatable("--extension", localization["Terminal.Option.Extension"]);
		SelectedPaths = Repeatable("--select", localization["Terminal.Option.Select"]);
		GitMode = new Option<string?>("--git-mode")
		{
			Description = localization["Terminal.Option.GitMode"]
		};
		Exclusions = Repeatable("--exclude", localization["Terminal.Option.Exclude"]);
		GitMode.Validators.Add(result =>
		{
			var value = result.GetValueOrDefault<string?>();
			if (value is not null && !CliValueParser.TryParseGitMode(value, out _))
				result.AddError(LocalizedParseError.Create(localization["Terminal.Validation.GitMode"]));
		});
		Exclusions.Validators.Add(result =>
		{
			var values = result.GetValueOrDefault<string[]>() ?? [];
			var hasNone = values.Any(static value => value.Equals("none", StringComparison.OrdinalIgnoreCase));
			if (hasNone && values.Length > 1)
				result.AddError(LocalizedParseError.Create(localization["Terminal.Validation.ExcludeNone"]));
			foreach (var value in values)
			{
				if (!value.Equals("none", StringComparison.OrdinalIgnoreCase) &&
				    !CliValueParser.TryParseExclusion(value, out _))
				{
					result.AddError(LocalizedParseError.Create(
						localization.Format("Terminal.Validation.UnknownExclusion", value)));
				}
			}
		});
	}

	public void AddTo(Command command)
	{
		command.Options.Add(Profile);
		command.Options.Add(Roots);
		command.Options.Add(Extensions);
		command.Options.Add(SelectedPaths);
		command.Options.Add(GitMode);
		command.Options.Add(Exclusions);
	}

	public async Task<ProjectSelectionSpec> ResolveAsync(
		ParseResult parseResult,
		string projectPath,
		Execution.TerminalServices services,
		CancellationToken cancellationToken)
	{
		var profileValue = parseResult.GetValue(Profile) ?? "standard";
		var profile = ResolveProfileReference(profileValue, projectPath, services);
		GitFilteringMode? gitMode = parseResult.GetResult(GitMode) is null
			? null
			: CliValueParser.ParseGitMode(parseResult.GetValue(GitMode)!);
		var exclusions = parseResult.GetResult(Exclusions) is null
			? null
			: ParseExclusions(parseResult.GetValue(Exclusions) ?? []);
		var overrides = new ProjectSelectionSpec(
			Roots: GetExplicitValues(parseResult, Roots),
			Extensions: GetExplicitValues(parseResult, Extensions),
			SelectedPaths: GetExplicitValues(parseResult, SelectedPaths),
			GitMode: gitMode,
			Exclusions: exclusions,
			ProfileSource: profile);

		return await services.SelectionResolver
			.ResolveAsync(projectPath, profile, overrides, cancellationToken)
			.ConfigureAwait(false);
	}

	private static ProjectProfileReference ResolveProfileReference(
		string value,
		string projectPath,
		Execution.TerminalServices services)
	{
		if (value.Equals("auto", StringComparison.OrdinalIgnoreCase))
		{
			return services.LocalProfileStore.TryLoadProfile(projectPath, out _)
				? ProjectProfileReference.Local
				: ProjectProfileReference.Standard;
		}
		if (value.Equals("standard", StringComparison.OrdinalIgnoreCase))
			return ProjectProfileReference.Standard;
		if (value.Equals("local", StringComparison.OrdinalIgnoreCase))
			return ProjectProfileReference.Local;

		return new ProjectProfileReference(
			ProjectProfileSourceKind.Portable,
			Path.GetFullPath(value));
	}

	private static IReadOnlyCollection<ProjectExclusion> ParseExclusions(IReadOnlyList<string> values)
	{
		if (values.Any(static value => value.Equals("none", StringComparison.OrdinalIgnoreCase)))
			return [];

		return values
			.Select(CliValueParser.ParseExclusion)
			.Distinct()
			.OrderBy(static exclusion => exclusion)
			.ToArray();
	}

	private static IReadOnlyCollection<string>? GetExplicitValues(
		ParseResult parseResult,
		Option<string[]> option) =>
		parseResult.GetResult(option) is null
			? null
			: parseResult.GetValue(option) ?? [];

	private static Option<string[]> Repeatable(string name, string description)
	{
		var option = new Option<string[]>(name)
		{
			Description = description,
			Arity = ArgumentArity.OneOrMore,
			AllowMultipleArgumentsPerToken = false
		};
		return option;
	}
}

internal static class CliValueParser
{
	public static GitFilteringMode ParseGitMode(string value) =>
		TryParseGitMode(value, out var mode)
			? mode
			: throw new ArgumentOutOfRangeException(nameof(value), value, null);

	public static bool TryParseGitMode(string value, out GitFilteringMode mode)
		=> ProjectSelectionTokens.TryParseGitMode(value, out mode);

	public static ProjectExclusion ParseExclusion(string value) =>
		TryParseExclusion(value, out var exclusion)
			? exclusion
			: throw new ArgumentOutOfRangeException(nameof(value), value, null);

	public static bool TryParseExclusion(string value, out ProjectExclusion exclusion)
		=> ProjectSelectionTokens.TryParseExclusion(value, out exclusion);

	public static TerminalColorMode ParseColor(string value) =>
		value.ToLowerInvariant() switch
		{
			"always" => TerminalColorMode.Always,
			"never" => TerminalColorMode.Never,
			_ => TerminalColorMode.Auto
		};

	public static TerminalProgressMode ParseProgress(string value) =>
		value.ToLowerInvariant() switch
		{
			"always" => TerminalProgressMode.Always,
			"never" => TerminalProgressMode.Never,
			_ => TerminalProgressMode.Auto
		};

	public static TerminalVerbosity ParseVerbosity(string value) =>
		value.ToLowerInvariant() switch
		{
			"quiet" => TerminalVerbosity.Quiet,
			"minimal" => TerminalVerbosity.Minimal,
			"detailed" => TerminalVerbosity.Detailed,
			"diagnostic" => TerminalVerbosity.Diagnostic,
			_ => TerminalVerbosity.Normal
		};
}
