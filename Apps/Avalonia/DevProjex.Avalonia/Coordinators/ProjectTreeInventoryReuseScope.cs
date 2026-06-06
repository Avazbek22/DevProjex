using DevProjex.Kernel;

namespace DevProjex.Avalonia.Coordinators;

internal sealed record ProjectTreeInventoryReuseScope(
    string RootPath,
    IReadOnlySet<string> AllowedRootFolders,
    bool UseGitIgnore,
    bool UseSmartIgnore,
    bool IgnoreHiddenFolders,
    bool IgnoreDotFolders,
    bool SupportsHiddenDotFolderVariants)
{
    public static ProjectTreeInventoryReuseScope Create(
        string rootPath,
        TreeFilterOptions options,
        bool supportsHiddenDotFolderVariants)
    {
        return new ProjectTreeInventoryReuseScope(
            rootPath,
            new HashSet<string>(options.AllowedRootFolders, PathComparer.Default),
            options.IgnoreRules.UseGitIgnore,
            options.IgnoreRules.UseSmartIgnore,
            options.IgnoreRules.IgnoreHiddenFolders,
            options.IgnoreRules.IgnoreDotFolders,
            supportsHiddenDotFolderVariants);
    }

    public bool CanProject(string rootPath, TreeFilterOptions options)
    {
        if (!PathComparer.Default.Equals(RootPath, rootPath))
            return false;

        if (!IsRootSelectionCovered(options.AllowedRootFolders))
            return false;

        var rules = options.IgnoreRules;
        if (UseGitIgnore && !rules.UseGitIgnore)
            return false;
        if (UseSmartIgnore && !rules.UseSmartIgnore)
            return false;

        if (!SupportsHiddenDotFolderVariants &&
            ((IgnoreHiddenFolders && !rules.IgnoreHiddenFolders) ||
             (IgnoreDotFolders && !rules.IgnoreDotFolders)))
        {
            return false;
        }

        return true;
    }

    private bool IsRootSelectionCovered(IReadOnlySet<string> requestedRootFolders)
    {
        foreach (var rootFolder in requestedRootFolders)
        {
            if (!AllowedRootFolders.Contains(rootFolder))
                return false;
        }

        return true;
    }
}

internal sealed record ProjectTreeInventoryState(
    ProjectTreeInventorySnapshot Snapshot,
    ProjectTreeInventoryReuseScope Scope);
