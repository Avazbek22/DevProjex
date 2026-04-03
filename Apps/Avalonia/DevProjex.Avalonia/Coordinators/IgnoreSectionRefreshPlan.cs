namespace DevProjex.Avalonia.Coordinators;

internal readonly record struct IgnoreSectionRefreshPlan(
    bool RequiresIgnoreOptionsRefresh,
    bool RequiresSecondSnapshotPass,
    bool RequiresRootFolderRefresh,
    IgnoreOptionRefreshImpact Impact)
{
    public static IgnoreSectionRefreshPlan None { get; } = new(
        RequiresIgnoreOptionsRefresh: false,
        RequiresSecondSnapshotPass: false,
        RequiresRootFolderRefresh: false,
        Impact: IgnoreOptionRefreshImpact.None);
}
