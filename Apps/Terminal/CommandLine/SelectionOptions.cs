using System.CommandLine;

namespace DevProjex.Terminal.CommandLine;

internal enum GitModeOptionCapability
{
	All,
	Desktop,
	Persistent
}

internal sealed class SelectionOptions
{
	private readonly ITerminalEnvironment _environment;
	private readonly LocalizationService _localization;
	private readonly bool _includeHidePrivateData;
	private readonly bool _includeContentTransformations;
	private readonly bool _includeMaxFileBytes;
	public Option<CliProfileValue> Profile { get; }

	public Option<string[]> Roots { get; }
	public Option<string[]> Extensions { get; }
	public Option<string[]> SelectedPaths { get; }
	public Option<string?> SelectedPathsSource { get; }
	public Option<CliGitModeValue?> GitMode { get; }
	public Option<CliExclusionValue[]> Exclusions { get; }
	public Option<bool?> HideSecrets { get; }
	public Option<bool> NoHideSecrets { get; }
	public Option<bool?> HidePrivateData { get; }
	public Option<bool> NoHidePrivateData { get; }
	public Option<bool?> CompressCode { get; }
	public Option<bool> NoCompressCode { get; }
	public Option<bool?> StripComments { get; }
	public Option<bool> NoStripComments { get; }
	public Option<bool?> StripBlankLines { get; }
	public Option<bool> NoStripBlankLines { get; }
	public Option<long?> MaxFileBytes { get; }
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
		.. MaxFileSizeSymbols(),
		.. (_includeContentTransformations
			? ContentTransformationSymbols()
			: [])
	];

	public SelectionOptions(
		LocalizationService localization,
		ITerminalEnvironment environment,
		string defaultProfile = "standard",
		bool includeHidePrivateData = true,
		bool includeContentTransformations = true,
		bool includeMaxFileBytes = false,
		GitModeOptionCapability gitModeCapability = GitModeOptionCapability.All)
	{
		_environment = environment;
		_localization = localization;
		_includeContentTransformations = includeContentTransformations;
		_includeHidePrivateData = includeContentTransformations && includeHidePrivateData;
		_includeMaxFileBytes = includeMaxFileBytes;
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
		GitMode = CreateGitModeOption(localization, gitModeCapability);
		Exclusions = RepeatableExclusions(localization);
		MaxFileBytes = new Option<long?>("--max-file-bytes")
		{
			Description = localization["Terminal.Option.MaxFileBytes"],
			HelpName = "SIZE",
			Arity = ArgumentArity.ExactlyOne,
			CustomParser = result =>
			{
				if (result.Tokens.Count == 1 &&
				    TryParseFileSize(result.Tokens[0].Value, out var value))
				{
					return value;
				}

				result.AddError(LocalizedParseError.Create(
					localization["Terminal.Validation.MaxFileBytes"]));
				return null;
			}
		};
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
		if (_includeMaxFileBytes)
			command.Options.Add(MaxFileBytes);
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
		var parsedGitMode = parseResult.GetResult(GitMode) is null
			? null
			: parseResult.GetValue(GitMode);
		GitFilteringMode? gitMode = parsedGitMode?.Mode;
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
			GitDiffRange: parsedGitMode?.DiffRange,
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

	public bool? GetHideSecretsOverride(ParseResult parseResult) =>
		_includeContentTransformations
			? ResolveToggle(parseResult, HideSecrets, NoHideSecrets)
			: null;

	public long? GetMaxFileBytes(ParseResult parseResult) =>
		_includeMaxFileBytes && parseResult.GetResult(MaxFileBytes) is { Implicit: false }
			? parseResult.GetValue(MaxFileBytes)
			: null;

	public async Task<IReadOnlyCollection<string>?> ReadSelectedPathsAsync(
		ParseResult parseResult,
		CancellationToken cancellationToken)
	{
		var direct = GetExplicitValues(parseResult, SelectedPaths);
		var sourceResult = parseResult.GetResult(SelectedPathsSource);
		if (direct is null && sourceResult is null)
			return null;

		var selected = new HashSet<string>(ProjectTreePathIdentity.CanonicalComparer);
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

	private static Option<CliGitModeValue?> CreateGitModeOption(
		LocalizationService localization,
		GitModeOptionCapability capability)
	{
		var fixedTokens = capability == GitModeOptionCapability.Persistent
			? CliChoiceSets.PersistentGitMode.Tokens
			: CliChoiceSets.GitMode.Tokens;
		var advertisesDiff = capability == GitModeOptionCapability.All;
		var advertisedTokens = advertisesDiff
			? fixedTokens.Concat(["diff:<ref>..<ref>"]).ToArray()
			: fixedTokens.ToArray();
		var capabilityMessage = capability == GitModeOptionCapability.All
			? localization["Terminal.Validation.GitMode"]
			: localization.Format(
				"Terminal.Validation.Choice",
				"--git-mode",
				string.Join(", ", advertisedTokens));
		var option = new Option<CliGitModeValue?>("--git-mode")
		{
			Description = capability == GitModeOptionCapability.All
				? localization["Terminal.Option.GitMode"]
				: capabilityMessage,
			HelpName = "MODE",
			Arity = ArgumentArity.ExactlyOne,
			CustomParser = result =>
			{
				if (result.Tokens.Count == 1)
				{
					var token = result.Tokens[0].Value;
					if (GitScopeSelection.TryParse(token, out var mode, out var diffRange))
					{
						return new CliGitModeValue(mode, diffRange);
					}
				}

				result.AddError(LocalizedParseError.Create(capabilityMessage));
				return null;
			}
		};
		option.CompletionSources.Add(fixedTokens.ToArray());
		if (advertisesDiff)
			option.CompletionSources.Add(["diff:<ref>..<ref>"]);
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

	internal static bool TryParseFileSize(string? token, out long value)
	{
		value = 0;
		if (string.IsNullOrWhiteSpace(token))
			return false;

		var normalized = token.Trim();
		var suffixLength = 0;
		long multiplier = 1;
		foreach (var (suffix, factor) in FileSizeSuffixes)
		{
			if (!normalized.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
				continue;
			suffixLength = suffix.Length;
			multiplier = factor;
			break;
		}

		var number = suffixLength == 0
			? normalized
			: normalized[..^suffixLength];
		if (!long.TryParse(
			    number,
			    System.Globalization.NumberStyles.None,
			    System.Globalization.CultureInfo.InvariantCulture,
			    out var parsed) ||
		    parsed < 1 ||
		    parsed > long.MaxValue / multiplier)
		{
			return false;
		}

		value = parsed * multiplier;
		return true;
	}

	private static readonly (string Suffix, long Multiplier)[] FileSizeSuffixes =
	[
		("kib", 1024L),
		("mib", 1024L * 1024),
		("gib", 1024L * 1024 * 1024),
		("kb", 1024L),
		("mb", 1024L * 1024),
		("gb", 1024L * 1024 * 1024),
		("k", 1024L),
		("m", 1024L * 1024),
		("g", 1024L * 1024 * 1024)
	];

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

	private IReadOnlyList<Option> MaxFileSizeSymbols() =>
		_includeMaxFileBytes ? [MaxFileBytes] : [];

	private IEnumerable<(Option<bool?> Positive, Option<bool> Negative)> TogglePairs()
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
		Option<bool?> positive,
		Option<bool> negative)
	{
		if (parseResult.GetResult(negative) is { Implicit: false })
			return false;
		if (parseResult.GetResult(positive) is not { Implicit: false })
			return null;
		return parseResult.GetValue(positive) ?? true;
	}

	private static (Option<bool?> Positive, Option<bool> Negative) CreateToggle(
		string positiveName,
		string negativeName,
		string positiveDescriptionKey,
		string negativeDescriptionKey,
		LocalizationService localization)
	{
		var positive = new Option<bool?>(positiveName)
		{
			Description = localization[positiveDescriptionKey],
			HelpName = "on|off",
			Arity = ArgumentArity.ZeroOrOne
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

internal readonly record struct CliGitModeValue(
	GitFilteringMode Mode,
	string? DiffRange);
