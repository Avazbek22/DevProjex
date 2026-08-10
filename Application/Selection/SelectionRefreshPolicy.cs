using DevProjex.Application.Models;

namespace DevProjex.Application.Selection;

public enum PreparedSelectionMode
{
    None = 0,
    Defaults = 1,
    Profile = 2
}

public static class SelectionRefreshPolicy
{
    public static bool ShouldClearCachesForCurrentPath(
        string? lastLoadedPath,
        string? preparedSelectionPath,
        string currentPath)
    {
        var isPathSwitch = lastLoadedPath is not null && !PathComparer.Default.Equals(lastLoadedPath, currentPath);
        var hasPreparedSelectionForCurrentPath = HasPreparedSelectionForPath(preparedSelectionPath, currentPath);
        return isPathSwitch && !hasPreparedSelectionForCurrentPath;
    }

    public static bool ShouldSkipRefreshForPreparedPath(string? preparedSelectionPath, string currentPath)
    {
        return preparedSelectionPath is not null &&
               !PathComparer.Default.Equals(preparedSelectionPath, currentPath);
    }

    public static IReadOnlyList<SelectionOption> ApplyMissingProfileSelectionsFallbackToExtensions(
        PreparedSelectionMode preparedSelectionMode,
        IReadOnlyCollection<string> cachedSelections,
        IReadOnlyList<SelectionOption> options)
    {
        if (!ShouldApplyMissingProfileSelectionsFallback(preparedSelectionMode, cachedSelections, options))
            return options;

        var fallback = new List<SelectionOption>(options.Count);
        foreach (var option in options)
            fallback.Add(option with { IsChecked = true });
        return fallback;
    }

    public static bool ShouldApplyMissingProfileSelectionsFallback(
        PreparedSelectionMode preparedSelectionMode,
        IReadOnlyCollection<string> cachedSelections,
        IReadOnlyList<SelectionOption> options)
    {
        if (preparedSelectionMode != PreparedSelectionMode.Profile)
            return false;
        if (cachedSelections.Count == 0 || options.Count == 0)
            return false;

        foreach (var option in options)
        {
            if (option.IsChecked)
                return false;
        }

        return true;
    }

    // TODO(cli): Remove with the legacy --root/profile selection contract. Desktop no longer
    // exposes or persists top-level-folder selection.
    public static IReadOnlyList<SelectionOption> ApplyLegacyCliRootFallback(
        PreparedSelectionMode preparedSelectionMode,
        IReadOnlyCollection<string> cachedSelections,
        IReadOnlyList<SelectionOption> options,
        IReadOnlyList<string> scannedRootFolders,
        IgnoreRules ignoreRules,
        FilterOptionSelectionService filterSelectionService,
        IReadOnlySet<string> emptySelectionSet)
    {
        if (preparedSelectionMode != PreparedSelectionMode.Profile ||
            cachedSelections.Count == 0 ||
            options.Count == 0 ||
            options.Any(static option => option.IsChecked))
        {
            return options;
        }

        return filterSelectionService.BuildRootFolderOptions(
            scannedRootFolders,
            emptySelectionSet,
            ignoreRules,
            hasPreviousSelections: false);
    }

    public static bool ShouldUseIgnoreDefaultFallback(
        PreparedSelectionMode preparedSelectionMode,
        IReadOnlyList<IgnoreOptionDescriptor> options,
        IReadOnlySet<IgnoreOptionId> previousSelections)
    {
        if (preparedSelectionMode != PreparedSelectionMode.Profile)
            return false;
        if (previousSelections.Count == 0 || options.Count == 0)
            return false;

        foreach (var option in options)
        {
            if (previousSelections.Contains(option.Id))
                return false;
        }

        return true;
    }

    public static bool CanUseIgnoreDefaultFallback(IgnoreOptionId optionId) =>
        optionId is not IgnoreOptionId.UseGitIgnore
            and not IgnoreOptionId.TrackedGitFilesOnly
            and not IgnoreOptionId.SmartIgnore
            and not IgnoreOptionId.HideSecrets
			and not IgnoreOptionId.CompressCode
			and not IgnoreOptionId.StripComments;

    private static bool HasPreparedSelectionForPath(string? preparedSelectionPath, string path)
    {
        return preparedSelectionPath is not null &&
               PathComparer.Default.Equals(preparedSelectionPath, path);
    }
}
