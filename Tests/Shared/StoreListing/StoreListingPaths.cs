namespace DevProjex.Tests.Shared.StoreListing;

internal static class StoreListingPaths
{
    internal static readonly string[] MetadataColumns =
    [
        "Field",
        "ID",
        "Type (Тип)",
        "default"
    ];

    // Only the truly baseline locales stay hardcoded.
    // The validator derives the rest of the locale set from the actual CSV header
    // so adding a future language does not require rewriting validation code first.
    internal static readonly string[] CoreLocaleColumns =
    [
        "en-us",
        "ru-ru"
    ];

    internal const string ImportFolderRelativePath = "Packaging/Windows/StoreListing/ImportFolder";
    internal const string ImportCsvRelativePath = "Packaging/Windows/StoreListing/ImportFolder/listingData.csv";
    internal const string StoreListingRootRelativePath = "Packaging/Windows/StoreListing";

    internal static readonly string[] LocaleColumns =
    [
        "en-us",
        "en",
        "ru",
        "ru-ru",
        "kk-kz",
        "de-de",
        "it-it",
        "tg-cyrl-tj",
        "uz-latn-uz",
        "fr-fr"
    ];

    internal static string FindRepositoryRoot()
    {
        var directory = AppContext.BaseDirectory;

        while (!string.IsNullOrWhiteSpace(directory))
        {
            if (Directory.Exists(Path.Combine(directory, ".git")) ||
                File.Exists(Path.Combine(directory, "DevProjex.sln")))
            {
                return directory;
            }

            directory = Directory.GetParent(directory)?.FullName;
        }

        throw new InvalidOperationException("Repository root was not found.");
    }

    internal static string GetImportFolder(string repositoryRoot)
    {
        return CombineRelativePath(repositoryRoot, ImportFolderRelativePath);
    }

    internal static string GetImportCsvPath(string repositoryRoot)
    {
        return CombineRelativePath(repositoryRoot, ImportCsvRelativePath);
    }

    internal static string GetStoreListingRoot(string repositoryRoot)
    {
        return CombineRelativePath(repositoryRoot, StoreListingRootRelativePath);
    }

    internal static string[] GetLocaleColumns(IEnumerable<string> headers)
    {
        // Locale columns are whatever remains after the fixed metadata columns.
        // This lets the validator adapt to future languages without shipping a code edit first.
        return headers
            .Where(header => !MetadataColumns.Contains(header, StringComparer.Ordinal))
            .ToArray();
    }

    internal static string FindLatestExportTemplateCsv(string repositoryRoot)
    {
        var storeListingRoot = GetStoreListingRoot(repositoryRoot);
        var candidates = new[]
        {
            "listingData-*.csv",
            "Exported*.csv"
        };

        var files = candidates
            .SelectMany(pattern => Directory.EnumerateFiles(storeListingRoot, pattern, SearchOption.TopDirectoryOnly))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ToArray();

        if (files.Length == 0)
        {
            throw new FileNotFoundException(
                "Could not find a fresh Partner Center export template. Export a new listingData CSV and place it under Packaging/Windows/StoreListing.");
        }

        return files[0];
    }

    internal static string NormalizeAssetPath(string importFolderPath, string relativeAssetPath)
    {
        const string importFolderPrefix = "ImportFolder/";

        if (relativeAssetPath.StartsWith(importFolderPrefix, StringComparison.OrdinalIgnoreCase))
        {
            relativeAssetPath = relativeAssetPath[importFolderPrefix.Length..];
        }

        return Path.Combine(importFolderPath, relativeAssetPath.Replace('/', Path.DirectorySeparatorChar));
    }

    internal static string CombineRelativePath(string basePath, string relativePath)
    {
        // Test code runs on Windows, Linux, and macOS. Relative paths stored in repository-facing
        // constants may use either slash style, so normalize them once before combining.
        var segments = relativePath
            .Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);

        return segments.Aggregate(basePath, Path.Combine);
    }

    internal static int CountWords(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0;
        }

        return value
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Length;
    }

    internal static string DescribeRelativePath(string fullPath, string repositoryRoot)
    {
        return Path.GetRelativePath(repositoryRoot, fullPath)
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');
    }
}
