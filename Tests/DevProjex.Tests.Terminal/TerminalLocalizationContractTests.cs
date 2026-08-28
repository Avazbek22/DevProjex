using System.Globalization;
using System.Text.RegularExpressions;
using DevProjex.Application.Presentation;
using DevProjex.Infrastructure.ResourceStore;
using Terminal.Gui.Text;

namespace DevProjex.Tests.Terminal;

public sealed partial class TerminalLocalizationContractTests
{
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
		foreach (var locale in catalogs.Keys)
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
		foreach (var locale in catalogs.Keys)
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

		foreach (var locale in catalogs.Keys)
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
	public void EveryLocale_PreservesPlaceholdersAndLineStructure()
	{
		var catalogs = ReadCatalogs();
		var english = catalogs["en"];

		foreach (var locale in catalogs.Keys)
		{
			foreach (var (key, expectedValue) in english)
			{
				var actualValue = catalogs[locale][key];
				var expectedPlaceholders = GetPlaceholderMultiset(expectedValue);
				var actualPlaceholders = GetPlaceholderMultiset(actualValue);
				Assert.True(
					expectedPlaceholders.SequenceEqual(actualPlaceholders, StringComparer.Ordinal),
					$"Placeholder mismatch in {locale}.json/{key}: " +
					$"expected [{string.Join(", ", expectedPlaceholders)}], " +
					$"actual [{string.Join(", ", actualPlaceholders)}].");
				var expectedLineShape = GetLineShape(expectedValue);
				var actualLineShape = GetLineShape(actualValue);
				Assert.True(
					string.Equals(expectedLineShape, actualLineShape, StringComparison.Ordinal),
					$"Line-break structure mismatch in {locale}.json/{key}: " +
					$"expected {expectedLineShape}, actual {actualLineShape}.");
			}
		}
	}

	[Fact]
	public void EveryLocalizedHelpPreservesNumberedSectionsAndBulletMarkup()
	{
		var helpDirectory = Path.Combine(FindRepositoryRoot(), "Assets", "HelpContent");
		var paths = Directory.GetFiles(helpDirectory, "help.*.txt", SearchOption.TopDirectoryOnly)
			.Order(StringComparer.Ordinal)
			.ToArray();
		var english = File.ReadAllText(Path.Combine(helpDirectory, "help.en.txt"));
		var expectedHeadings = GetNumberedHelpHeadings(english);
		var expectedResetDataBulletCount = GetNumberedHelpBulletCount(english, "16.3");

		foreach (var path in paths)
		{
			var source = Path.GetFileName(path);
			var help = File.ReadAllText(path);
			var actualHeadings = GetNumberedHelpHeadings(help);
			Assert.True(
				expectedHeadings.SequenceEqual(actualHeadings, StringComparer.Ordinal),
				$"Numbered help headings differ in {source}: " +
				$"expected [{string.Join(", ", expectedHeadings)}], " +
				$"actual [{string.Join(", ", actualHeadings)}].");
			var actualResetDataBulletCount = GetNumberedHelpBulletCount(help, "16.3");
			Assert.True(
				expectedResetDataBulletCount == actualResetDataBulletCount,
				$"Reset Data help bullet count differs in {source}: " +
				$"expected {expectedResetDataBulletCount}, actual {actualResetDataBulletCount}.");
			Assert.DoesNotMatch(MalformedHelpBulletRegex(), help);
			Assert.DoesNotContain(",?", help, StringComparison.Ordinal);

			var plainText = HelpContentProvider.ToPlainText(help);
			Assert.DoesNotMatch(LiteralHelpAsteriskRegex(), plainText);
		}
	}

	[Fact]
	public void LocalizationResources_DoNotContainUnicodeFormatControls()
	{
		var violations = new List<string>();
		foreach (var (locale, catalog) in ReadCatalogs())
		{
			foreach (var (key, value) in catalog)
				AddFormatControlViolations(value, $"{locale}.json/{key}", violations);
		}

		var helpDirectory = Path.Combine(FindRepositoryRoot(), "Assets", "HelpContent");
		foreach (var path in Directory.GetFiles(helpDirectory, "help.*.txt", SearchOption.TopDirectoryOnly))
			AddFormatControlViolations(File.ReadAllText(path), Path.GetFileName(path), violations);

		Assert.True(
			violations.Count == 0,
			$"Unicode format controls are not allowed in localized resources:{Environment.NewLine}" +
			string.Join(Environment.NewLine, violations));
	}

	[Fact]
	public void NonEnglishLocales_DoNotUseEnglishFallbacksForHumanText()
	{
		var catalogs = ReadCatalogs();
		var english = catalogs["en"];

		foreach (var locale in catalogs.Keys.Where(static locale => locale != "en"))
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
			var welcomeSegments = WelcomeFooterSegmentSeparatorRegex()
				.Split(catalog["Terminal.Tui.Footer.Welcome"])
				.Where(static segment => !string.IsNullOrWhiteSpace(segment))
				.ToArray();
			Assert.True(
				welcomeSegments.Length == 5,
				$"The compact Welcome footer must contain five separated shortcut/action pairs in {locale}.");
			Assert.True(
				catalog["Terminal.Tui.Footer.Welcome"].GetColumns() <= 76,
				$"The compact Welcome footer does not fit 80 columns in {locale}.");
			foreach (var key in workspaceFooterKeys)
			{
				Assert.True(
					catalog[key].GetColumns() <= 80,
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
	public void LocalizationText_PreservesTypographyAndBrandContracts()
	{
		var catalogs = ReadCatalogs();
		var punctuationViolations = new List<string>();
		foreach (var (locale, catalog) in catalogs)
		{
			foreach (var (key, value) in catalog)
			{
				if (!string.Equals(locale, "fr", StringComparison.Ordinal)
					&& ForbiddenSpaceBeforePunctuationRegex().IsMatch(RemoveTechnicalLiterals(value)))
				{
					punctuationViolations.Add($"{locale}.json/{key}");
				}

				AssertBalancedParentheses(value, $"{locale}.json/{key}");
				Assert.DoesNotMatch(DuplicateBrandRegex(), value);
			}

			Assert.Contains(
				"DevProjex",
				catalog["Help.About.Body"],
				StringComparison.Ordinal);
		}

		var helpDirectory = Path.Combine(FindRepositoryRoot(), "Assets", "HelpContent");
		foreach (var path in Directory.GetFiles(helpDirectory, "help.*.txt", SearchOption.TopDirectoryOnly))
		{
			var help = File.ReadAllText(path);
			var locale = Path.GetFileNameWithoutExtension(path)["help.".Length..];
			if (!string.Equals(locale, "fr", StringComparison.Ordinal)
				&& ForbiddenSpaceBeforePunctuationRegex().IsMatch(RemoveTechnicalLiterals(help)))
			{
				punctuationViolations.Add(Path.GetFileName(path));
			}

			AssertBalancedParentheses(
				NumberedMarkerRegex().Replace(help, string.Empty),
				Path.GetFileName(path));
			Assert.DoesNotMatch(DuplicateBrandRegex(), help);
			Assert.Contains("DevProjex", help, StringComparison.Ordinal);
		}

		Assert.True(
			punctuationViolations.Count == 0,
			$"Forbidden punctuation spacing:{Environment.NewLine}{string.Join(Environment.NewLine, punctuationViolations)}");
	}

	[Theory]
	[InlineData("devprojex analyze .")]
	[InlineData("devprojex export context . -o context.md")]
	[InlineData("devprojex mcp --root .")]
	public void LocalizationTypography_AllowsCurrentDirectoryCommandArguments(string value)
	{
		Assert.DoesNotMatch(ForbiddenSpaceBeforePunctuationRegex(), RemoveTechnicalLiterals(value));
	}

	[Fact]
	public void EveryLocalePreservesExecutableQuickStartAndHelpCommands()
	{
		foreach (var (locale, catalog) in ReadCatalogs())
		{
			var quickStart = catalog["Terminal.Tui.Welcome.QuickStart"];
			Assert.Contains("devprojex analyze .", quickStart, StringComparison.Ordinal);
			Assert.Contains(
				"devprojex export context . -o context.md",
				quickStart,
				StringComparison.Ordinal);
		}

		var helpDirectory = Path.Combine(FindRepositoryRoot(), "Assets", "HelpContent");
		foreach (var path in Directory.GetFiles(helpDirectory, "help.*.txt", SearchOption.TopDirectoryOnly))
		{
			var help = File.ReadAllText(path);
			Assert.Contains(":language", help, StringComparison.Ordinal);
			Assert.Matches("(?m)^MD\\s*[:：]$", help.ReplaceLineEndings("\n"));
			Assert.Contains("`Root: ...`", help, StringComparison.Ordinal);
			Assert.Contains(
				"`devprojex export context . --format markdown -o ../devprojex-context.md`",
				help,
				StringComparison.Ordinal);
			Assert.Contains(
				"`devprojex export project . --as folder -o ../devprojex-submission`",
				help,
				StringComparison.Ordinal);
			Assert.Contains(
				"`devprojex export project . --as zip -o ../devprojex-submission.zip`",
				help,
				StringComparison.Ordinal);
		}
	}

	[Theory]
	[InlineData("word .")]
	[InlineData("{0} .")]
	[InlineData("Cloning {0} ...")]
	public void LocalizationTypography_RejectsDetachedPunctuation(string value)
	{
		Assert.Matches(ForbiddenSpaceBeforePunctuationRegex(), RemoveTechnicalLiterals(value));
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
		return Directory.GetFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
			.Order(StringComparer.Ordinal)
			.ToDictionary(
				static path => Path.GetFileNameWithoutExtension(path),
				ReadCatalog,
				StringComparer.Ordinal);
	}

	private static void AssertBalancedParentheses(string value, string source)
	{
		var asciiOpen = value.Count(static character => character == '(');
		var asciiClose = value.Count(static character => character == ')');
		var fullWidthOpen = value.Count(static character => character == '（');
		var fullWidthClose = value.Count(static character => character == '）');
		Assert.True(asciiOpen == asciiClose, $"Unbalanced ASCII parentheses in {source}.");
		Assert.True(fullWidthOpen == fullWidthClose, $"Unbalanced full-width parentheses in {source}.");
	}

	private static string[] GetPlaceholderMultiset(string value) =>
		PlaceholderRegex().Matches(value)
			.Select(static match => match.Value)
			.Order(StringComparer.Ordinal)
			.ToArray();

	private static string GetLineShape(string value) =>
		string.Concat(
			value.ReplaceLineEndings("\n")
				.Split('\n')
				.Select(static line => line.Length == 0 ? '0' : '1'));

	private static string[] GetNumberedHelpHeadings(string help) =>
		NumberedHelpHeadingRegex().Matches(help)
			.Select(static match => match.Groups[1].Value)
			.ToArray();

	private static int GetNumberedHelpBulletCount(string help, string sectionNumber)
	{
		var headings = NumberedHelpHeadingRegex().Matches(help);
		for (var index = 0; index < headings.Count; index++)
		{
			var heading = headings[index];
			if (!string.Equals(heading.Groups[1].Value, sectionNumber, StringComparison.Ordinal))
				continue;
			var sectionEnd = index + 1 < headings.Count
				? headings[index + 1].Index
				: help.Length;
			var section = help[heading.Index..sectionEnd];
			return HelpBulletRegex().Count(section);
		}

		throw new InvalidOperationException($"Help section {sectionNumber} is missing.");
	}

	private static void AddFormatControlViolations(
		string value,
		string source,
		ICollection<string> violations)
	{
		var controls = value.EnumerateRunes()
			.Where(static rune => Rune.GetUnicodeCategory(rune) == UnicodeCategory.Format)
			.Select(static rune => $"U+{rune.Value:X4}")
			.Distinct(StringComparer.Ordinal)
			.ToArray();
		if (controls.Length > 0)
			violations.Add($"{source}: {string.Join(", ", controls)}");
	}

	private static string RemoveTechnicalLiterals(string value) =>
		CurrentDirectoryCommandRegex().Replace(
			CommandVariadicSchemaRegex().Replace(
				StandaloneCommandLineShortcutRegex().Replace(
					StandaloneHelpShortcutRegex().Replace(
						WorkspaceCommandTokenRegex().Replace(
							WorkspaceCommandListRegex().Replace(
								InlineCodeRegex().Replace(value, "literal"),
								"commands"),
							"command"),
						"shortcut"),
					"command-line"),
				"arguments"),
			"path");

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

	[GeneratedRegex(@"\{[^{}\r\n]+\}", RegexOptions.CultureInvariant)]
	private static partial Regex PlaceholderRegex();

	[GeneratedRegex(@"(?m)^#{2,3}\s+(\d+(?:\.\d+)*)(?=\)|\s)", RegexOptions.CultureInvariant)]
	private static partial Regex NumberedHelpHeadingRegex();

	[GeneratedRegex(@"(?m)^\s*\*(?!\s)", RegexOptions.CultureInvariant)]
	private static partial Regex MalformedHelpBulletRegex();

	[GeneratedRegex(@"(?m)^\s*\*", RegexOptions.CultureInvariant)]
	private static partial Regex LiteralHelpAsteriskRegex();

	[GeneratedRegex(@"(?m)^\s*\*\s", RegexOptions.CultureInvariant)]
	private static partial Regex HelpBulletRegex();

	[GeneratedRegex(@"\s{2,}", RegexOptions.CultureInvariant)]
	private static partial Regex WelcomeFooterSegmentSeparatorRegex();

	[GeneratedRegex(@" (?=[,:;!?\)]|\.\.\.|\.(?:\s|$))", RegexOptions.CultureInvariant)]
	private static partial Regex ForbiddenSpaceBeforePunctuationRegex();

	[GeneratedRegex(@"DevProjex\s+DevProjex", RegexOptions.CultureInvariant)]
	private static partial Regex DuplicateBrandRegex();

	[GeneratedRegex(@"`[^`\r\n]+`", RegexOptions.CultureInvariant)]
	private static partial Regex InlineCodeRegex();

	[GeneratedRegex(@"(?<!\S)\?(?=\s|\p{L}|-)", RegexOptions.CultureInvariant)]
	private static partial Regex StandaloneHelpShortcutRegex();

	[GeneratedRegex(@"(?<!\S):(?=\s|\p{L})", RegexOptions.CultureInvariant)]
	private static partial Regex StandaloneCommandLineShortcutRegex();

	[GeneratedRegex(@"\bdevprojex(?:\s+(?:[a-z][a-z-]*|--[a-z][a-z-]*)){1,5}\s+\.(?=\s|$)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
	private static partial Regex CurrentDirectoryCommandRegex();

	[GeneratedRegex(@"(?<=>) \.\.\.", RegexOptions.CultureInvariant)]
	private static partial Regex CommandVariadicSchemaRegex();

	[GeneratedRegex(@"(?<!\S):[a-z][a-z-]*", RegexOptions.CultureInvariant)]
	private static partial Regex WorkspaceCommandTokenRegex();

	[GeneratedRegex(@"\(:[a-z][a-z-]*(?:,\s*:[a-z][a-z-]*)+\)", RegexOptions.CultureInvariant)]
	private static partial Regex WorkspaceCommandListRegex();

	[GeneratedRegex(@"(?m)^\s*(?:#{1,6}\s*)?\d+\)", RegexOptions.CultureInvariant)]
	private static partial Regex NumberedMarkerRegex();

	[GeneratedRegex("\"((?:Terminal|Content)\\.[A-Za-z0-9_.-]+)\"", RegexOptions.CultureInvariant)]
	private static partial Regex SourceLocalizationKeyRegex();
}
