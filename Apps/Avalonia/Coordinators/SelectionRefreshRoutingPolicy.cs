namespace DevProjex.Avalonia.Coordinators;

internal static class SelectionRefreshRoutingPolicy
{
    public static bool CanUseLiveOptionsRefresh(IgnoreOptionId? changedOptionId)
    {
        return changedOptionId.HasValue &&
               IgnoreOptionRefreshPlanner.GetImpact(changedOptionId.Value) == IgnoreOptionRefreshImpact.FileVisibility;
    }
}
