using DevProjex.Tests.Shared.StoreListing;

namespace DevProjex.Tests.Integration;

public sealed class StoreListingImportFolderIntegrationTests
{
    private static readonly Lazy<string> RepoRoot = new(StoreListingPaths.FindRepositoryRoot);

    [Fact]
    public void ImportFolder_IsReadyForPartnerCenterImport()
    {
        // This is the main end-to-end guard: if it fails, the current repository artifact
        // is no longer a trustworthy Partner Center import candidate.
        var report = StoreListingImportValidator.ValidateRepositoryImportFolder(RepoRoot.Value);

        Assert.False(report.HasErrors, string.Join(Environment.NewLine, report.Errors.Select(error => $"{error.Code}: {error.Message}")));
    }

    [Fact]
    public void ImportFolder_ContainsExactlyOneCsv_AndHasListingAssets()
    {
        var importFolder = StoreListingPaths.GetImportFolder(RepoRoot.Value);
        var document = StoreListingCsvDocument.Load(StoreListingPaths.GetImportCsvPath(RepoRoot.Value));
        var localeColumns = StoreListingPaths.GetLocaleColumns(document.Headers);
        var csvFiles = Directory.EnumerateFiles(importFolder, "*.csv", SearchOption.TopDirectoryOnly).ToArray();
        var screenshotFiles = Directory.EnumerateFiles(Path.Combine(importFolder, "Screenshots"), "*.png", SearchOption.AllDirectories).ToArray();
        var logoFiles = Directory.EnumerateFiles(Path.Combine(importFolder, "StoreAssets"), "*.png", SearchOption.TopDirectoryOnly).ToArray();

        Assert.Single(csvFiles);
        Assert.True(screenshotFiles.Length >= localeColumns.Length, "The import folder should contain at least one screenshot asset per locale.");
        Assert.Single(logoFiles);
    }

    [Fact]
    public void ImportFolder_UsesContiguousAndConsistentScreenshotSlotsAcrossLocales()
    {
        var document = StoreListingCsvDocument.Load(StoreListingPaths.GetImportCsvPath(RepoRoot.Value));
        var localeColumns = StoreListingPaths.GetLocaleColumns(document.Headers);
        HashSet<int>? referenceCoverage = null;

        foreach (var locale in localeColumns)
        {
            var coverage = Enumerable.Range(1, 30)
                .Select(index => document.RowsByField.GetValueOrDefault($"DesktopScreenshot{index}")?.GetValue(locale))
                .Select((value, zeroBasedIndex) => new { Index = zeroBasedIndex + 1, Value = value })
                .Where(item => !string.IsNullOrWhiteSpace(item.Value))
                .Select(item => item.Index)
                .ToArray();

            Assert.NotEmpty(coverage);

            // Slots must stay gap-free. A listing that references 1,2,4 usually means a manual
            // edit drifted away from the intended screenshot order.
            Assert.Equal(Enumerable.Range(1, coverage[^1]).ToArray(), coverage);

            if (referenceCoverage is null)
            {
                referenceCoverage = [.. coverage];
                continue;
            }

            Assert.True(referenceCoverage.SetEquals(coverage), $"Locale {locale} uses a different screenshot slot set.");
        }
    }

    [Fact]
    public void ImportFolder_PreservesTemplateLocalesAndAppendsNewListings()
    {
        var repositoryRoot = RepoRoot.Value;
        var importDocument = StoreListingCsvDocument.Load(StoreListingPaths.GetImportCsvPath(repositoryRoot));
        var templateDocument = StoreListingCsvDocument.Load(StoreListingPaths.FindLatestExportTemplateCsv(repositoryRoot));

        var importLocales = StoreListingPaths.GetLocaleColumns(importDocument.Headers);
        var templateLocales = StoreListingPaths.GetLocaleColumns(templateDocument.Headers);

        Assert.Equal(templateLocales, importLocales.Take(templateLocales.Length));
        Assert.Equal(
            ["es-es", "pt-br", "pt-pt", "pl-pl", "tr-tr", "uk-ua", "ja-jp", "ko-kr", "zh-cn", "zh-tw", "vi-vn", "id-id"],
            importLocales.Skip(templateLocales.Length));
    }

    [Fact]
    public void ImportFolder_HasNoDuplicateNamedFieldRows()
    {
        var document = StoreListingCsvDocument.Load(StoreListingPaths.GetImportCsvPath(RepoRoot.Value));
        var duplicateFields = document.Rows
            .Select(row => row.Field)
            .Where(field => !string.IsNullOrWhiteSpace(field))
            .GroupBy(field => field, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();

        Assert.Empty(duplicateFields);
    }

    [Fact]
    public void ImportFolder_CriticalLocalizedValuesAreTrimmed()
    {
        var document = StoreListingCsvDocument.Load(StoreListingPaths.GetImportCsvPath(RepoRoot.Value));
        var localeColumns = StoreListingPaths.GetLocaleColumns(document.Headers);

        foreach (var row in document.Rows)
        {
            if (!RequiresTrimmedLocalizedValues(row.Field))
            {
                continue;
            }

            foreach (var locale in localeColumns)
            {
                var value = row.GetValue(locale);
                if (string.IsNullOrEmpty(value))
                {
                    continue;
                }

                Assert.Equal(value.Trim(), value);
            }
        }
    }

    [Fact]
    public void ImportFolder_FeatureSummaryAdvertisesBothGitAwareFilteringModes()
    {
        var document = StoreListingCsvDocument.Load(StoreListingPaths.GetImportCsvPath(RepoRoot.Value));
        var feature = document.RowsByField["Feature7"];
        var expectedValues = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["en-us"] = "Smart Ignore, .gitignore, and Git-tracked mode",
            ["en"] = "Smart Ignore, .gitignore, and Git-tracked mode",
            ["ru"] = "Smart Ignore, .gitignore и режим отслеживаемых файлов",
            ["ru-ru"] = "Smart Ignore, .gitignore и режим отслеживаемых файлов",
            ["kk-kz"] = "Smart Ignore, .gitignore және қадағаланатын файлдар режимі",
            ["de-de"] = "Smart Ignore, .gitignore und Git-Tracked-Modus",
            ["it-it"] = "Smart Ignore, .gitignore e modalità file tracciati",
            ["tg-cyrl-tj"] = "Smart Ignore, .gitignore ва реҷаи файлҳои пайгиришаванда",
            ["uz-latn-uz"] = "Smart Ignore, .gitignore va kuzatilgan fayllar rejimi",
            ["fr-fr"] = "Smart Ignore, .gitignore et mode fichiers suivis par Git",
            ["es-es"] = "Smart Ignore, .gitignore y modo de archivos rastreados",
            ["pt-br"] = "Smart Ignore, .gitignore e modo de arquivos rastreados",
            ["pt-pt"] = "Smart Ignore, .gitignore e modo de ficheiros seguidos",
            ["pl-pl"] = "Smart Ignore, .gitignore i tryb plików śledzonych",
            ["tr-tr"] = "Smart Ignore, .gitignore ve izlenen dosyalar modu",
            ["uk-ua"] = "Smart Ignore, .gitignore і режим відстежуваних файлів",
            ["ja-jp"] = "Smart Ignore・.gitignore・Git 追跡ファイルモード",
            ["ko-kr"] = "Smart Ignore, .gitignore, Git 추적 파일 모드",
            ["zh-cn"] = "Smart Ignore、.gitignore 与 Git 跟踪文件模式",
            ["zh-tw"] = "Smart Ignore、.gitignore 與 Git 追蹤檔案模式",
            ["vi-vn"] = "Smart Ignore, .gitignore và chế độ tệp Git theo dõi",
            ["id-id"] = "Smart Ignore, .gitignore, dan mode berkas terlacak Git"
        };

        Assert.Equal(
            expectedValues.Keys,
            StoreListingPaths.GetLocaleColumns(document.Headers));
        foreach (var (locale, expectedValue) in expectedValues)
            Assert.Equal(expectedValue, feature.GetValue(locale));
    }

    [Fact]
    public void ImportFolder_UsesConsistentTgScreenshotNaming()
    {
        var importCsvPath = StoreListingPaths.GetImportCsvPath(RepoRoot.Value);
        var text = File.ReadAllText(importCsvPath);

        Assert.DoesNotContain("TJ.png", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("TG.png", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ImportFolder_KeywordBudgetStaysWithinPartnerCenterLimits()
    {
        var document = StoreListingCsvDocument.Load(StoreListingPaths.GetImportCsvPath(RepoRoot.Value));
        var localeColumns = StoreListingPaths.GetLocaleColumns(document.Headers);

        foreach (var locale in localeColumns)
        {
            // The "21 total words" rule is the specific Partner Center trap that already broke
            // real imports. It deserves a dedicated integration assertion on the real artifact.
            var terms = Enumerable.Range(1, 7)
                .Select(index => document.RowsByField[$"SearchTerm{index}"].GetValue(locale))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToArray();

            Assert.True(terms.All(term => term.Length <= 40), $"Locale {locale} has a keyword longer than 40 characters.");
            Assert.True(terms.Sum(StoreListingPaths.CountWords) <= 21, $"Locale {locale} exceeds the 21-word keyword budget.");
        }
    }

    private static bool RequiresTrimmedLocalizedValues(string field)
    {
        return field is "Title" or "ShortDescription" or "ReleaseNotes" or "StoreLogo300x300" ||
               field.StartsWith("Feature", StringComparison.Ordinal) ||
               field.StartsWith("SearchTerm", StringComparison.Ordinal) ||
               field.StartsWith("DesktopScreenshot", StringComparison.Ordinal);
    }
}
