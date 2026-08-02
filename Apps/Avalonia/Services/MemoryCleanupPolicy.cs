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
    PreviewRebuildCompleted = 8,
    SelectionProjectionNarrowed = 9,
    TreeCollapseCompleted = 10,
    FilterApplied = 11
}

internal enum MemoryCleanupCollectionMode
{
    None = 0,
    Background = 1,
    Aggressive = 2
}

internal readonly record struct MemoryCleanupPlan(
    TimeSpan Delay,
    bool WaitForUiSettled,
    MemoryCleanupCollectionMode CollectionMode);

internal readonly record struct MemoryCleanupSnapshot(
    long ManagedHeapBytes,
    long HeapSizeBytes,
    long FragmentedBytes,
    long MemoryLoadBytes,
    long HighMemoryLoadThresholdBytes,
    long CollectionIndex = 0)
{
    public static MemoryCleanupSnapshot Capture()
    {
        var memory = GC.GetGCMemoryInfo();
        return new MemoryCleanupSnapshot(
            ManagedHeapBytes: GC.GetTotalMemory(forceFullCollection: false),
            HeapSizeBytes: memory.HeapSizeBytes,
            FragmentedBytes: memory.FragmentedBytes,
            MemoryLoadBytes: memory.MemoryLoadBytes,
            HighMemoryLoadThresholdBytes: memory.HighMemoryLoadThresholdBytes,
            CollectionIndex: memory.Index);
    }
}

internal static class MemoryCleanupPolicy
{
    internal const long CompactionMinimumHeapBytes = 128L * 1024 * 1024;
    internal const long CompactionMinimumFragmentedBytes = 32L * 1024 * 1024;
    private const int CompactionMinimumFragmentationPercentage = 20;
    private static readonly TimeSpan GeneralDeferredDelay = TimeSpan.FromMilliseconds(450);
    private static readonly TimeSpan ReleasedGraphCleanupDelay = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan InitialLoadExtraDelay = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan RefreshExtraDelay = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan PreviewDeferredDelay = TimeSpan.FromMilliseconds(350);

    public static MemoryCleanupPlan CreateDeferredPlan(
        MemoryCleanupReason reason,
        TimeSpan settingsPanelAnimationDuration)
    {
        return reason switch
        {
            MemoryCleanupReason.InitialProjectLoad => new(
                Delay: settingsPanelAnimationDuration + InitialLoadExtraDelay,
                WaitForUiSettled: true,
                CollectionMode: MemoryCleanupCollectionMode.Aggressive),

            MemoryCleanupReason.ProjectSwitchPostLoad => new(
                Delay: GeneralDeferredDelay,
                WaitForUiSettled: true,
                CollectionMode: MemoryCleanupCollectionMode.Aggressive),

            MemoryCleanupReason.RefreshProject => new(
                Delay: settingsPanelAnimationDuration + RefreshExtraDelay,
                WaitForUiSettled: true,
                CollectionMode: MemoryCleanupCollectionMode.Aggressive),
            MemoryCleanupReason.GitPullUpdate => new(
                Delay: GeneralDeferredDelay,
                WaitForUiSettled: true,
                CollectionMode: MemoryCleanupCollectionMode.Aggressive),
            MemoryCleanupReason.GitBranchSwitch => new(
                Delay: GeneralDeferredDelay,
                WaitForUiSettled: true,
                CollectionMode: MemoryCleanupCollectionMode.Aggressive),
            MemoryCleanupReason.SelectionProjectionNarrowed => new(
                Delay: GeneralDeferredDelay,
                WaitForUiSettled: true,
                CollectionMode: MemoryCleanupCollectionMode.Aggressive),
            MemoryCleanupReason.TreeCollapseCompleted => new(
                Delay: GeneralDeferredDelay,
                WaitForUiSettled: true,
                CollectionMode: MemoryCleanupCollectionMode.Aggressive),

            MemoryCleanupReason.SearchClose => new(
                Delay: ReleasedGraphCleanupDelay,
                WaitForUiSettled: true,
                CollectionMode: MemoryCleanupCollectionMode.Aggressive),

            MemoryCleanupReason.FilterClose => new(
                Delay: ReleasedGraphCleanupDelay,
                WaitForUiSettled: true,
                CollectionMode: MemoryCleanupCollectionMode.Aggressive),

            MemoryCleanupReason.FilterApplied => new(
                Delay: ReleasedGraphCleanupDelay,
                WaitForUiSettled: true,
                CollectionMode: MemoryCleanupCollectionMode.Aggressive),

            MemoryCleanupReason.PreviewClose => new(
                Delay: PreviewDeferredDelay,
                WaitForUiSettled: true,
                CollectionMode: MemoryCleanupCollectionMode.Aggressive),

            MemoryCleanupReason.PreviewRebuildCompleted => new(
                Delay: GeneralDeferredDelay,
                WaitForUiSettled: true,
                CollectionMode: MemoryCleanupCollectionMode.Background),

            _ => new(
                Delay: GeneralDeferredDelay,
                WaitForUiSettled: true,
                CollectionMode: MemoryCleanupCollectionMode.Background)
        };
    }

    public static bool ShouldCompactAfterBackgroundCollection(
        MemoryCleanupSnapshot snapshot)
    {
        return
            snapshot.HeapSizeBytes >= CompactionMinimumHeapBytes &&
            snapshot.FragmentedBytes >= CompactionMinimumFragmentedBytes &&
            snapshot.FragmentedBytes >=
            snapshot.HeapSizeBytes * CompactionMinimumFragmentationPercentage / 100;
    }
}
