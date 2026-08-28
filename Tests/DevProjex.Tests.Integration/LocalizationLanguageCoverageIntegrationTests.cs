using System.Xml.Linq;

namespace DevProjex.Tests.Integration;

public sealed class LocalizationLanguageCoverageIntegrationTests
{
    [Fact]
    public void EveryAppLanguage_HasCompleteInterfaceAndHelpResources()
    {
        var catalog = new JsonLocalizationCatalog();
        var helpProvider = new HelpContentProvider();
        var englishKeys = catalog.Get(AppLanguage.En).Keys.Order(StringComparer.Ordinal).ToArray();

        foreach (var language in Enum.GetValues<AppLanguage>())
        {
            var localized = catalog.Get(language);
            Assert.Equal(englishKeys, localized.Keys.Order(StringComparer.Ordinal));
            Assert.All(localized, pair => Assert.False(
                string.IsNullOrWhiteSpace(pair.Value),
                $"Localization value is empty: {language}/{pair.Key}"));

            var help = helpProvider.GetHelpBody(language);
            Assert.Contains("## 1)", help, StringComparison.Ordinal);
            Assert.Contains("## 19)", help, StringComparison.Ordinal);
            Assert.Contains("`devprojex --help`", help, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void EveryAppLanguage_ResolvesItsOwnEmbeddedResources()
    {
        var root = FindRepositoryRoot();
        var catalog = new JsonLocalizationCatalog();
        var helpProvider = new HelpContentProvider(DesktopPlatform.Windows);

        foreach (var language in Enum.GetValues<AppLanguage>())
        {
            var code = AppLanguageUtility.ToCode(language);
            var localizationPath = Path.Combine(root, "Assets", "Localization", $"{code}.json");
            var expectedLocalization = JsonSerializer.Deserialize<Dictionary<string, string>>(
                File.ReadAllText(localizationPath))!;
            var actualLocalization = catalog.Get(language);

            Assert.Equal(expectedLocalization.Count, actualLocalization.Count);
            foreach (var (key, expectedValue) in expectedLocalization)
            {
                Assert.True(
                    actualLocalization.TryGetValue(key, out var actualValue),
                    $"{code}.json/{key} was not loaded for {language}.");
                Assert.Equal(expectedValue, actualValue);
            }

            var helpPath = Path.Combine(root, "Assets", "HelpContent", $"help.{code}.txt");
            var expectedHelp = DesktopShortcutTextFormatter.Format(
                File.ReadAllText(helpPath),
                DesktopPlatform.Windows);
            Assert.Equal(expectedHelp, helpProvider.GetHelpBody(language));
        }
    }

    [Theory]
    [InlineData(AppLanguage.Es, "Aplicar configuración", "Acceso denegado")]
    [InlineData(AppLanguage.Pt, "Aplicar configurações", "Acesso negado")]
    [InlineData(AppLanguage.PtPt, "Aplicar definições", "Acesso negado")]
    public void NewLanguages_UseLocalizedCriticalUiText(
        AppLanguage language,
        string applyLabel,
        string accessDeniedText)
    {
        var localized = new JsonLocalizationCatalog().Get(language);

        Assert.Equal(applyLabel, localized["Settings.Apply"]);
        Assert.Contains(accessDeniedText, localized["Msg.AccessDeniedRoot"], StringComparison.Ordinal);
        Assert.DoesNotContain("Apply settings", localized.Values);
    }

    [Theory]
    [InlineData(AppLanguage.En, "Exclusions:")]
    [InlineData(AppLanguage.Ru, "Исключения:")]
    [InlineData(AppLanguage.De, "Ausschlüsse:")]
    [InlineData(AppLanguage.Fr, "Exclusions :")]
    [InlineData(AppLanguage.It, "Esclusioni:")]
    [InlineData(AppLanguage.Es, "Exclusiones:")]
    [InlineData(AppLanguage.Pt, "Exclusões:")]
    [InlineData(AppLanguage.PtPt, "Exclusões:")]
    [InlineData(AppLanguage.Kk, "Ерекшеліктер:")]
    [InlineData(AppLanguage.Tg, "Истисноҳо:")]
    [InlineData(AppLanguage.Uz, "Istisnolar:")]
    public void ExclusionSection_UsesLocalizedNounLabel(
        AppLanguage language,
        string expectedLabel)
    {
        var localized = new JsonLocalizationCatalog().Get(language);

        Assert.Equal(expectedLabel, localized["Settings.IgnoreTitle"]);
        Assert.Equal(expectedLabel, localized["Settings.Ignore"]);
    }

    [Fact]
    public void WindowsPackage_DeclaresSpanishAndPortugueseResources()
    {
        var root = FindRepositoryRoot();
        var packageRoot = Path.Combine(root, "Packaging", "Windows", "DevProjex.Store");
        var manifest = XDocument.Load(Path.Combine(packageRoot, "Package.appxmanifest"));
        XNamespace packageNamespace = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
        var languages = manifest
            .Descendants(packageNamespace + "Resource")
            .Select(static resource => (string?)resource.Attribute("Language"))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains("es-es", languages);
        Assert.Contains("pt-br", languages);
        Assert.Contains("pt-pt", languages);
        AssertStoreResource(packageRoot, "es-ES", "Herramienta de solo lectura");
        AssertStoreResource(packageRoot, "pt-BR", "Ferramenta somente leitura");
        AssertStoreResource(packageRoot, "pt-PT", "Ferramenta só de leitura");
    }

    [Fact]
    public void LanguageMenu_UsesAudienceOrder()
    {
        var root = FindRepositoryRoot();
        var file = Path.Combine(
            root,
            "Apps",
            "Avalonia",
            "Views",
            "TopMenuBarView.axaml");
        var document = XDocument.Load(file);
        var englishItem = document
            .Descendants()
            .Single(element => (string?)element.Attribute("Name") == "LanguageEnMenuItem");
        var languageItems = englishItem.Parent!
            .Elements()
            .Select(element => new
            {
                Name = (string?)element.Attribute("Name"),
                Header = (string?)element.Attribute("Header")
            })
            .ToArray();

        Assert.Equal(
            [
                "LanguageEnMenuItem",
                "LanguageRuMenuItem",
                "LanguageEsMenuItem",
                "LanguagePtMenuItem",
                "LanguagePtPtMenuItem",
                "LanguageDeMenuItem",
                "LanguageFrMenuItem",
                "LanguageItMenuItem",
                "LanguageTgMenuItem",
                "LanguageUzMenuItem",
                "LanguageKkMenuItem",
                "LanguageZhCnMenuItem",
                "LanguageZhTwMenuItem",
                "LanguageJaMenuItem",
                "LanguageKoMenuItem",
                "LanguageTrMenuItem",
                "LanguageUkMenuItem",
                "LanguagePlMenuItem",
                "LanguageViMenuItem",
                "LanguageIdMenuItem"
            ],
            languageItems.Select(static item => item.Name));
        Assert.Equal(20, languageItems.Length);
        Assert.Equal("Español", languageItems[2].Header);
        Assert.Equal("Português (Brasil)", languageItems[3].Header);
        Assert.Equal("Português (Portugal)", languageItems[4].Header);
    }

    private static void AssertStoreResource(string packageRoot, string culture, string expectedDescription)
    {
        var file = Path.Combine(packageRoot, "Strings", culture, "Resources.resw");
        var document = XDocument.Load(file);
        var description = document
            .Descendants("data")
            .Single(element => string.Equals(
                (string?)element.Attribute("name"),
                "AppDescription",
                StringComparison.Ordinal))
            .Element("value")?.Value;

        Assert.Contains(expectedDescription, description, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = AppContext.BaseDirectory;
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory, "DevProjex.sln")))
                return directory;

            directory = Directory.GetParent(directory)?.FullName;
        }

        throw new InvalidOperationException("Repository root not found.");
    }
}
