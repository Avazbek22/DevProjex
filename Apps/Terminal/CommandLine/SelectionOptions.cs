using System.CommandLine;

namespace DevProjex.Terminal.CommandLine;

internal sealed class SelectionOptions
{
	private readonly bool _includeHidePrivateData;
	public Option<CliProfileValue> Profile { get; }

	public Option<string[]> Roots { get; }
	public Option<string[]> Extensions { get; }
	public Option<string[]> SelectedPaths { get; }
	public Option<GitFilteringMode?> GitMode { get; }
	public Option<CliExclusionValue[]> Exclusions { get; }
	public Option<bool> HideSecrets { get; }
	public Option<bool> HidePrivateData { get; }
	public Option<bool> CompressCode { get; }
	public Option<bool> StripComments { get; }
	public Option<bool> StripBlankLines { get; }
	public bool IncludesHidePrivateData => _includeHidePrivateData;

	public SelectionOptions(
		LocalizationService localization,
		string defaultProfile = "standard",
		bool includeHidePrivateData = true)
	{
		_includeHidePrivateData = includeHidePrivateData;
		Profile = CliChoiceSymbols.ProfileOption(
			localization["Terminal.Option.Profile"],
			defaultProfile,
			localization,
			allowAuto: defaultProfile.Equals("auto", StringComparison.Ordinal));
		Roots = Repeatable(
			"--root",
			localization["Terminal.Option.Root"],
			"PATH",
			FileSystemCompletionKind.Directories);
		Extensions = Repeatable(
			"--extension",
			localization["Terminal.Option.Extension"],
			"EXT");
		SelectedPaths = Repeatable(
			"--select",
			localization["Terminal.Option.Select"],
			"RELATIVE_PATH",
			FileSystemCompletionKind.FilesAndDirectories);
		GitMode = CliChoiceSymbols.NullableOption(
			"--git-mode",
			localization["Terminal.Option.GitMode"],
			CliChoiceSets.GitMode,
			localization);
		Exclusions = RepeatableExclusions(localization);
		HideSecrets = new Option<bool>("--hide-secrets")
		{
			Description = localization["Terminal.Option.HideSecrets"]
		};
		HidePrivateData = new Option<bool>("--hide-private-data")
		{
			Description = localization["Terminal.Option.HidePrivateData"]
		};
		CompressCode = new Option<bool>("--compress")
		{
			Description = localization["Terminal.Option.CompressCode"]
		};
		StripComments = new Option<bool>("--strip-comments")
		{
			Description = localization["Terminal.Option.StripComments"]
		};
		StripBlankLines = new Option<bool>("--strip-blank-lines")
		{
			Description = localization["Terminal.Option.StripBlankLines"]
		};
	}

	public void AddTo(Command command)
	{
		command.Options.Add(Profile);
		command.Options.Add(Roots);
		command.Options.Add(Extensions);
		command.Options.Add(SelectedPaths);
		command.Options.Add(GitMode);
		command.Options.Add(Exclusions);
		command.Options.Add(HideSecrets);
		if (_includeHidePrivateData)
			command.Options.Add(HidePrivateData);
		command.Options.Add(CompressCode);
		command.Options.Add(StripComments);
		command.Options.Add(StripBlankLines);
	}

	public async Task<ProjectSelectionSpec> ResolveAsync(
		ParseResult parseResult,
		string projectPath,
		Execution.TerminalServices services,
		CancellationToken cancellationToken)
	{
		var profileValue = parseResult.GetValue(Profile);
		var profile = profileValue.Resolve(projectPath, services);
		GitFilteringMode? gitMode = parseResult.GetResult(GitMode) is null
			? null
			: parseResult.GetValue(GitMode)!.Value;
		var exclusions = parseResult.GetResult(Exclusions) is null
			? null
			: ParseExclusions(parseResult.GetValue(Exclusions) ?? []);
		bool? hideSecrets = parseResult.GetResult(HideSecrets) is { Implicit: false }
			? parseResult.GetValue(HideSecrets)
			: null;
		bool? hidePrivateData = _includeHidePrivateData &&
		                        parseResult.GetResult(HidePrivateData) is { Implicit: false }
			? parseResult.GetValue(HidePrivateData)
			: null;
		bool? compressCode = parseResult.GetResult(CompressCode) is { Implicit: false }
			? parseResult.GetValue(CompressCode)
			: null;
		bool? stripComments = parseResult.GetResult(StripComments) is { Implicit: false }
			? parseResult.GetValue(StripComments)
			: null;
		bool? stripBlankLines = parseResult.GetResult(StripBlankLines) is { Implicit: false }
			? parseResult.GetValue(StripBlankLines)
			: null;
		var overrides = new ProjectSelectionSpec(
			Roots: GetExplicitValues(parseResult, Roots),
			Extensions: GetExplicitValues(parseResult, Extensions),
			SelectedPaths: GetExplicitValues(parseResult, SelectedPaths),
			GitMode: gitMode,
			Exclusions: exclusions,
			HideSecrets: hideSecrets,
			HidePrivateData: hidePrivateData,
			CompressCode: compressCode,
			StripComments: stripComments,
			StripBlankLines: stripBlankLines,
			ProfileSource: profile);

		return await services.SelectionResolver
			.ResolveAsync(projectPath, profile, overrides, cancellationToken)
			.ConfigureAwait(false);
	}

	private static IReadOnlyCollection<ProjectExclusion> ParseExclusions(
		IReadOnlyList<CliExclusionValue> values)
	{
		if (values.Any(static value => value.IsNone))
			return [];

		return values
			.Select(static value => value.Exclusion!.Value)
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

	private static Option<string[]> Repeatable(
		string name,
		string description,
		string helpName,
		FileSystemCompletionKind? completionKind = null)
	{
		var option = new Option<string[]>(name)
		{
			Description = description,
			HelpName = helpName,
			Arity = ArgumentArity.OneOrMore,
			AllowMultipleArgumentsPerToken = false
		};
		if (completionKind is { } kind)
		{
			option.CompletionSources.Add(context => FileSystemCompletionSource.Complete(
				context,
				kind,
				FileSystemCompletionSource.ResolveProjectDirectory(context)));
		}
		return option;
	}

	private static Option<CliExclusionValue[]> RepeatableExclusions(LocalizationService localization)
	{
		var option = new Option<CliExclusionValue[]>("--exclude")
		{
			Description = localization["Terminal.Option.Exclude"],
			HelpName = string.Join('|', CliChoiceSets.Exclusion.Tokens),
			Arity = ArgumentArity.OneOrMore,
			AllowMultipleArgumentsPerToken = false,
			CustomParser = result =>
			{
				var values = new List<CliExclusionValue>(result.Tokens.Count);
				foreach (var token in result.Tokens)
				{
					if (CliChoiceSets.Exclusion.TryParse(token.Value, out var value))
					{
						values.Add(value);
						continue;
					}

					result.AddError(LocalizedParseError.Create(
						localization.Format("Terminal.Validation.UnknownExclusion", token.Value)));
				}

				if (values.Any(static value => value.IsNone) &&
				    values.Any(static value => !value.IsNone))
				{
					result.AddError(LocalizedParseError.Create(
						localization["Terminal.Validation.ExcludeNone"]));
				}

				return values.ToArray();
			}
		};
		option.CompletionSources.Add(CliChoiceSets.Exclusion.Tokens.ToArray());
		return option;
	}
}
