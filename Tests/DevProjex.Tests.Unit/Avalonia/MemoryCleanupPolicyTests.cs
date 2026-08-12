using DevProjex.Avalonia.Services;

namespace DevProjex.Tests.Unit.Avalonia;

public sealed class MemoryCleanupPolicyTests
{
    private const long Megabyte = 1024L * 1024L;

    [Theory]
    [InlineData(
        (int)MemoryCleanupReason.ProjectSwitchPostLoad,
        450,
        (int)MemoryCleanupCollectionMode.Aggressive)]
    [InlineData(
        (int)MemoryCleanupReason.GitPullUpdate,
        450,
        (int)MemoryCleanupCollectionMode.Aggressive)]
    [InlineData(
        (int)MemoryCleanupReason.GitBranchSwitch,
        450,
        (int)MemoryCleanupCollectionMode.Aggressive)]
    [InlineData(
        (int)MemoryCleanupReason.SelectionProjectionNarrowed,
        450,
        (int)MemoryCleanupCollectionMode.Aggressive)]
    [InlineData(
        (int)MemoryCleanupReason.TreeCollapseCompleted,
        450,
        (int)MemoryCleanupCollectionMode.Aggressive)]
    [InlineData(
        (int)MemoryCleanupReason.SearchClose,
        400,
        (int)MemoryCleanupCollectionMode.Aggressive)]
    [InlineData(
        (int)MemoryCleanupReason.FilterClose,
        400,
        (int)MemoryCleanupCollectionMode.Aggressive)]
    [InlineData(
        (int)MemoryCleanupReason.PreviewClose,
        350,
        (int)MemoryCleanupCollectionMode.Aggressive)]
    [InlineData(
        (int)MemoryCleanupReason.PreviewRebuildCompleted,
        450,
        (int)MemoryCleanupCollectionMode.Background)]
    [InlineData(
        (int)MemoryCleanupReason.ApplySettingsWorkCompleted,
        450,
        (int)MemoryCleanupCollectionMode.Background)]
    public void CreateDeferredPlan_ReturnsReasonSpecificContract(
        int reasonRaw,
        int expectedDelayMilliseconds,
        int expectedCollectionModeRaw)
    {
        var result = MemoryCleanupPolicy.CreateDeferredPlan(
            (MemoryCleanupReason)reasonRaw,
            settingsPanelAnimationDuration: TimeSpan.FromMilliseconds(180));

        Assert.True(result.WaitForUiSettled);
        Assert.Equal(
            TimeSpan.FromMilliseconds(expectedDelayMilliseconds),
            result.Delay);
        Assert.Equal(
            (MemoryCleanupCollectionMode)expectedCollectionModeRaw,
            result.CollectionMode);
    }

    [Theory]
    [InlineData(0, 400)]
    [InlineData(180, 580)]
    [InlineData(350, 750)]
    public void CreateDeferredPlan_InitialProjectLoad_UsesSettingsAnimationAwareDelay(
        int settingsAnimationMilliseconds,
        int expectedTotalDelayMilliseconds)
    {
        var result = MemoryCleanupPolicy.CreateDeferredPlan(
            MemoryCleanupReason.InitialProjectLoad,
            TimeSpan.FromMilliseconds(settingsAnimationMilliseconds));

        Assert.True(result.WaitForUiSettled);
        Assert.Equal(
            MemoryCleanupCollectionMode.Aggressive,
            result.CollectionMode);
        Assert.Equal(
            TimeSpan.FromMilliseconds(expectedTotalDelayMilliseconds),
            result.Delay);
    }

    [Theory]
    [InlineData(0, 300)]
    [InlineData(180, 480)]
    [InlineData(350, 650)]
    public void CreateDeferredPlan_RefreshProject_UsesSettingsAnimationAwareDelay(
        int settingsAnimationMilliseconds,
        int expectedTotalDelayMilliseconds)
    {
        var result = MemoryCleanupPolicy.CreateDeferredPlan(
            MemoryCleanupReason.RefreshProject,
            TimeSpan.FromMilliseconds(settingsAnimationMilliseconds));

        Assert.True(result.WaitForUiSettled);
        Assert.Equal(
            MemoryCleanupCollectionMode.Aggressive,
            result.CollectionMode);
        Assert.Equal(
            TimeSpan.FromMilliseconds(expectedTotalDelayMilliseconds),
            result.Delay);
    }

    [Fact]
    public void CreateDeferredPlan_FilterAppliedWaitsForPublishedMetricsAndStableUi()
    {
        var result = MemoryCleanupPolicy.CreateDeferredPlan(
            MemoryCleanupReason.FilterApplied,
            settingsPanelAnimationDuration: TimeSpan.Zero);

        Assert.True(result.WaitForUiSettled);
        Assert.Equal(TimeSpan.FromMilliseconds(400), result.Delay);
        Assert.Equal(
            MemoryCleanupCollectionMode.Aggressive,
            result.CollectionMode);
    }

    [Theory]
    [InlineData((int)MemoryCleanupReason.SearchClose)]
    [InlineData((int)MemoryCleanupReason.FilterClose)]
    [InlineData((int)MemoryCleanupReason.FilterApplied)]
    [InlineData((int)MemoryCleanupReason.PreviewClose)]
    [InlineData((int)MemoryCleanupReason.InitialProjectLoad)]
    [InlineData((int)MemoryCleanupReason.ProjectSwitchPostLoad)]
    [InlineData((int)MemoryCleanupReason.RefreshProject)]
    [InlineData((int)MemoryCleanupReason.GitPullUpdate)]
    [InlineData((int)MemoryCleanupReason.GitBranchSwitch)]
    [InlineData((int)MemoryCleanupReason.SelectionProjectionNarrowed)]
    [InlineData((int)MemoryCleanupReason.TreeCollapseCompleted)]
    public void CreateDeferredPlan_ReleasedGraphsUseAggressiveCleanup(
        int reasonRaw)
    {
        var plan = CreatePlan((MemoryCleanupReason)reasonRaw);

        Assert.Equal(
            MemoryCleanupCollectionMode.Aggressive,
            plan.CollectionMode);
    }

    [Theory]
    [InlineData((int)MemoryCleanupReason.PreviewRebuildCompleted)]
    [InlineData((int)MemoryCleanupReason.ApplySettingsWorkCompleted)]
    public void CreateDeferredPlan_NonDetachingWorkUsesBackgroundCleanup(
        int reasonRaw)
    {
        var plan = CreatePlan((MemoryCleanupReason)reasonRaw);

        Assert.Equal(
            MemoryCleanupCollectionMode.Background,
            plan.CollectionMode);
    }

    [Fact]
    public void CreateDeferredPlan_EveryReasonHasAnExplicitEscalationPolicy()
    {
        var expected = new Dictionary<MemoryCleanupReason, MemoryCleanupEscalationMode>
        {
            [MemoryCleanupReason.InitialProjectLoad] = MemoryCleanupEscalationMode.Aggressive,
            [MemoryCleanupReason.ProjectSwitchPostLoad] = MemoryCleanupEscalationMode.Aggressive,
            [MemoryCleanupReason.RefreshProject] = MemoryCleanupEscalationMode.Aggressive,
            [MemoryCleanupReason.GitPullUpdate] = MemoryCleanupEscalationMode.Aggressive,
            [MemoryCleanupReason.GitBranchSwitch] = MemoryCleanupEscalationMode.Aggressive,
            [MemoryCleanupReason.SearchClose] = MemoryCleanupEscalationMode.Aggressive,
            [MemoryCleanupReason.FilterClose] = MemoryCleanupEscalationMode.Aggressive,
            [MemoryCleanupReason.PreviewClose] = MemoryCleanupEscalationMode.Aggressive,
            [MemoryCleanupReason.PreviewRebuildCompleted] = MemoryCleanupEscalationMode.Compacting,
            [MemoryCleanupReason.SelectionProjectionNarrowed] = MemoryCleanupEscalationMode.Aggressive,
            [MemoryCleanupReason.TreeCollapseCompleted] = MemoryCleanupEscalationMode.Aggressive,
            [MemoryCleanupReason.FilterApplied] = MemoryCleanupEscalationMode.Aggressive,
            [MemoryCleanupReason.ApplySettingsWorkCompleted] = MemoryCleanupEscalationMode.Compacting
        };
        var reasons = Enum.GetValues<MemoryCleanupReason>();

        Assert.Equal(reasons.Length, expected.Count);
        foreach (var reason in reasons)
        {
            Assert.Equal(
                expected[reason],
                CreatePlan(reason).EscalationMode);
        }
    }

    [Theory]
    [InlineData(127, 100, false)]
    [InlineData(128, 31, false)]
    [InlineData(159, 32, true)]
    [InlineData(160, 32, true)]
    [InlineData(161, 32, false)]
    [InlineData(200, 40, true)]
    public void ShouldCompactAfterBackgroundCollection_UsesReservedHeapFragmentation(
        long heapSizeMegabytes,
        long fragmentedMegabytes,
        bool expected)
    {
        var snapshot = Snapshot(
            managedHeapBytes: 1 * Megabyte,
            heapSizeBytes: heapSizeMegabytes * Megabyte,
            fragmentedBytes: fragmentedMegabytes * Megabyte);

        Assert.Equal(
            expected,
            MemoryCleanupPolicy.ShouldCompactAfterBackgroundCollection(
                snapshot));
    }

    [Fact]
    public void ShouldCompactAfterBackgroundCollection_LowLiveBytesDoNotHideFragmentedReservedHeap()
    {
        var snapshot = Snapshot(
            managedHeapBytes: 20 * Megabyte,
            heapSizeBytes: 466 * Megabyte,
            fragmentedBytes: 339 * Megabyte);

        Assert.True(
            MemoryCleanupPolicy.ShouldCompactAfterBackgroundCollection(
                snapshot));
    }

    private static MemoryCleanupPlan CreatePlan(
        MemoryCleanupReason reason) =>
        MemoryCleanupPolicy.CreateDeferredPlan(
            reason,
            settingsPanelAnimationDuration: TimeSpan.Zero);

    private static MemoryCleanupSnapshot Snapshot(
        long managedHeapBytes,
        long heapSizeBytes = 0,
        long fragmentedBytes = 0) =>
        new(
            managedHeapBytes,
            heapSizeBytes,
            fragmentedBytes,
            MemoryLoadBytes: 0,
            HighMemoryLoadThresholdBytes: 0);
}
