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
    bool WaitForUiSettled,
    long MinimumManagedHeapBytes);

internal static class MemoryCleanupPolicy
{
    internal const long RoutineCleanupMinimumManagedHeapBytes = 64L * 1024 * 1024;
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
                WaitForUiSettled: true,
                MinimumManagedHeapBytes: RoutineCleanupMinimumManagedHeapBytes),

            // Project switches already run one immediate compacting cleanup before the next
            // load starts. The post-load sweep only needs to reclaim transient buffers left
            // behind by the new load once the UI is calm again.
            MemoryCleanupReason.ProjectSwitchPostLoad => new(
                Delay: GeneralDeferredDelay,
                WaitForUiSettled: true,
                MinimumManagedHeapBytes: RoutineCleanupMinimumManagedHeapBytes),

            // Refresh/generic git reload paths rebuild the visible tree in-place. A delayed
            // cleanup after the first calm frame avoids stealing time from the paint pipeline.
            MemoryCleanupReason.RefreshProject => new(
                Delay: settingsPanelAnimationDuration + RefreshExtraDelay,
                WaitForUiSettled: true,
                MinimumManagedHeapBytes: RoutineCleanupMinimumManagedHeapBytes),
            MemoryCleanupReason.GitPullUpdate => new(
                Delay: GeneralDeferredDelay,
                WaitForUiSettled: true,
                MinimumManagedHeapBytes: RoutineCleanupMinimumManagedHeapBytes),
            MemoryCleanupReason.GitBranchSwitch => new(
                Delay: GeneralDeferredDelay,
                WaitForUiSettled: true,
                MinimumManagedHeapBytes: RoutineCleanupMinimumManagedHeapBytes),

            // Search/filter already wait for their close animation before requesting cleanup.
            // Another "UI settled" gate here would only delay reclamation without improving UX.
            MemoryCleanupReason.SearchClose => new(
                Delay: GeneralDeferredDelay,
                WaitForUiSettled: false,
                MinimumManagedHeapBytes: RoutineCleanupMinimumManagedHeapBytes),
            MemoryCleanupReason.FilterClose => new(
                Delay: GeneralDeferredDelay,
                WaitForUiSettled: false,
                MinimumManagedHeapBytes: RoutineCleanupMinimumManagedHeapBytes),

            // Preview close schedules cleanup only after render passes have already completed.
            MemoryCleanupReason.PreviewClose => new(
                Delay: PreviewDeferredDelay,
                WaitForUiSettled: false,
                MinimumManagedHeapBytes: 0),

            // Do not compact a healthy small heap every time the user Alt-Tabs. Besides the
            // collection itself, trimming the working set creates page faults on reactivation.
            MemoryCleanupReason.WindowDeactivated => new(
                Delay: GeneralDeferredDelay,
                WaitForUiSettled: false,
                MinimumManagedHeapBytes: RoutineCleanupMinimumManagedHeapBytes),

            _ => new(
                Delay: GeneralDeferredDelay,
                WaitForUiSettled: false,
                MinimumManagedHeapBytes: RoutineCleanupMinimumManagedHeapBytes)
        };
    }

    public static bool ShouldRun(MemoryCleanupPlan plan, long managedHeapBytes) =>
        managedHeapBytes >= plan.MinimumManagedHeapBytes;

    public static bool ShouldRunRoutineCleanup(long managedHeapBytes) =>
        managedHeapBytes >= RoutineCleanupMinimumManagedHeapBytes;
}
