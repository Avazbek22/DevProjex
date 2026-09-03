using System.Buffers.Binary;
using System.Security.Cryptography;
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
    public void ImportFolder_ContainsLocalizedGuiAndTuiMedia()
    {
        var repositoryRoot = RepoRoot.Value;
        var screenshotRoot = Path.Combine(StoreListingPaths.GetImportFolder(repositoryRoot), "Screenshots");
        var languageCodes = Directory
            .EnumerateFiles(Path.Combine(repositoryRoot, "Assets", "Localization"), "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .Select(static code => code!.ToUpperInvariant())
            .Order(StringComparer.Ordinal)
            .ToArray();

        string[] guiDirectories =
        [
            "1_Main",
            "2_Loaded_Project",
            "3_Tree_Preview",
            "4_Filter_Preview",
            "5_Tree_Preview_Settings"
        ];
        foreach (var directory in guiDirectories)
        {
            AssertLocalizedScreenshotSet(screenshotRoot, directory, languageCodes, requireUniqueImages: false);
        }

        string[] tuiDirectories =
        [
            "6_Terminal_Workspace",
            "7_Terminal_Command_Hints",
            "8_Terminal_Action_Palette",
            "9_Terminal_Markdown",
            "10_Terminal_JSON"
        ];
        foreach (var directory in tuiDirectories)
        {
            AssertLocalizedScreenshotSet(screenshotRoot, directory, languageCodes, requireUniqueImages: true);
        }

        var document = StoreListingCsvDocument.Load(StoreListingPaths.GetImportCsvPath(repositoryRoot));
        var localeColumns = StoreListingPaths.GetLocaleColumns(document.Headers);
        for (var index = 0; index < tuiDirectories.Length; index++)
        {
            var row = document.RowsByField[$"DesktopScreenshot{index + 6}"];
            foreach (var locale in localeColumns)
            {
                var languageCode = ResolveAppLanguageCode(locale, languageCodes);
                var expectedPath = $"ImportFolder/Screenshots/{tuiDirectories[index]}/{languageCode}.png";
                Assert.Equal(expectedPath, row.GetValue(locale));
            }
        }
    }

    private static void AssertLocalizedScreenshotSet(
        string screenshotRoot,
        string directory,
        IReadOnlyCollection<string> expectedLanguageCodes,
        bool requireUniqueImages)
    {
        var imagePaths = Directory
            .EnumerateFiles(Path.Combine(screenshotRoot, directory), "*.png")
            .ToArray();
        var actualCodes = imagePaths
            .Select(static imagePath => Path.GetFileNameWithoutExtension(imagePath)!)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expectedLanguageCodes, actualCodes);
        Assert.All(imagePaths, imagePath => AssertPngContract(
            imagePath,
            expectedWidth: 2048,
            expectedHeight: 1280,
            requireOpaquePixels: requireUniqueImages));
        if (!requireUniqueImages)
        {
            return;
        }

        Assert.Equal(
            imagePaths.Length,
            imagePaths
                .Select(static imagePath => File.ReadAllBytes(imagePath))
                .Select(static bytes => SHA256.HashData(bytes))
                .Select(static hash => Convert.ToHexString(hash))
                .Distinct(StringComparer.Ordinal)
                .Count());
    }

    private static string ResolveAppLanguageCode(string storeLocale, IReadOnlyCollection<string> supportedCodes)
    {
        var normalized = storeLocale.Replace('_', '-').ToUpperInvariant();
        if (supportedCodes.Contains(normalized))
        {
            return normalized;
        }

        var primary = normalized.Split('-')[0];
        Assert.Contains(primary, supportedCodes);
        return primary;
    }

    private static void AssertPngContract(
        string path,
        int expectedWidth,
        int expectedHeight,
        bool requireOpaquePixels)
    {
        Span<byte> header = stackalloc byte[26];
        using var stream = File.OpenRead(path);
        stream.ReadExactly(header);

        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, header[..8].ToArray());
        Assert.Equal(expectedWidth, BinaryPrimitives.ReadInt32BigEndian(header[16..20]));
        Assert.Equal(expectedHeight, BinaryPrimitives.ReadInt32BigEndian(header[20..24]));
        if (requireOpaquePixels)
        {
            var colorType = header[25];
            Assert.True(
                colorType is 2 or 3,
                $"{path} must use truecolor or indexed color, but its PNG color type is {colorType}.");
            if (colorType == 3)
                AssertIndexedPngIsOpaque(stream, path);
        }
    }

    private static void AssertIndexedPngIsOpaque(Stream stream, string path)
    {
        stream.Position = 8;
        Span<byte> chunkHeader = stackalloc byte[8];
        Span<byte> transparency = stackalloc byte[256];
        while (stream.Position + 12 <= stream.Length)
        {
            stream.ReadExactly(chunkHeader);
            var dataLength = BinaryPrimitives.ReadUInt32BigEndian(chunkHeader[..4]);
            var isTransparencyChunk = chunkHeader[4..].SequenceEqual("tRNS"u8);
            Assert.True(
                stream.Length - stream.Position >= dataLength + 4,
                $"{path} contains a truncated PNG chunk.");

            if (isTransparencyChunk)
            {
                Assert.InRange(dataLength, 1u, (uint)transparency.Length);
                var alphaValues = transparency[..checked((int)dataLength)];
                stream.ReadExactly(alphaValues);
                Assert.True(
                    alphaValues.IndexOfAnyExcept(byte.MaxValue) < 0,
                    $"{path} contains a non-opaque indexed palette entry.");
            }
            else
            {
                stream.Position += dataLength;
            }

            stream.Position += 4;
            if (chunkHeader[4..].SequenceEqual("IEND"u8))
                return;
        }

        Assert.Fail($"{path} does not contain a complete PNG end chunk.");
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
    public void ImportFolder_MatchesTemplateLocalesExactly()
    {
        // Since the 2026-09-03 import, every listing language exists in Partner Center,
        // so the exported template and the import CSV must carry the same locale columns
        // in the same order. A new language starts as an extra column appended after the
        // template columns and joins the template with the next fresh export.
        var repositoryRoot = RepoRoot.Value;
        var importDocument = StoreListingCsvDocument.Load(StoreListingPaths.GetImportCsvPath(repositoryRoot));
        var templateDocument = StoreListingCsvDocument.Load(StoreListingPaths.FindLatestExportTemplateCsv(repositoryRoot));

        var importLocales = StoreListingPaths.GetLocaleColumns(importDocument.Headers);
        var templateLocales = StoreListingPaths.GetLocaleColumns(templateDocument.Headers);

        Assert.Equal(templateLocales, importLocales);
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
