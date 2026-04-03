namespace DevProjex.Avalonia.Services;

internal enum MemoryCleanupReason
{
    InitialProjectLoad = 0,
    ProjectSwitchPostLoad = 1,
    RefreshProject = 2,
    GitPullUpdate = 3,
    GitBranchSwitch = 4,
    SearchClose = 5,
    FilterClose = 6,
    PreviewClose = 7,
    WindowDeactivated = 8
}

internal readonly record struct MemoryCleanupPlan(
    TimeSpan Delay,
    bool WaitForUiSettled);

internal static class MemoryCleanupPolicy
{
    private static readonly TimeSpan GeneralDeferredDelay = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan InitialLoadExtraDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan RefreshExtraDelay = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan PreviewDeferredDelay = TimeSpan.FromMilliseconds(140);

    public static MemoryCleanupPlan CreateDeferredPlan(
        MemoryCleanupReason reason,
        TimeSpan settingsPanelAnimationDuration)
    {
        return reason switch
        {
            // Initial project load should stay as smooth as possible. Wait for the settings
            // reveal to settle before running the expensive compacting collection.
            MemoryCleanupReason.InitialProjectLoad => new(
                Delay: settingsPanelAnimationDuration + InitialLoadExtraDelay,
                WaitForUiSettled: true),

            // Project switches already run one immediate compacting cleanup before the next
            // load starts. The post-load sweep only needs to reclaim transient buffers left
            // behind by the new load once the UI is calm again.
            MemoryCleanupReason.ProjectSwitchPostLoad => new(
                Delay: GeneralDeferredDelay,
                WaitForUiSettled: true),

            // Refresh/generic git reload paths rebuild the visible tree in-place. A delayed
            // cleanup after the first calm frame avoids stealing time from the paint pipeline.
            MemoryCleanupReason.RefreshProject => new(
                Delay: settingsPanelAnimationDuration + RefreshExtraDelay,
                WaitForUiSettled: true),
            MemoryCleanupReason.GitPullUpdate => new(
                Delay: GeneralDeferredDelay,
                WaitForUiSettled: true),
            MemoryCleanupReason.GitBranchSwitch => new(
                Delay: GeneralDeferredDelay,
                WaitForUiSettled: true),

            // Search/filter already wait for their close animation before requesting cleanup.
            // Another "UI settled" gate here would only delay reclamation without improving UX.
            MemoryCleanupReason.SearchClose => new(
                Delay: GeneralDeferredDelay,
                WaitForUiSettled: false),
            MemoryCleanupReason.FilterClose => new(
                Delay: GeneralDeferredDelay,
                WaitForUiSettled: false),

            // Preview close schedules cleanup only after render passes have already completed.
            MemoryCleanupReason.PreviewClose => new(
                Delay: PreviewDeferredDelay,
                WaitForUiSettled: false),

            // Deactivation is naturally outside the user's active interaction loop.
            MemoryCleanupReason.WindowDeactivated => new(
                Delay: GeneralDeferredDelay,
                WaitForUiSettled: false),

            _ => new(
                Delay: GeneralDeferredDelay,
                WaitForUiSettled: false)
        };
    }
}
