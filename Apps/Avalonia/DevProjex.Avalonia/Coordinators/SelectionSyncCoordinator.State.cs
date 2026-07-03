namespace DevProjex.Avalonia.Coordinators;

public sealed partial class SelectionSyncCoordinator
{
    private sealed record IgnoreRulesBuildCacheEntry(string Key, IgnoreRules Rules);

    private sealed record LiveRefreshInput(
        SelectionRefreshContext Context,
        IReadOnlyCollection<string> SelectedRoots);
}
