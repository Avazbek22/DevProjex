using System.Text.RegularExpressions;
using DevProjex.Application.Presentation;

namespace DevProjex.Tests.Terminal;

public sealed partial class TerminalLocalizationContractTests
{
	private static readonly string[] Locales =
	[
		"en",
		"ru",
		"de",
		"fr",
		"it",
		"es",
		"pt",
		"pt-pt",
		"kk",
		"tg",
		"uz"
	];

	private static readonly string[] NativeTranslationKeys =
	[
		"Terminal.Command.Root",
		"Terminal.Command.Analyze",
		"Terminal.Option.Language",
		"Terminal.Error.Unexpected",
		"Terminal.Error.ParserRejected",
		"Terminal.Analysis.Size",
		"Terminal.Tui.Welcome.Description",
		"Terminal.Tui.ExportSummary",
		"Terminal.Tui.DryRunReady",
		"Terminal.Tui.Help",
		"Terminal.Tui.Footer.Preview",
		"Terminal.Tui.Recent.StorageUnavailable",
		"Terminal.Tui.ProfileInvalidRecovery",
		"Terminal.Tui.Progress.CopyingFiles",
		"Terminal.Tui.Progress.CancelHint",
		"Terminal.Tui.Command.Set.Description",
		"Terminal.Tui.Command.Error.UnknownToken",
		"Terminal.Exit.Runtime",
		"Terminal.Doctor.current-directory",
		"Content.Classification.Binary"
	];

	[Fact]
	public void EveryLocale_HasTheCompleteTerminalCatalog()
	{
		var catalogs = ReadCatalogs();
		var expectedKeys = catalogs["en"].Keys
			.Where(static key => key.StartsWith("Terminal.", StringComparison.Ordinal))
			.ToHashSet(StringComparer.Ordinal);

		Assert.NotEmpty(expectedKeys);
		foreach (var locale in Locales)
		{
			var actual = catalogs[locale]
				.Where(static entry => entry.Key.StartsWith("Terminal.", StringComparison.Ordinal))
				.ToDictionary(StringComparer.Ordinal);

			Assert.Equal(expectedKeys.Order(), actual.Keys.Order());
			Assert.All(actual, entry =>
			{
				Assert.False(string.IsNullOrWhiteSpace(entry.Value), $"{entry.Key} is empty in {locale}.json.");
				Assert.DoesNotContain("[[", entry.Value, StringComparison.Ordinal);
			});
		}
	}

	[Fact]
	public void EveryTerminalLocalizationKeyReferencedBySourceExistsInAllLocales()
	{
		var catalogs = ReadCatalogs();
		var repositoryRoot = FindRepositoryRoot();
		var sourceDirectories = new[]
		{
			Path.Combine(repositoryRoot, "Apps", "Terminal"),
			Path.Combine(repositoryRoot, "Application", "Presentation")
		};
		var sourceKeys = sourceDirectories
			.SelectMany(static directory => Directory.EnumerateFiles(
				directory,
				"*.cs",
				SearchOption.AllDirectories))
			.SelectMany(path => SourceLocalizationKeyRegex()
				.Matches(File.ReadAllText(path))
				.Select(static match => match.Groups[1].Value))
			.Distinct(StringComparer.Ordinal)
			.Order(StringComparer.Ordinal)
			.ToArray();

		Assert.NotEmpty(sourceKeys);
		foreach (var locale in Locales)
		{
			var missing = sourceKeys
				.Where(key => !catalogs[locale].ContainsKey(key))
				.ToArray();
			Assert.Empty(missing);
		}
	}

	[Fact]
	public void SharedPresentationDescriptorsHaveLocalizedLabelsAndStableUserFormats()
	{
		var catalogs = ReadCatalogs();
		var labelKeys = ProjectPresentationCatalog.GitFiltering
			.Select(static descriptor => descriptor.LabelKey)
			.Concat(ProjectPresentationCatalog.Exclusions.Select(static descriptor => descriptor.LabelKey))
			.Concat(ProjectPresentationCatalog.ContentTransformations.Select(static descriptor => descriptor.LabelKey))
			.Concat(ProjectPresentationCatalog.PreviewModes.Select(static descriptor => descriptor.LabelKey))
			.Concat(FileContentClassificationCatalog.All.Select(static descriptor => descriptor.LabelKey))
			.Distinct(StringComparer.Ordinal)
			.ToArray();

		foreach (var locale in Locales)
		{
			Assert.All(labelKeys, key =>
			{
				Assert.True(catalogs[locale].ContainsKey(key), $"{key} is missing in {locale}.json.");
				Assert.DoesNotContain("[[", catalogs[locale][key], StringComparison.Ordinal);
			});
		}

		Assert.Equal(
			["ASCII", "JSON", "XML", "Markdown"],
			ProjectPresentationCatalog.Formats.Select(static descriptor => descriptor.UserLabel));
	}

	[Fact]
	public void WorkspaceCommandCatalogAndContextHelpAreCompleteInEveryLocale()
	{
		var catalogs = ReadCatalogs();
		var commandKeys = TerminalWorkspaceCommandCatalog.All
			.SelectMany(static definition => new[]
			{
				definition.TitleKey,
				definition.DescriptionKey,
				definition.SchemaKey
			})
			.Append("Terminal.Tui.Command.Help.OverlayTitle")
			.Append("Terminal.Tui.Command.Error.Similar")
			.Distinct(StringComparer.Ordinal)
			.ToArray();
		var contextualHelpKeys = new[]
		{
			"Terminal.Tui.Help.Tree",
			"Terminal.Tui.Help.Preview",
			"Terminal.Tui.Help.Controls"
		};

		foreach (var (locale, catalog) in catalogs)
		{
			Assert.All(commandKeys, key =>
			{
				Assert.True(catalog.TryGetValue(key, out var value), $"{key} is missing in {locale}.json.");
				Assert.False(string.IsNullOrWhiteSpace(value), $"{key} is empty in {locale}.json.");
			});
			Assert.All(contextualHelpKeys, key =>
				Assert.Contains(":", catalog[key], StringComparison.Ordinal));
		}
	}

	[Fact]
	public void EveryLocale_PreservesCompositeFormatPlaceholders()
	{
		var catalogs = ReadCatalogs();
		var english = catalogs["en"];

		foreach (var locale in Locales)
		{
			foreach (var (key, expectedValue) in english.Where(static entry =>
				         entry.Key.StartsWith("Terminal.", StringComparison.Ordinal)))
			{
				var expected = PlaceholderRegex().Matches(expectedValue).Select(static match => match.Value).Distinct().Order();
				var actual = PlaceholderRegex().Matches(catalogs[locale][key]).Select(static match => match.Value).Distinct().Order();
				Assert.Equal(expected, actual);
			}
		}
	}

	[Fact]
	public void NonEnglishLocales_DoNotUseEnglishFallbacksForHumanText()
	{
		var catalogs = ReadCatalogs();
		var english = catalogs["en"];

		foreach (var locale in Locales.Where(static locale => locale != "en"))
		{
			foreach (var key in NativeTranslationKeys)
				Assert.NotEqual(english[key], catalogs[locale][key]);
		}
	}

	[Fact]
	public void CompactFooterText_FitsTheMinimumSupportedViewport()
	{
		var catalogs = ReadCatalogs();
		var workspaceFooterKeys = new[]
		{
			"Terminal.Tui.Footer.Tree",
			"Terminal.Tui.Footer.Preview",
			"Terminal.Tui.Footer.Controls"
		};

		foreach (var (locale, catalog) in catalogs)
		{
			Assert.True(
				catalog["Terminal.Tui.Footer.Welcome"].Length <= 76,
				$"The compact Welcome footer does not fit 80 columns in {locale}.");
			foreach (var key in workspaceFooterKeys)
			{
				Assert.True(
					catalog[key].Length <= 80,
					$"{key} does not fit 80 columns in {locale}.");
			}
		}
	}

	[Fact]
	public void OrdinaryTuiCatalogUsesDesktopTerminologyWithoutPresentationOrProfileJargon()
	{
		var catalogs = ReadCatalogs();
		foreach (var catalog in catalogs.Values)
		{
			Assert.DoesNotContain("Terminal.Tui.Preview.Readable", catalog.Keys);
			Assert.DoesNotContain("Terminal.Tui.Preview.Raw", catalog.Keys);
			Assert.DoesNotContain("Terminal.Tui.Action.Presentation", catalog.Keys);
			Assert.DoesNotContain("Terminal.Tui.Action.Presentation.Description", catalog.Keys);
			Assert.DoesNotContain("Terminal.Tui.InternalCachePath", catalog.Keys);
		}

		Assert.Equal("Settings", catalogs["en"]["Terminal.Tui.Profile"]);
		Assert.Equal("Settings file:", catalogs["en"]["Terminal.Tui.ProfileFile"]);
		Assert.Equal("Параметры", catalogs["ru"]["Terminal.Tui.Profile"]);
		Assert.Equal("Файл параметров:", catalogs["ru"]["Terminal.Tui.ProfileFile"]);
		Assert.Equal("Project folder", catalogs["en"]["Terminal.Tui.SourceReference"]);
		Assert.Equal("Папка проекта", catalogs["ru"]["Terminal.Tui.SourceReference"]);
	}

	[Fact]
	public void ContentProcessingTitleAndStatusUseOneSharedDesktopAndTuiContract()
	{
		var catalogs = ReadCatalogs();
		foreach (var (locale, catalog) in catalogs)
		{
			Assert.True(
				catalog.TryGetValue("Settings.Secrets.Title", out var title) &&
				!string.IsNullOrWhiteSpace(title),
				$"Settings.Secrets.Title is missing in {locale}.json.");
			Assert.False(
				catalog.ContainsKey("Settings.ContentProcessing.Title"),
				$"{locale}.json contains a second content-processing title contract.");
			Assert.False(string.IsNullOrWhiteSpace(catalog["Settings.Secrets.Status.Failed"]));
			Assert.False(string.IsNullOrWhiteSpace(catalog["Settings.Secrets.Status.Retry"]));
			Assert.False(string.IsNullOrWhiteSpace(catalog["Settings.Ignore.HideSecrets.NoMatches"]));
			Assert.Contains("{0}", catalog["Settings.Secrets.Status.Applied"], StringComparison.Ordinal);
			Assert.Contains("{1}", catalog["Settings.Secrets.Status.Applied"], StringComparison.Ordinal);
			Assert.Contains("{0}", catalog["Settings.Secrets.Status.SizeLimit"], StringComparison.Ordinal);
			Assert.Contains("{1}", catalog["Settings.Secrets.Status.SizeLimit"], StringComparison.Ordinal);
			Assert.Contains("{0}", catalog["Settings.Secrets.Status.FailedFiles"], StringComparison.Ordinal);
			Assert.False(string.IsNullOrWhiteSpace(catalog["Settings.Ignore.StripBlankLines"]));
			Assert.False(string.IsNullOrWhiteSpace(catalog["Settings.BlankLines.Status.Scanning"]));
			Assert.Contains("{0}", catalog["Settings.BlankLines.Status.Applied"], StringComparison.Ordinal);
			Assert.Contains("{1}", catalog["Settings.BlankLines.Status.Applied"], StringComparison.Ordinal);
			Assert.False(string.IsNullOrWhiteSpace(catalog["Settings.BlankLines.Status.NothingToStrip"]));
			Assert.False(string.IsNullOrWhiteSpace(catalog["Settings.BlankLines.Status.Failed"]));
			Assert.Equal(3, catalog["Preview.Secret.Redacted.Tooltip"].Split('\n').Length);
			Assert.Equal(4, catalog["Preview.Secret.Kept.Tooltip"].Split('\n').Length);
		}

		Assert.Equal("Content processing:", catalogs["en"]["Settings.Secrets.Title"]);
		Assert.Equal("Обработка содержимого:", catalogs["ru"]["Settings.Secrets.Title"]);
		Assert.Equal("Убирать пустые строки", catalogs["ru"]["Settings.Ignore.StripBlankLines"]);
		Assert.Equal("DevProjex не нашёл секреты", catalogs["ru"]["Settings.Ignore.HideSecrets.NoMatches"]);
		Assert.Equal("Найдено: {0}. Скрыто: {1}.", catalogs["ru"]["Settings.Secrets.Status.Applied"]);
		Assert.Equal("Не удалось завершить анализ.", catalogs["ru"]["Settings.Secrets.Status.Failed"]);
		Assert.Equal(
			"Не удалось проверить файлов: {0}.",
			catalogs["ru"]["Settings.Secrets.Status.FailedFiles"]);
		Assert.Equal(
			"Нажмите, чтобы повторить проверку.",
			catalogs["ru"]["Settings.Secrets.Status.Retry"]);
		Assert.Equal(
			"Файлы больше {1} МиБ не проверены: {0}.",
			catalogs["ru"]["Settings.Secrets.Status.SizeLimit"]);
	}

	private static Dictionary<string, Dictionary<string, string>> ReadCatalogs()
	{
		var directory = Path.Combine(FindRepositoryRoot(), "Assets", "Localization");
		return Locales.ToDictionary(
			static locale => locale,
			locale => ReadCatalog(Path.Combine(directory, $"{locale}.json")),
			StringComparer.Ordinal);
	}

	private static Dictionary<string, string> ReadCatalog(string path)
	{
		using var document = JsonDocument.Parse(File.ReadAllText(path));
		return document.RootElement.EnumerateObject().ToDictionary(
			static property => property.Name,
			static property => property.Value.GetString() ?? string.Empty,
			StringComparer.Ordinal);
	}

	private static string FindRepositoryRoot()
		=> PublishedApplicationLocator.FindRepositoryRoot();

	[GeneratedRegex(@"\{\d+\}", RegexOptions.CultureInvariant)]
	private static partial Regex PlaceholderRegex();

	[GeneratedRegex("\"((?:Terminal|Content)\\.[A-Za-z0-9_.-]+)\"", RegexOptions.CultureInvariant)]
	private static partial Regex SourceLocalizationKeyRegex();
}
