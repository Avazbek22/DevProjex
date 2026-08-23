using System.CommandLine;
using System.CommandLine.Parsing;

namespace DevProjex.Terminal.CommandLine;

internal enum CliTextJsonFormat
{
	Text,
	Json
}

internal enum CliRecentKind
{
	All,
	Folder,
	Repository
}

internal readonly record struct CliExclusionValue(ProjectExclusion? Exclusion)
{
	public bool IsNone => Exclusion is null;
}

internal enum CliCompletionShell
{
	Bash,
	Zsh,
	Fish,
	Powershell
}

internal enum CliDeveloperScenario
{
	Standard,
	PreviewSearchRetention,
	ProjectMemoryLifecycle
}

internal enum CliProfileSource
{
	Invalid = -1,
	Auto,
	Standard,
	Local,
	Portable
}

internal readonly record struct CliProfileValue(
	CliProfileSource Source,
	string Value)
{
	public static CliProfileValue Parse(string value, bool allowAuto) =>
		value.ToLowerInvariant() switch
		{
			"auto" when allowAuto => new CliProfileValue(CliProfileSource.Auto, "auto"),
			"standard" => new CliProfileValue(CliProfileSource.Standard, "standard"),
			"local" => new CliProfileValue(CliProfileSource.Local, "local"),
			_ => new CliProfileValue(CliProfileSource.Portable, value)
		};

	public ProjectProfileReference Resolve(
		string projectPath,
		Execution.TerminalServices services) =>
		Source switch
		{
			CliProfileSource.Auto => services.LocalProfileStore.TryLoadProfile(projectPath, out _)
				? ProjectProfileReference.Local
				: ProjectProfileReference.Standard,
			CliProfileSource.Standard => ProjectProfileReference.Standard,
			CliProfileSource.Local => ProjectProfileReference.Local,
			CliProfileSource.Portable => new ProjectProfileReference(
				ProjectProfileSourceKind.Portable,
				Path.GetFullPath(Value)),
			_ => throw new ArgumentOutOfRangeException(nameof(Source), Source, null)
		};

	public override string ToString() => Value;
}

internal sealed class CliChoiceSet<T>(params CliChoiceSet<T>.Choice[] choices)
	where T : struct
{
	private readonly IReadOnlyDictionary<string, T> _values = choices.ToDictionary(
		static choice => choice.Token,
		static choice => choice.Value,
		StringComparer.OrdinalIgnoreCase);
	private readonly IReadOnlyDictionary<T, string> _tokens = choices.ToDictionary(
		static choice => choice.Value,
		static choice => choice.Token);

	public IReadOnlyList<string> Tokens { get; } =
		choices.Where(static choice => choice.IsVisible).Select(static choice => choice.Token).ToArray();

	public bool TryParse(string token, out T value) =>
		_values.TryGetValue(token, out value);

	public string ToToken(T value) =>
		_tokens.TryGetValue(value, out var token)
			? token
			: throw new ArgumentOutOfRangeException(nameof(value), value, null);

	public readonly record struct Choice(string Token, T Value, bool IsVisible = true);
}

internal static class CliChoiceSets
{
	public static CliChoiceSet<CliTextJsonFormat> TextJson { get; } = new(
		new("text", CliTextJsonFormat.Text),
		new("json", CliTextJsonFormat.Json));

	public static CliChoiceSet<CliRecentKind> RecentKind { get; } = new(
		new("all", CliRecentKind.All),
		new("folder", CliRecentKind.Folder),
		new("repository", CliRecentKind.Repository));

	public static CliChoiceSet<ProjectContextView> ContextView { get; } = new(
		ProjectPresentationCatalog.PreviewModes
			.OrderBy(static descriptor => descriptor.Order)
			.Select(static descriptor =>
				new CliChoiceSet<ProjectContextView>.Choice(descriptor.Token, descriptor.Id))
			.ToArray());

	public static CliChoiceSet<ProjectContextDocumentFormat> ContextDocumentFormat { get; } = new(
		ProjectPresentationCatalog.Formats
			.OrderBy(static descriptor => descriptor.Order)
			.Select(static descriptor =>
				new CliChoiceSet<ProjectContextDocumentFormat>.Choice(descriptor.Token, descriptor.Id))
			.ToArray());

	public static CliChoiceSet<ProjectCopyExportFormat> ProjectExportFormat { get; } = new(
		new("folder", ProjectCopyExportFormat.Folder),
		new("zip", ProjectCopyExportFormat.Zip));

	public static CliChoiceSet<TerminalScreenMode> ScreenMode { get; } = new(
		new("auto", TerminalScreenMode.Auto),
		new("alternate", TerminalScreenMode.Alternate),
		new("inline", TerminalScreenMode.Inline));

	public static CliChoiceSet<TerminalColorMode> ColorMode { get; } = new(
		new("auto", TerminalColorMode.Auto),
		new("always", TerminalColorMode.Always),
		new("never", TerminalColorMode.Never));

	public static CliChoiceSet<TerminalProgressMode> ProgressMode { get; } = new(
		new("auto", TerminalProgressMode.Auto),
		new("always", TerminalProgressMode.Always),
		new("never", TerminalProgressMode.Never));

	public static CliChoiceSet<TerminalVerbosity> Verbosity { get; } = new(
		new("normal", TerminalVerbosity.Normal),
		new("quiet", TerminalVerbosity.Quiet),
		new("minimal", TerminalVerbosity.Minimal),
		new("detailed", TerminalVerbosity.Detailed),
		new("diagnostic", TerminalVerbosity.Diagnostic));

	public static CliChoiceSet<DesktopPreviewView> DesktopView { get; } = new(
		ProjectPresentationCatalog.PreviewModes
			.OrderBy(static descriptor => descriptor.Order)
			.Select(static descriptor =>
				new CliChoiceSet<DesktopPreviewView>.Choice(
					descriptor.Token,
					ToDesktopPreviewView(descriptor.Id)))
			.ToArray());

	public static CliChoiceSet<ProjectContextDocumentFormat> TreeFormat { get; } =
		ContextDocumentFormat;

	public static CliChoiceSet<GitFilteringMode> GitMode { get; } = new(
		ProjectPresentationCatalog.GitFiltering
			.OrderBy(static descriptor => descriptor.Order)
			.Select(static descriptor =>
				new CliChoiceSet<GitFilteringMode>.Choice(descriptor.Token, descriptor.Id))
			.ToArray());

	public static CliChoiceSet<CliExclusionValue> Exclusion { get; } = new(
		[
			new(
				ProjectPresentationCatalog.NoExclusionsToken,
				new CliExclusionValue(null)),
			.. ProjectPresentationCatalog.Exclusions
				.OrderBy(static descriptor => descriptor.Order)
				.Select(static descriptor =>
					new CliChoiceSet<CliExclusionValue>.Choice(
						descriptor.Token,
						new CliExclusionValue(descriptor.Id))),
			new(
				ProjectPresentationCatalog.Get(ProjectExclusion.HideSecrets).Token,
				new CliExclusionValue(ProjectExclusion.HideSecrets),
				IsVisible: false)
		]);

	public static CliChoiceSet<CliCompletionShell> CompletionShell { get; } = new(
		new("bash", CliCompletionShell.Bash),
		new("zsh", CliCompletionShell.Zsh),
		new("fish", CliCompletionShell.Fish),
		new("powershell", CliCompletionShell.Powershell));

	public static CliChoiceSet<CliDeveloperScenario> DeveloperScenario { get; } = new(
		new("standard", CliDeveloperScenario.Standard),
		new("preview-search-retention", CliDeveloperScenario.PreviewSearchRetention),
		new("project-memory-lifecycle", CliDeveloperScenario.ProjectMemoryLifecycle));

	public static CliChoiceSet<AppLanguage> Language { get; } = new(
		new("en", AppLanguage.En),
		new("ru", AppLanguage.Ru),
		new("de", AppLanguage.De),
		new("fr", AppLanguage.Fr),
		new("it", AppLanguage.It),
		new("es", AppLanguage.Es),
		new("pt", AppLanguage.Pt),
		new("pt-pt", AppLanguage.PtPt),
		new("kk", AppLanguage.Kk),
		new("tg", AppLanguage.Tg),
		new("uz", AppLanguage.Uz));

	private static DesktopPreviewView ToDesktopPreviewView(ProjectContextView view) =>
		view switch
		{
			ProjectContextView.Tree => DesktopPreviewView.Tree,
			ProjectContextView.Content => DesktopPreviewView.Content,
			ProjectContextView.TreeContent => DesktopPreviewView.TreeContent,
			_ => throw new ArgumentOutOfRangeException(nameof(view), view, null)
		};
}

internal static class CliChoiceSymbols
{
	public static Option<CliProfileValue> ProfileOption(
		string description,
		string defaultValue,
		LocalizationService localization,
		bool allowAuto = false)
	{
		var profileTokens = allowAuto
			? new[] { "auto", "standard", "local" }
			: new[] { "standard", "local" };
		var option = new Option<CliProfileValue>("--profile")
		{
			Description = description,
			HelpName = $"{string.Join('|', profileTokens)}|FILE",
			DefaultValueFactory = _ => CliProfileValue.Parse(defaultValue, allowAuto),
			CustomParser = result =>
			{
				if (result.Tokens.Count == 1)
				{
					var token = result.Tokens[0].Value;
					if (token.Equals("auto", StringComparison.OrdinalIgnoreCase) &&
					    !allowAuto)
					{
						result.AddError(LocalizedParseError.Create(localization.Format(
							"Terminal.Validation.Choice",
							"--profile",
							"standard, local, FILE")));
						return new CliProfileValue(CliProfileSource.Invalid, string.Empty);
					}
					return CliProfileValue.Parse(token, allowAuto);
				}

				result.AddError(LocalizedParseError.Create(
					localization.Format("Terminal.Error.MissingValue", "--profile")));
				return new CliProfileValue(CliProfileSource.Invalid, string.Empty);
			}
		};
		option.CompletionSources.Add(profileTokens);
		option.CompletionSources.Add(context => FileSystemCompletionSource.Complete(
			context,
			FileSystemCompletionKind.FilesAndDirectories));
		return option;
	}

	public static Option<T> Option<T>(
		string name,
		string description,
		T defaultValue,
		CliChoiceSet<T> choices,
		LocalizationService localization)
		where T : struct
	{
		var option = CreateOption<T>(name, description, choices, localization);
		option.DefaultValueFactory = _ => defaultValue;
		return option;
	}

	public static Option<T> RequiredOption<T>(
		string name,
		string description,
		CliChoiceSet<T> choices,
		LocalizationService localization,
		params string[] aliases)
		where T : struct
	{
		var option = CreateOption<T>(name, description, choices, localization, aliases);
		option.Required = true;
		return option;
	}

	public static Option<T?> NullableOption<T>(
		string name,
		string description,
		CliChoiceSet<T> choices,
		LocalizationService localization)
		where T : struct
	{
		var option = new Option<T?>(name)
		{
			Description = description,
			HelpName = string.Join('|', choices.Tokens),
			CustomParser = result => Parse(result, name, choices, localization)
		};
		option.CompletionSources.Add(choices.Tokens.ToArray());
		return option;
	}

	public static Argument<T> Argument<T>(
		string name,
		CliChoiceSet<T> choices,
		LocalizationService localization)
		where T : struct
	{
		var argument = new Argument<T>(name)
		{
			Arity = ArgumentArity.ExactlyOne,
			HelpName = string.Join('|', choices.Tokens),
			CustomParser = result => Parse(result, name, choices, localization)
		};
		argument.CompletionSources.Add(choices.Tokens.ToArray());
		return argument;
	}

	private static Option<T> CreateOption<T>(
		string name,
		string description,
		CliChoiceSet<T> choices,
		LocalizationService localization,
		params string[] aliases)
		where T : struct
	{
		var option = new Option<T>(name, aliases)
		{
			Description = description,
			HelpName = string.Join('|', choices.Tokens),
			CustomParser = result => Parse(result, name, choices, localization)
		};
		option.CompletionSources.Add(choices.Tokens.ToArray());
		return option;
	}

	private static T Parse<T>(
		ArgumentResult result,
		string symbolName,
		CliChoiceSet<T> choices,
		LocalizationService localization)
		where T : struct
	{
		if (result.Tokens.Count == 1 &&
		    choices.TryParse(result.Tokens[0].Value, out var value))
		{
			return value;
		}

		if (result.Tokens.Count > 0)
		{
			result.AddError(LocalizedParseError.Create(localization.Format(
				"Terminal.Validation.Choice",
				symbolName,
				string.Join(", ", choices.Tokens))));
		}

		return default;
	}
}
