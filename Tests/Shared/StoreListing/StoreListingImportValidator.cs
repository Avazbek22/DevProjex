using System.Globalization;

namespace DevProjex.Tests.Shared.StoreListing;

internal static class StoreListingImportValidator
{
    // This validator intentionally encodes Partner Center behavior that was learned
    // the hard way from real failed imports. The point is not to validate "some CSV",
    // but to validate the exact import artifact shape that DevProjex publishes.

    internal static StoreListingValidationReport ValidateRepositoryImportFolder(
        string repositoryRoot,
        StoreListingValidationOptions? options = null)
    {
        var importFolderPath = StoreListingPaths.GetImportFolder(repositoryRoot);
        var importCsvPath = StoreListingPaths.GetImportCsvPath(repositoryRoot);
        var templateCsvPath = StoreListingPaths.FindLatestExportTemplateCsv(repositoryRoot);

        return ValidateImportFolder(importFolderPath, importCsvPath, templateCsvPath, options ?? new StoreListingValidationOptions());
    }

    internal static StoreListingValidationReport ValidateImportFolder(
        string importFolderPath,
        string importCsvPath,
        string? templateCsvPath,
        StoreListingValidationOptions options)
    {
        var report = new StoreListingValidationReport();

        ValidateDirectoryShape(importFolderPath, importCsvPath, report);
        ValidateCsvEncoding(importCsvPath, options, report);

        var importDocument = StoreListingCsvDocument.Load(importCsvPath);
        StoreListingCsvDocument? templateDocument = null;

        if (options.RequireTemplateSchemaMatch && !string.IsNullOrWhiteSpace(templateCsvPath) && File.Exists(templateCsvPath))
        {
            templateDocument = StoreListingCsvDocument.Load(templateCsvPath);
        }

        // Locale columns are derived from the actual CSV header instead of being frozen
        // in code. That makes the validator forward-compatible with future language
        // additions, while still enforcing a couple of baseline locales below.
        var localeColumns = StoreListingPaths.GetLocaleColumns(importDocument.Headers);

        ValidateHeaderShape(importDocument, report);
        ValidateHeaders(importDocument, templateDocument, report);
        ValidateSchemaAndRowOrder(importDocument, templateDocument, report);
        ValidateDuplicateFieldRows(importDocument, report);
        ValidateRequiredLocaleColumns(localeColumns, report);
        ValidateRequiredFields(importDocument, localeColumns, report);
        ValidateTrimmedCriticalValues(importDocument, localeColumns, report);
        ValidateKeywordBudget(importDocument, localeColumns, report);
        ValidateAssetPaths(importDocument, localeColumns, importFolderPath, options, report);

        return report;
    }

    private static void ValidateDirectoryShape(
        string importFolderPath,
        string importCsvPath,
        StoreListingValidationReport report)
    {
        if (!Directory.Exists(importFolderPath))
        {
            report.AddError("SLI001", $"Import folder was not found: {importFolderPath}");
            return;
        }

        var csvFiles = Directory.EnumerateFiles(importFolderPath, "*.csv", SearchOption.TopDirectoryOnly).ToArray();
        if (csvFiles.Length != 1)
        {
            report.AddError("SLI002", $"Import folder must contain exactly one CSV file. Found: {csvFiles.Length}");
        }

        if (!File.Exists(importCsvPath))
        {
            report.AddError("SLI003", $"Import CSV was not found: {importCsvPath}");
        }
    }

    private static void ValidateCsvEncoding(
        string importCsvPath,
        StoreListingValidationOptions options,
        StoreListingValidationReport report)
    {
        var bytes = File.ReadAllBytes(importCsvPath);

        // Partner Center proved to be extremely picky about CSV encoding.
        // We keep this rule explicit because a future editor may save the file with BOM
        // and the import can start failing without a useful error message.
        if (options.RequireUtf8WithoutBom &&
            bytes.Length >= 3 &&
            bytes[0] == 0xEF &&
            bytes[1] == 0xBB &&
            bytes[2] == 0xBF)
        {
            report.AddError("SLI004", "Import CSV must be saved as UTF-8 without BOM.");
        }

        if (!options.RequireCrLfLineEndings)
        {
            return;
        }

        var text = File.ReadAllText(importCsvPath, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true));

        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '\n' && (index == 0 || text[index - 1] != '\r'))
            {
                report.AddError("SLI005", "Import CSV must use CRLF line endings only.");
                break;
            }
        }
    }

    private static void ValidateHeaders(
        StoreListingCsvDocument importDocument,
        StoreListingCsvDocument? templateDocument,
        StoreListingValidationReport report)
    {
        // The latest export template is the safest source of truth.
        // If Microsoft inserts, removes, or renames a column, we want a hard failure here
        // instead of a mysterious Partner Center rejection later.
        var expectedHeaders = templateDocument?.Headers
                              ?? ["Field", "ID", "Type (Тип)", "default", .. StoreListingPaths.LocaleColumns];

        if (!importDocument.Headers.SequenceEqual(expectedHeaders, StringComparer.Ordinal))
        {
            report.AddError("SLI006", "Import CSV header does not match the Partner Center export template.");
        }
    }

    private static void ValidateHeaderShape(StoreListingCsvDocument importDocument, StoreListingValidationReport report)
    {
        // Partner Center header mistakes are painful to diagnose because import errors are often vague.
        // We validate the header shape explicitly so issues like duplicate locale columns or accidental
        // blank columns fail with a precise message instead of bubbling up as "processing failed".
        foreach (var header in importDocument.Headers)
        {
            if (string.IsNullOrWhiteSpace(header))
            {
                report.AddError("SLI023", "Import CSV contains an empty header column.");
            }
        }

        var duplicateHeaders = importDocument.Headers
            .GroupBy(header => header, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();

        foreach (var duplicateHeader in duplicateHeaders)
        {
            report.AddError("SLI024", $"Import CSV contains a duplicate header column: '{duplicateHeader}'.");
        }
    }

    private static void ValidateSchemaAndRowOrder(
        StoreListingCsvDocument importDocument,
        StoreListingCsvDocument? templateDocument,
        StoreListingValidationReport report)
    {
        if (templateDocument is null)
        {
            return;
        }

        var importFields = importDocument.Rows.Select(row => row.Field).ToArray();
        var templateFields = templateDocument.Rows.Select(row => row.Field).ToArray();

        if (!importFields.SequenceEqual(templateFields, StringComparer.Ordinal))
        {
            report.AddError("SLI007", "Import CSV row order drifted away from the exported Partner Center template.");
        }
    }

    private static void ValidateDuplicateFieldRows(StoreListingCsvDocument importDocument, StoreListingValidationReport report)
    {
        var duplicates = importDocument.Rows
            .Select(row => row.Field)
            .Where(field => !string.IsNullOrWhiteSpace(field))
            .GroupBy(field => field, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();

        foreach (var duplicate in duplicates)
        {
            report.AddError("SLI025", $"Import CSV contains a duplicate field row: '{duplicate}'.");
        }
    }

    private static void ValidateRequiredLocaleColumns(IReadOnlyList<string> localeColumns, StoreListingValidationReport report)
    {
        if (localeColumns.Count == 0)
        {
            report.AddError("SLI008", "No locale columns were found in the import CSV.");
            return;
        }

        foreach (var locale in StoreListingPaths.CoreLocaleColumns)
        {
            // The project may add more locales over time, but these baseline columns are
            // expected to remain present because the current release automation relies on them.
            if (!localeColumns.Contains(locale, StringComparer.Ordinal))
            {
                report.AddError("SLI008", $"Locale column is missing: {locale}");
            }
        }
    }

    private static void ValidateRequiredFields(
        StoreListingCsvDocument importDocument,
        IReadOnlyList<string> localeColumns,
        StoreListingValidationReport report)
    {
        var requiredFields = new[] { "Title", "Description", "ShortDescription", "DesktopScreenshot1" };

        foreach (var field in requiredFields)
        {
            if (!importDocument.RowsByField.TryGetValue(field, out var row))
            {
                report.AddError("SLI009", $"Required field row is missing: {field}");
                continue;
            }

            foreach (var locale in localeColumns)
            {
                if (string.IsNullOrWhiteSpace(row.GetValue(locale)))
                {
                    report.AddError("SLI010", $"Field '{field}' is empty for locale '{locale}'.");
                }
            }
        }
    }

    private static void ValidateKeywordBudget(
        StoreListingCsvDocument importDocument,
        IReadOnlyList<string> localeColumns,
        StoreListingValidationReport report)
    {
        foreach (var locale in localeColumns)
        {
            // SearchTerm rows are optional individually, but if present they must respect
            // the Partner Center UI limits and the hidden total-word constraint.
            var searchTerms = Enumerable.Range(1, 7)
                .Select(index => importDocument.RowsByField.GetValueOrDefault($"SearchTerm{index}")?.GetValue(locale) ?? string.Empty)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToArray();

            if (searchTerms.Length > 7)
            {
                report.AddError("SLI011", $"Locale '{locale}' exceeds the 7 keyword limit.");
            }

            foreach (var term in searchTerms)
            {
                if (term.Length > 40)
                {
                    report.AddError("SLI012", $"Locale '{locale}' has a keyword longer than 40 characters: '{term}'.");
                }
            }

            // Partner Center applies a less obvious rule on top of the per-term limit:
            // all keyword phrases together may contain at most 21 separate words.
            // This is the exact regression that made the import fail late in the process.
            var totalWordCount = searchTerms.Sum(StoreListingPaths.CountWords);
            if (totalWordCount > 21)
            {
                report.AddError("SLI013", $"Locale '{locale}' exceeds the 21-word keyword budget ({totalWordCount}).");
            }
        }
    }

    private static void ValidateTrimmedCriticalValues(
        StoreListingCsvDocument importDocument,
        IReadOnlyList<string> localeColumns,
        StoreListingValidationReport report)
    {
        // Leading/trailing whitespace is easy to miss in CSV reviews and can silently break
        // path resolution, search-term limits, or presentation quality. We only validate the
        // fields where extra whitespace is almost certainly accidental.
        foreach (var row in importDocument.Rows)
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

                if (!string.Equals(value, value.Trim(), StringComparison.Ordinal))
                {
                    report.AddError("SLI026", $"Field '{row.Field}' contains leading or trailing whitespace for locale '{locale}'.");
                }
            }
        }
    }

    private static bool RequiresTrimmedLocalizedValues(string field)
    {
        if (field.Equals("Title", StringComparison.Ordinal) ||
            field.Equals("ShortDescription", StringComparison.Ordinal) ||
            field.Equals("ReleaseNotes", StringComparison.Ordinal) ||
            field.Equals("StoreLogo300x300", StringComparison.Ordinal))
        {
            return true;
        }

        return field.StartsWith("Feature", StringComparison.Ordinal) ||
               field.StartsWith("SearchTerm", StringComparison.Ordinal) ||
               field.StartsWith("DesktopScreenshot", StringComparison.Ordinal);
    }

    private static void ValidateAssetPaths(
        StoreListingCsvDocument importDocument,
        IReadOnlyList<string> localeColumns,
        string importFolderPath,
        StoreListingValidationOptions options,
        StoreListingValidationReport report)
    {
        // Partner Center exports many DesktopScreenshotN rows even if only the first few are used.
        // We validate every present row, but screenshot coverage is checked separately so future
        // releases can move from 5 screenshots to 3 or 6 without rewriting the validator.
        var screenshotRows = Enumerable.Range(1, 30)
            .Select(index => importDocument.RowsByField.GetValueOrDefault($"DesktopScreenshot{index}"))
            .Where(row => row is not null)
            .Cast<StoreListingCsvRow>()
            .ToArray();

        ValidateScreenshotCoverage(screenshotRows, localeColumns, options, report);

        foreach (var row in screenshotRows)
        {
            foreach (var locale in localeColumns)
            {
                var assetValue = row.GetValue(locale);

                if (string.IsNullOrWhiteSpace(assetValue))
                {
                    continue;
                }

                ValidateSingleAssetPath(assetValue, row.Field, locale, importFolderPath, options, report);
            }
        }

        if (importDocument.RowsByField.TryGetValue("StoreLogo300x300", out var logoRow))
        {
            foreach (var locale in localeColumns)
            {
                var assetValue = logoRow.GetValue(locale);
                if (string.IsNullOrWhiteSpace(assetValue))
                {
                    continue;
                }

                ValidateSingleAssetPath(assetValue, logoRow.Field, locale, importFolderPath, options, report);
            }
        }
    }

    private static void ValidateScreenshotCoverage(
        IReadOnlyList<StoreListingCsvRow> screenshotRows,
        IReadOnlyList<string> localeColumns,
        StoreListingValidationOptions options,
        StoreListingValidationReport report)
    {
        // Screenshot validation must stay flexible.
        // "Exactly 5" is not a real business rule; the real requirement is:
        //   1. every locale has at least N screenshots
        //   2. locales use the same screenshot slot set
        //   3. screenshot slots stay contiguous from 1 without gaps
        //
        // This keeps the tests future-proof if the listing grows from 5 screenshots
        // to 6 or shrinks to 3, while still catching broken per-language coverage.
        var screenshotIndices = screenshotRows
            .Select(row => int.Parse(row.Field["DesktopScreenshot".Length..], CultureInfo.InvariantCulture))
            .OrderBy(index => index)
            .ToArray();

        if (screenshotIndices.Length == 0)
        {
            report.AddError("SLI014", "No DesktopScreenshot rows were found in the import CSV.");
            return;
        }

        HashSet<int>? referenceCoverage = null;
        string? referenceLocale = null;

        foreach (var locale in localeColumns)
        {
            var coverage = screenshotRows
                .Select(row => new
                {
                    Index = int.Parse(row.Field["DesktopScreenshot".Length..], CultureInfo.InvariantCulture),
                    Value = row.GetValue(locale)
                })
                .Where(item => !string.IsNullOrWhiteSpace(item.Value))
                .Select(item => item.Index)
                .OrderBy(index => index)
                .ToArray();

            if (coverage.Length < options.MinimumScreenshotsPerLocale)
            {
                report.AddError("SLI014", $"Locale '{locale}' has only {coverage.Length} screenshots, but the minimum is {options.MinimumScreenshotsPerLocale}.");
            }

            if (options.RequireContiguousScreenshotSlots && coverage.Length > 0)
            {
                var expected = Enumerable.Range(1, coverage[^1]).ToArray();
                if (!coverage.SequenceEqual(expected))
                {
                    report.AddError("SLI021", $"Locale '{locale}' uses non-contiguous screenshot slots ({string.Join(", ", coverage)}).");
                }
            }

            if (!options.RequireConsistentScreenshotCoverage)
            {
                continue;
            }

            if (referenceCoverage is null)
            {
                referenceCoverage = [.. coverage];
                referenceLocale = locale;
                continue;
            }

            if (!referenceCoverage.SetEquals(coverage))
            {
                report.AddError(
                    "SLI022",
                    $"Locale '{locale}' uses screenshot slots [{string.Join(", ", coverage)}], which does not match locale '{referenceLocale}' [{string.Join(", ", referenceCoverage.OrderBy(value => value))}].");
            }
        }
    }

    private static void ValidateSingleAssetPath(
        string assetValue,
        string field,
        string locale,
        string importFolderPath,
        StoreListingValidationOptions options,
        StoreListingValidationReport report)
    {
        if (Uri.TryCreate(assetValue, UriKind.Absolute, out var assetUri) &&
            (assetUri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
             assetUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            // Partner Center exports may preserve already-uploaded asset URLs.
            // Those URLs are still valid references and should not be forced back
            // into the local ImportFolder/... path format.
            return;
        }

        if (assetValue.Contains('\\', StringComparison.Ordinal))
        {
            report.AddError("SLI015", $"Asset path must use forward slashes only: {field}/{locale}");
        }

        if (assetValue.Contains("..", StringComparison.Ordinal))
        {
            report.AddError("SLI016", $"Asset path may not escape the import folder: {field}/{locale}");
        }

        if (assetValue.Contains("TJ.png", StringComparison.OrdinalIgnoreCase))
        {
            // The locale code remains tg-cyrl-tj, but the screenshot asset suffix must be TG.
            // Keeping this explicit prevents the same inconsistency from silently returning later.
            report.AddError("SLI017", $"Legacy TJ screenshot suffix is not allowed anymore: {field}/{locale}");
        }

        if (options.RequireImportFolderPrefixInAssetPaths &&
            !assetValue.StartsWith("ImportFolder/", StringComparison.Ordinal))
        {
            report.AddError("SLI018", $"Asset path must start with 'ImportFolder/': {field}/{locale}");
            return;
        }

        var fullAssetPath = StoreListingPaths.NormalizeAssetPath(importFolderPath, assetValue);

        if (!File.Exists(fullAssetPath))
        {
            report.AddError("SLI019", $"Asset file does not exist for {field}/{locale}: {assetValue}");
            return;
        }

        if (Path.GetExtension(fullAssetPath).Equals(".png", StringComparison.OrdinalIgnoreCase) &&
            !PngFileProbe.TryReadInfo(fullAssetPath, out _))
        {
            report.AddError("SLI020", $"PNG asset is invalid or unreadable for {field}/{locale}: {assetValue}");
        }
    }
}
