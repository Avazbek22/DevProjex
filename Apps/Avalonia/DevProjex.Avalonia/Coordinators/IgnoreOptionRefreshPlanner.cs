namespace DevProjex.Avalonia.Coordinators;

internal static class IgnoreOptionRefreshPlanner
{
    public static IgnoreOptionRefreshImpact ClassifyChangedSelection(
        IReadOnlySet<IgnoreOptionId> beforeSelection,
        IReadOnlySet<IgnoreOptionId> afterSelection)
    {
        var impact = IgnoreOptionRefreshImpact.None;

        foreach (var id in beforeSelection)
        {
            if (!afterSelection.Contains(id))
                impact |= GetImpact(id);
        }

        foreach (var id in afterSelection)
        {
            if (!beforeSelection.Contains(id))
                impact |= GetImpact(id);
        }

        return impact;
    }

    /// <summary>
    /// Directory-level toggles can invalidate the root-folder list itself, while file-level
    /// toggles only change extension/count snapshots. Keeping this split explicit prevents
    /// the coordinator from falling back to blanket rescans after every dynamic mutation.
    /// </summary>
    public static IgnoreOptionRefreshImpact GetImpact(IgnoreOptionId id)
    {
        return id switch
        {
            IgnoreOptionId.HiddenFolders => IgnoreOptionRefreshImpact.RootStructure,
            IgnoreOptionId.DotFolders => IgnoreOptionRefreshImpact.RootStructure,
            IgnoreOptionId.EmptyFolders => IgnoreOptionRefreshImpact.RootStructure,
            IgnoreOptionId.HiddenFiles => IgnoreOptionRefreshImpact.FileVisibility,
            IgnoreOptionId.DotFiles => IgnoreOptionRefreshImpact.FileVisibility,
            IgnoreOptionId.EmptyFiles => IgnoreOptionRefreshImpact.FileVisibility,
            IgnoreOptionId.ExtensionlessFiles => IgnoreOptionRefreshImpact.FileVisibility,
            _ => IgnoreOptionRefreshImpact.RootStructure
        };
    }
}
