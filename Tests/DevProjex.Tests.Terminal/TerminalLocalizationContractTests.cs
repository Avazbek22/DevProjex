using System.Text.RegularExpressions;

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
		"Terminal.Exit.Runtime",
		"Terminal.Doctor.current-directory"
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
}
