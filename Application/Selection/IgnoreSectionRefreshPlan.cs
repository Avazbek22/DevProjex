namespace DevProjex.Application.Selection;

public readonly record struct IgnoreSectionRefreshPlan(
    bool RequiresIgnoreOptionsRefresh,
    bool RequiresSecondSnapshotPass,
    bool RequiresScanRootRefresh,
    IgnoreOptionRefreshImpact Impact)
{
    public static IgnoreSectionRefreshPlan None { get; } = new(
        RequiresIgnoreOptionsRefresh: false,
        RequiresSecondSnapshotPass: false,
        RequiresScanRootRefresh: false,
        Impact: IgnoreOptionRefreshImpact.None);
}
