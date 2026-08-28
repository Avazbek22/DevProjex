using System.CommandLine;

namespace DevProjex.Terminal.CommandLine;

internal sealed class SelectionOptions
{
	private readonly ITerminalEnvironment _environment;
	private readonly LocalizationService _localization;
	private readonly bool _includeHidePrivateData;
	private readonly bool _includeContentTransformations;
	public Option<CliProfileValue> Profile { get; }

	public Option<string[]> Roots { get; }
	public Option<string[]> Extensions { get; }
	public Option<string[]> SelectedPaths { get; }
	public Option<string?> SelectedPathsSource { get; }
	public Option<GitFilteringMode?> GitMode { get; }
	public Option<CliExclusionValue[]> Exclusions { get; }
	public Option<string?> HideSecrets { get; }
	public Option<bool> NoHideSecrets { get; }
	public Option<string?> HidePrivateData { get; }
	public Option<bool> NoHidePrivateData { get; }
	public Option<string?> CompressCode { get; }
	public Option<bool> NoCompressCode { get; }
	public Option<string?> StripComments { get; }
	public Option<bool> NoStripComments { get; }
	public Option<string?> StripBlankLines { get; }
	public Option<bool> NoStripBlankLines { get; }
	public bool IncludesHidePrivateData => _includeHidePrivateData;
	public IReadOnlyList<Option> AllSymbols =>
	[
		Profile,
		Roots,
		Extensions,
		SelectedPaths,
		SelectedPathsSource,
		GitMode,
		Exclusions,
		.. (_includeContentTransformations
			? ContentTransformationSymbols()
			: [])
	];

	public SelectionOptions(
		LocalizationService localization,
		ITerminalEnvironment environment,
		string defaultProfile = "standard",
		bool includeHidePrivateData = true,
		bool includeContentTransformations = true)
	{
		_environment = environment;
		_localization = localization;
		_includeContentTransformations = includeContentTransformations;
		_includeHidePrivateData = includeContentTransformations && includeHidePrivateData;
		Profile = CliChoiceSymbols.ProfileOption(
			localization["Terminal.Option.Profile"],
			defaultProfile,
			localization,
			allowAuto: defaultProfile.Equals("auto", StringComparison.Ordinal));
		Roots = Repeatable(
			"--root",
			"-r",
			localization["Terminal.Option.Root"],
			"PATH",
			FileSystemCompletionKind.Directories);
		Extensions = Repeatable(
			"--extension",
			"-e",
			localization["Terminal.Option.Extension"],
			"EXT");
		SelectedPaths = Repeatable(
			"--select",
			"-s",
			localization["Terminal.Option.Select"],
			"RELATIVE_PATH",
			FileSystemCompletionKind.FilesAndDirectories);
		SelectedPathsSource = new Option<string?>("--select-from")
		{
			Description = localization["Terminal.Option.SelectFrom"],
			HelpName = "FILE|-"
		};
		SelectedPathsSource.CompletionSources.Add(context =>
			FileSystemCompletionSource.Complete(
				context,
				FileSystemCompletionKind.FilesAndDirectories,
				FileSystemCompletionSource.ResolveProjectDirectory(context)));
		GitMode = CliChoiceSymbols.NullableOption(
			"--git-mode",
			localization["Terminal.Option.GitMode"],
			CliChoiceSets.GitMode,
			localization);
		Exclusions = RepeatableExclusions(localization);
		(HideSecrets, NoHideSecrets) = CreateToggle(
			"--hide-secrets", "--no-hide-secrets",
			"Terminal.Option.HideSecrets", "Terminal.Option.NoHideSecrets",
			localization);
		(HidePrivateData, NoHidePrivateData) = CreateToggle(
			"--hide-private-data", "--no-hide-private-data",
			"Terminal.Option.HidePrivateData", "Terminal.Option.NoHidePrivateData",
			localization);
		(CompressCode, NoCompressCode) = CreateToggle(
			"--compress-code", "--no-compress-code",
			"Terminal.Option.CompressCode", "Terminal.Option.NoCompressCode",
			localization);
		(StripComments, NoStripComments) = CreateToggle(
			"--strip-comments", "--no-strip-comments",
			"Terminal.Option.StripComments", "Terminal.Option.NoStripComments",
			localization);
		(StripBlankLines, NoStripBlankLines) = CreateToggle(
			"--strip-blank-lines", "--no-strip-blank-lines",
			"Terminal.Option.StripBlankLines", "Terminal.Option.NoStripBlankLines",
			localization);
	}

	public void AddTo(Command command)
	{
		command.Options.Add(Profile);
		command.Options.Add(Roots);
		command.Options.Add(Extensions);
		command.Options.Add(SelectedPaths);
		command.Options.Add(SelectedPathsSource);
		command.Options.Add(GitMode);
		command.Options.Add(Exclusions);
		if (_includeContentTransformations)
		{
			command.Options.Add(HideSecrets);
			command.Options.Add(NoHideSecrets);
			if (_includeHidePrivateData)
			{
				command.Options.Add(HidePrivateData);
				command.Options.Add(NoHidePrivateData);
			}
			command.Options.Add(CompressCode);
			command.Options.Add(NoCompressCode);
			command.Options.Add(StripComments);
			command.Options.Add(NoStripComments);
			command.Options.Add(StripBlankLines);
			command.Options.Add(NoStripBlankLines);
			command.Validators.Add(result =>
			{
				foreach (var (positive, negative) in TogglePairs())
				{
					if (result.GetResult(positive) is { Implicit: false } &&
					    result.GetResult(negative) is { Implicit: false })
					{
						result.AddError(LocalizedParseError.Create(_localization.Format(
							"Terminal.Validation.BooleanOptionConflict",
							positive.Name,
							negative.Name)));
					}
				}
			});
		}
	}

	public async Task<ProjectSelectionSpec> ResolveAsync(
		ParseResult parseResult,
		string projectPath,
		Execution.TerminalServices services,
		CancellationToken cancellationToken)
	{
		var selectedPaths = await ReadSelectedPathsAsync(
			parseResult,
			cancellationToken).ConfigureAwait(false);
		return await ResolveAsync(
			parseResult,
			projectPath,
			services,
			selectedPaths,
			cancellationToken).ConfigureAwait(false);
	}

	public async Task<ProjectSelectionSpec> ResolveAsync(
		ParseResult parseResult,
		string projectPath,
		Execution.TerminalServices services,
		IReadOnlyCollection<string>? selectedPaths,
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
		bool? hideSecrets = _includeContentTransformations
			? ResolveToggle(parseResult, HideSecrets, NoHideSecrets)
			: null;
		bool? hidePrivateData = _includeHidePrivateData
			? ResolveToggle(parseResult, HidePrivateData, NoHidePrivateData)
			: null;
		bool? compressCode = _includeContentTransformations
			? ResolveToggle(parseResult, CompressCode, NoCompressCode)
			: null;
		bool? stripComments = _includeContentTransformations
			? ResolveToggle(parseResult, StripComments, NoStripComments)
			: null;
		bool? stripBlankLines = _includeContentTransformations
			? ResolveToggle(parseResult, StripBlankLines, NoStripBlankLines)
			: null;
		SelectedPathExistenceValidator.Validate(projectPath, selectedPaths);
		var overrides = new ProjectSelectionSpec(
			Roots: GetExplicitValues(parseResult, Roots),
			Extensions: GetExplicitValues(parseResult, Extensions),
			SelectedPaths: selectedPaths,
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

	public async Task<IReadOnlyCollection<string>?> ReadSelectedPathsAsync(
		ParseResult parseResult,
		CancellationToken cancellationToken)
	{
		var direct = GetExplicitValues(parseResult, SelectedPaths);
		var sourceResult = parseResult.GetResult(SelectedPathsSource);
		if (direct is null && sourceResult is null)
			return null;

		var selected = new HashSet<string>(PathComparer.Default);
		if (direct is not null)
			selected.UnionWith(direct);
		if (sourceResult is not null)
		{
			var source = parseResult.GetValue(SelectedPathsSource);
			if (string.IsNullOrWhiteSpace(source))
				throw InvalidSelectionSource();
			selected.UnionWith(await SelectionPathListReader.ReadAsync(
				source,
				_environment,
				cancellationToken).ConfigureAwait(false));
		}

		return selected.ToArray();
	}

	private static ProjectContextValidationException InvalidSelectionSource() =>
		new("DPX-CLI-SELECT-FROM-INVALID", "Selection source is invalid.");

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
		string alias,
		string description,
		string helpName,
		FileSystemCompletionKind? completionKind = null)
	{
		var option = new Option<string[]>(name, alias)
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
		var option = new Option<CliExclusionValue[]>("--exclude", "-x")
		{
			Description = localization["Terminal.Option.Exclude"],
			HelpName = "NAME",
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

	private IReadOnlyList<Option> ContentTransformationSymbols()
	{
		var symbols = new List<Option>
		{
			HideSecrets,
			NoHideSecrets
		};
		if (_includeHidePrivateData)
		{
			symbols.Add(HidePrivateData);
			symbols.Add(NoHidePrivateData);
		}
		symbols.AddRange([
			CompressCode,
			NoCompressCode,
			StripComments,
			NoStripComments,
			StripBlankLines,
			NoStripBlankLines
		]);
		return symbols;
	}

	private IEnumerable<(Option<string?> Positive, Option<bool> Negative)> TogglePairs()
	{
		yield return (HideSecrets, NoHideSecrets);
		if (_includeHidePrivateData)
			yield return (HidePrivateData, NoHidePrivateData);
		yield return (CompressCode, NoCompressCode);
		yield return (StripComments, NoStripComments);
		yield return (StripBlankLines, NoStripBlankLines);
	}

	private static bool? ResolveToggle(
		ParseResult parseResult,
		Option<string?> positive,
		Option<bool> negative)
	{
		if (parseResult.GetResult(negative) is { Implicit: false })
			return false;
		if (parseResult.GetResult(positive) is not { Implicit: false })
			return null;
		return parseResult.GetValue(positive) switch
		{
			null => true,
			var value when value.Equals("true", StringComparison.OrdinalIgnoreCase) => true,
			var value when value.Equals("on", StringComparison.OrdinalIgnoreCase) => true,
			_ => false
		};
	}

	private static (Option<string?> Positive, Option<bool> Negative) CreateToggle(
		string positiveName,
		string negativeName,
		string positiveDescriptionKey,
		string negativeDescriptionKey,
		LocalizationService localization)
	{
		var positive = new Option<string?>(positiveName)
		{
			Description = localization[positiveDescriptionKey],
			HelpName = "on|off",
			Arity = ArgumentArity.ZeroOrOne,
			CustomParser = result =>
			{
				if (result.Tokens.Count == 0)
					return null;
				var value = result.Tokens[0].Value;
				if (value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
				    value.Equals("on", StringComparison.OrdinalIgnoreCase))
					return "on";
				if (value.Equals("false", StringComparison.OrdinalIgnoreCase) ||
				    value.Equals("off", StringComparison.OrdinalIgnoreCase))
					return "off";
				result.AddError(LocalizedParseError.Create(localization.Format(
					"Terminal.Validation.Choice",
					positiveName,
					"true, false, on, off")));
				return null;
			}
		};
		positive.CompletionSources.Add(["on", "off", "true", "false"]);
		var negative = new Option<bool>(negativeName)
		{
			Description = localization[negativeDescriptionKey]
		};
		CompletionConflictRegistry.RegisterMutual(positive, negative);
		return (positive, negative);
	}
}
