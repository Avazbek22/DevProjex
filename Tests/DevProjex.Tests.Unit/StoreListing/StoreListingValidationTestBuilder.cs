using DevProjex.Tests.Shared.StoreListing;

namespace DevProjex.Tests.Unit.StoreListing;

internal sealed class StoreListingValidationTestBuilder
{
    // The builder intentionally models the current CSV contract instead of a hypothetical
    // future abstraction. That keeps rule tests honest: they mutate the same field names
    // Partner Center exports today.
    private static readonly byte[] TinyPngBytes = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO7Zx7sAAAAASUVORK5CYII=");

    private static readonly string[] Headers =
    [
        "Field",
        "ID",
        "Type (Тип)",
        "default",
        .. StoreListingPaths.LocaleColumns
    ];

    private readonly Dictionary<string, Dictionary<string, string>> _rows = new(StringComparer.Ordinal)
    {
        ["Title"] = CreateRow("4", "Текст"),
        ["Description"] = CreateRow("2", "Текст"),
        ["ShortDescription"] = CreateRow("8", "Текст"),
        ["DesktopScreenshot1"] = CreateRow("100", "Относительный путь (или URL-адрес файла в Центре партнеров)"),
        ["DesktopScreenshot2"] = CreateRow("101", "Относительный путь (или URL-адрес файла в Центре партнеров)"),
        ["DesktopScreenshot3"] = CreateRow("102", "Относительный путь (или URL-адрес файла в Центре партнеров)"),
        ["DesktopScreenshot4"] = CreateRow("103", "Относительный путь (или URL-адрес файла в Центре партнеров)"),
        ["DesktopScreenshot5"] = CreateRow("104", "Относительный путь (или URL-адрес файла в Центре партнеров)"),
        ["StoreLogo300x300"] = CreateRow("602", "Относительный путь (или URL-адрес файла в Центре партнеров)"),
        ["SearchTerm1"] = CreateRow("900", "Текст"),
        ["SearchTerm2"] = CreateRow("901", "Текст"),
        ["SearchTerm3"] = CreateRow("902", "Текст"),
        ["SearchTerm4"] = CreateRow("903", "Текст"),
        ["SearchTerm5"] = CreateRow("904", "Текст"),
        ["SearchTerm6"] = CreateRow("905", "Текст"),
        ["SearchTerm7"] = CreateRow("906", "Текст")
    };

    private readonly string[] _defaultSearchTerms =
    [
        "project context",
        "project tree",
        "chatgpt code",
        "structure export",
        "ai codebase",
        "git viewer",
        "claude context"
    ];

    internal StoreListingValidationTestBuilder()
    {
        foreach (var locale in StoreListingPaths.LocaleColumns)
        {
            SetRowValue("Title", locale, "DevProjex");
            SetRowValue("Description", locale, "Multiline description line 1.\r\nLine 2 is intentionally present.");
            SetRowValue("ShortDescription", locale, "Short description");
            SetRowValue("StoreLogo300x300", locale, "ImportFolder/StoreAssets/StoreLogo300x300.png");

            for (var screenshot = 1; screenshot <= 5; screenshot++)
            {
                SetRowValue(
                    $"DesktopScreenshot{screenshot}",
                    locale,
                    $"ImportFolder/Screenshots/{screenshot}/{GetScreenshotCode(locale)}.png");
            }

            for (var index = 0; index < _defaultSearchTerms.Length; index++)
            {
                SetRowValue($"SearchTerm{index + 1}", locale, _defaultSearchTerms[index]);
            }
        }
    }

    internal StoreListingValidationTestBuilder WithValue(string field, string locale, string value)
    {
        // The tests intentionally mutate raw field/locale cells instead of using a higher-level
        // domain API. That makes it much harder for a future refactor to hide a broken CSV shape.
        SetRowValue(field, locale, value);
        return this;
    }

    internal StoreListingValidationTestBuilder UseConsistentScreenshotCoverage(params int[] slots)
    {
        var slotSet = slots.ToHashSet();

        foreach (var locale in StoreListingPaths.LocaleColumns)
        {
            for (var screenshot = 1; screenshot <= 5; screenshot++)
            {
                SetRowValue(
                    $"DesktopScreenshot{screenshot}",
                    locale,
                    slotSet.Contains(screenshot)
                        ? $"ImportFolder/Screenshots/{screenshot}/{GetScreenshotCode(locale)}.png"
                        : string.Empty);
            }
        }

        return this;
    }

    internal StoreListingValidationTestBuilder UseInconsistentScreenshotCoverage(string locale, params int[] slots)
    {
        // This helper is used to model the exact type of manual Partner Center mistake we want
        // to catch: one locale drifting away from the screenshot coverage used by the others.
        var slotSet = slots.ToHashSet();
        for (var screenshot = 1; screenshot <= 5; screenshot++)
        {
            SetRowValue(
                $"DesktopScreenshot{screenshot}",
                locale,
                slotSet.Contains(screenshot)
                    ? $"ImportFolder/Screenshots/{screenshot}/{GetScreenshotCode(locale)}.png"
                    : string.Empty);
        }

        return this;
    }

    internal StoreListingValidationTestBuilder RemoveValue(string field, string locale)
    {
        SetRowValue(field, locale, string.Empty);
        return this;
    }

    internal StoreListingValidationTestBuilder RemoveRow(string field)
    {
        _rows.Remove(field);
        return this;
    }

    internal StoreListingValidationTestBuilder ReorderRows(string firstField, string secondField)
    {
        var ordered = _rows.ToList();
        var firstIndex = ordered.FindIndex(item => item.Key.Equals(firstField, StringComparison.Ordinal));
        var secondIndex = ordered.FindIndex(item => item.Key.Equals(secondField, StringComparison.Ordinal));
        (ordered[firstIndex], ordered[secondIndex]) = (ordered[secondIndex], ordered[firstIndex]);

        _rows.Clear();
        foreach (var pair in ordered)
        {
            _rows.Add(pair.Key, pair.Value);
        }

        return this;
    }

    internal StoreListingValidationTestBuilder UseLegacyTjSuffix()
    {
        SetRowValue("DesktopScreenshot5", "tg-cyrl-tj", "ImportFolder/Screenshots/5/TJ.png");
        return this;
    }

    internal StoreListingValidationTestBuilder UseBackslashAssetPath()
    {
        SetRowValue("DesktopScreenshot1", "en-us", @"ImportFolder\Screenshots\1\EN-US.png");
        return this;
    }

    internal StoreListingValidationTestBuilder WithoutImportFolderPrefix()
    {
        SetRowValue("DesktopScreenshot1", "en-us", "Screenshots/1/EN-US.png");
        return this;
    }

    internal StoreListingValidationTestBuilder UseEscapingAssetPath()
    {
        SetRowValue("DesktopScreenshot1", "en-us", "ImportFolder/../Secrets/EN.png");
        return this;
    }

    internal StoreListingValidationTestBuilder UseKeywordBudgetOverflow()
    {
        var longTerms = new[]
        {
            "project context for ai",
            "project tree",
            "copy code for chatgpt",
            "export project structure",
            "codebase for ai chat",
            "git repository viewer",
            "context for claude"
        };

        for (var index = 0; index < longTerms.Length; index++)
        {
            SetRowValue($"SearchTerm{index + 1}", "en-us", longTerms[index]);
        }

        return this;
    }

    internal StoreListingValidationTestBuilder UseLongKeyword()
    {
        SetRowValue("SearchTerm1", "en-us", "this keyword phrase is intentionally longer than forty chars");
        return this;
    }

    internal StoreListingValidationTestBuilder CorruptPngAsset(string relativePath)
    {
        // Some validation errors are not about CSV text at all, but about a referenced PNG
        // being unreadable. Keep this hook so the asset validator has a direct negative test.
        _corruptedAssetRelativePaths.Add(relativePath.Replace('\\', '/'));
        return this;
    }

    private readonly HashSet<string> _corruptedAssetRelativePaths = new(StringComparer.OrdinalIgnoreCase);

    internal (TemporaryDirectory TempDirectory, string ImportFolderPath, string ImportCsvPath, string TemplateCsvPath) Build(
        bool withUtf8Bom = false,
        bool withLfOnly = false)
    {
        var tempDirectory = new TemporaryDirectory();
        var importFolderPath = tempDirectory.CreateFolder("Packaging/Windows/StoreListing/ImportFolder");
        var templateCsvPath = tempDirectory.CreateFile(
            "Packaging/Windows/StoreListing/listingData-template.csv",
            BuildCsvText(useTemplateValues: true, withLfOnly: withLfOnly));
        var importCsvPath = Path.Combine(importFolderPath, "listingData.csv");

        WriteText(importCsvPath, BuildCsvText(useTemplateValues: false, withLfOnly: withLfOnly), withUtf8Bom);
        WriteAssets(importFolderPath);

        return (tempDirectory, importFolderPath, importCsvPath, templateCsvPath);
    }

    private static Dictionary<string, string> CreateRow(string id, string type)
    {
        var row = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Field"] = string.Empty,
            ["ID"] = id,
            ["Type (Тип)"] = type,
            ["default"] = string.Empty
        };

        foreach (var locale in StoreListingPaths.LocaleColumns)
        {
            row[locale] = string.Empty;
        }

        return row;
    }

    private void SetRowValue(string field, string locale, string value)
    {
        _rows[field]["Field"] = field;
        _rows[field][locale] = value;
    }

    private string BuildCsvText(bool useTemplateValues, bool withLfOnly)
    {
        var lineEnding = withLfOnly ? "\n" : "\r\n";
        var builder = new StringBuilder();
        builder.Append(string.Join(",", Headers));
        builder.Append(lineEnding);

        foreach (var rowPair in _rows)
        {
            var row = rowPair.Value;
            row["Field"] = rowPair.Key;

            var values = Headers.Select(header =>
            {
                var value = row.TryGetValue(header, out var cell) ? cell : string.Empty;
                if (useTemplateValues && header == "default")
                {
                    value = string.Empty;
                }

                return EscapeCsv(value);
            });

            builder.Append(string.Join(",", values));
            builder.Append(lineEnding);
        }

        return builder.ToString();
    }

    private static string EscapeCsv(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\r') || value.Contains('\n'))
        {
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        return value;
    }

    private static void WriteText(string path, string content, bool withUtf8Bom)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(withUtf8Bom));
    }

    private void WriteAssets(string importFolderPath)
    {
        var logoPath = Path.Combine(importFolderPath, "StoreAssets", "StoreLogo300x300.png");
        Directory.CreateDirectory(Path.GetDirectoryName(logoPath)!);
        File.WriteAllBytes(logoPath, TinyPngBytes);

        foreach (var locale in StoreListingPaths.LocaleColumns)
        {
            var screenshotFileName = GetScreenshotCode(locale) + ".png";
            for (var index = 1; index <= 5; index++)
            {
                var screenshotPath = Path.Combine(importFolderPath, "Screenshots", index.ToString(CultureInfo.InvariantCulture), screenshotFileName);
                Directory.CreateDirectory(Path.GetDirectoryName(screenshotPath)!);
                var relativePath = $"Screenshots/{index.ToString(CultureInfo.InvariantCulture)}/{screenshotFileName}";

                // We keep every slot physically present on disk by default because many tests
                // validate the CSV coverage independently from the file inventory.
                // The CSV may stop referencing a slot, but the test fixture should still be able
                // to simulate stale or extra files without rebuilding the whole folder shape.
                File.WriteAllBytes(screenshotPath, _corruptedAssetRelativePaths.Contains(relativePath) ? "not-a-png"u8.ToArray() : TinyPngBytes);
            }
        }
    }

    private static string GetScreenshotCode(string locale)
    {
        return locale switch
        {
            "en-us" or "en" => "EN",
            "ru" or "ru-ru" => "RU",
            "kk-kz" => "KK",
            "de-de" => "DE",
            "it-it" => "IT",
            "tg-cyrl-tj" => "TG",
            "uz-latn-uz" => "UZ",
            "fr-fr" => "FR",
            _ => locale.ToUpperInvariant()
        };
    }
}
