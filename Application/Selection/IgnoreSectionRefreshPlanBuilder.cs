namespace DevProjex.Application.Selection;

public static class IgnoreSectionRefreshPlanBuilder
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
        var impact = IgnoreOptionRefreshPlanner.ClassifyChangedSelection(beforeSelection, afterSelection);
        if (!beforeSnapshot.HasAvailabilityDifference(afterSnapshot))
        {
            if (impact == IgnoreOptionRefreshImpact.None)
                return IgnoreSectionRefreshPlan.None;

            // A checked-state change can alter scan scope or extension output even when the
            // visible availability counters stay numerically equal. The follow-up keeps the
            // final tree rules and both remaining option sections aligned.
            return new IgnoreSectionRefreshPlan(
                RequiresIgnoreOptionsRefresh: true,
                RequiresSecondSnapshotPass: true,
                RequiresScanRootRefresh: (impact & IgnoreOptionRefreshImpact.RootStructure) != 0,
                Impact: impact);
        }

        if (impact == IgnoreOptionRefreshImpact.None)
        {
            return new IgnoreSectionRefreshPlan(
                RequiresIgnoreOptionsRefresh: true,
                RequiresSecondSnapshotPass: false,
                RequiresScanRootRefresh: false,
                Impact: IgnoreOptionRefreshImpact.None);
        }

        return new IgnoreSectionRefreshPlan(
            RequiresIgnoreOptionsRefresh: true,
            RequiresSecondSnapshotPass: true,
            RequiresScanRootRefresh: (impact & IgnoreOptionRefreshImpact.RootStructure) != 0,
            Impact: impact);
    }
}
