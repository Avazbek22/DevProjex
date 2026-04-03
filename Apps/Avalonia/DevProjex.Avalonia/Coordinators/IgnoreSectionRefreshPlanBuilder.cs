namespace DevProjex.Avalonia.Coordinators;

internal static class IgnoreSectionRefreshPlanBuilder
{
    /// <summary>
    /// Builds the minimal follow-up work after a live snapshot refresh.
    /// The coordinator uses this plan to avoid blanket rescans when only
    /// file-level dynamic toggles changed.
    /// </summary>
    public static IgnoreSectionRefreshPlan Build(
        in IgnoreSectionSnapshotState beforeSnapshot,
        in IgnoreSectionSnapshotState afterSnapshot,
        IReadOnlySet<IgnoreOptionId> beforeSelection,
        IReadOnlySet<IgnoreOptionId> afterSelection)
    {
        if (!beforeSnapshot.HasAvailabilityDifference(afterSnapshot))
            return IgnoreSectionRefreshPlan.None;

        var impact = IgnoreOptionRefreshPlanner.ClassifyChangedSelection(beforeSelection, afterSelection);
        if (impact == IgnoreOptionRefreshImpact.None)
        {
            return new IgnoreSectionRefreshPlan(
                RequiresIgnoreOptionsRefresh: true,
                RequiresSecondSnapshotPass: false,
                RequiresRootFolderRefresh: false,
                Impact: IgnoreOptionRefreshImpact.None);
        }

        return new IgnoreSectionRefreshPlan(
            RequiresIgnoreOptionsRefresh: true,
            RequiresSecondSnapshotPass: true,
            RequiresRootFolderRefresh: (impact & IgnoreOptionRefreshImpact.RootStructure) != 0,
            Impact: impact);
    }
}
