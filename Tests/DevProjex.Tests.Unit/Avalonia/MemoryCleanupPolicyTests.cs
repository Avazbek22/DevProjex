using DevProjex.Avalonia.Services;

namespace DevProjex.Tests.Unit.Avalonia;

public sealed class MemoryCleanupPolicyTests
{
    [Theory]
    [InlineData((int)MemoryCleanupReason.ProjectSwitchPostLoad, true, 400)]
    [InlineData((int)MemoryCleanupReason.GitPullUpdate, true, 400)]
    [InlineData((int)MemoryCleanupReason.GitBranchSwitch, true, 400)]
    [InlineData((int)MemoryCleanupReason.SearchClose, false, 400)]
    [InlineData((int)MemoryCleanupReason.FilterClose, false, 400)]
    [InlineData((int)MemoryCleanupReason.PreviewClose, false, 140)]
    [InlineData((int)MemoryCleanupReason.PreviewRebuildCompleted, false, 400)]
    public void CreateDeferredPlan_ReturnsStableReasonSpecificContract(
        int reasonRaw,
        bool expectedWaitForUiSettled,
        int expectedDelayMilliseconds)
    {
        var reason = (MemoryCleanupReason)reasonRaw;
        var result = MemoryCleanupPolicy.CreateDeferredPlan(
            reason,
            settingsPanelAnimationDuration: TimeSpan.FromMilliseconds(180));

        Assert.Equal(expectedWaitForUiSettled, result.WaitForUiSettled);
        Assert.Equal(TimeSpan.FromMilliseconds(expectedDelayMilliseconds), result.Delay);
    }

    [Theory]
    [InlineData(0, 500)]
    [InlineData(180, 680)]
    [InlineData(350, 850)]
    public void CreateDeferredPlan_InitialProjectLoad_UsesSettingsAnimationAwareDelay(
        int settingsAnimationMilliseconds,
        int expectedTotalDelayMilliseconds)
    {
        var result = MemoryCleanupPolicy.CreateDeferredPlan(
            MemoryCleanupReason.InitialProjectLoad,
            settingsPanelAnimationDuration: TimeSpan.FromMilliseconds(settingsAnimationMilliseconds));

        Assert.True(result.WaitForUiSettled);
        Assert.Equal(TimeSpan.FromMilliseconds(expectedTotalDelayMilliseconds), result.Delay);
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
            settingsPanelAnimationDuration: TimeSpan.FromMilliseconds(settingsAnimationMilliseconds));

        Assert.True(result.WaitForUiSettled);
        Assert.Equal(TimeSpan.FromMilliseconds(expectedTotalDelayMilliseconds), result.Delay);
    }

    [Fact]
    public void CreateDeferredPlan_NonAnimationReasons_DoNotDependOnSettingsDuration()
    {
        var shortDelayPlan = MemoryCleanupPolicy.CreateDeferredPlan(
            MemoryCleanupReason.SearchClose,
            settingsPanelAnimationDuration: TimeSpan.FromMilliseconds(120));
        var longDelayPlan = MemoryCleanupPolicy.CreateDeferredPlan(
            MemoryCleanupReason.SearchClose,
            settingsPanelAnimationDuration: TimeSpan.FromSeconds(3));

        Assert.Equal(shortDelayPlan, longDelayPlan);
    }

    [Theory]
    [InlineData((int)MemoryCleanupReason.InitialProjectLoad)]
    [InlineData((int)MemoryCleanupReason.ProjectSwitchPostLoad)]
    [InlineData((int)MemoryCleanupReason.RefreshProject)]
    [InlineData((int)MemoryCleanupReason.GitPullUpdate)]
    [InlineData((int)MemoryCleanupReason.GitBranchSwitch)]
    [InlineData((int)MemoryCleanupReason.SearchClose)]
    [InlineData((int)MemoryCleanupReason.FilterClose)]
    [InlineData((int)MemoryCleanupReason.PreviewRebuildCompleted)]
    public void ShouldRun_RoutineLifecycleCleanup_RequiresMaterialManagedHeap(int reasonRaw)
    {
        var plan = MemoryCleanupPolicy.CreateDeferredPlan(
            (MemoryCleanupReason)reasonRaw,
            settingsPanelAnimationDuration: TimeSpan.Zero);

        Assert.False(MemoryCleanupPolicy.ShouldRun(
            plan,
            MemoryCleanupPolicy.RoutineCleanupMinimumManagedHeapBytes - 1));
        Assert.True(MemoryCleanupPolicy.ShouldRun(
            plan,
            MemoryCleanupPolicy.RoutineCleanupMinimumManagedHeapBytes));
    }

    [Fact]
    public void ShouldRun_HeavyPreviewCleanup_RemainsUnconditional()
    {
        var plan = MemoryCleanupPolicy.CreateDeferredPlan(
            MemoryCleanupReason.PreviewClose,
            settingsPanelAnimationDuration: TimeSpan.Zero);

        Assert.True(MemoryCleanupPolicy.ShouldRun(plan, managedHeapBytes: 0));
    }

    [Fact]
    public void ShouldRun_PreviewRebuildCleanup_RemainsThresholdGated()
    {
        var plan = MemoryCleanupPolicy.CreateDeferredPlan(
            MemoryCleanupReason.PreviewRebuildCompleted,
            settingsPanelAnimationDuration: TimeSpan.Zero);

        Assert.False(MemoryCleanupPolicy.ShouldRun(
            plan,
            MemoryCleanupPolicy.RoutineCleanupMinimumManagedHeapBytes - 1));
        Assert.True(MemoryCleanupPolicy.ShouldRun(
            plan,
            MemoryCleanupPolicy.RoutineCleanupMinimumManagedHeapBytes));
    }

    [Fact]
    public void ShouldRunRoutineCleanup_UsesStableInclusiveThreshold()
    {
        Assert.False(MemoryCleanupPolicy.ShouldRunRoutineCleanup(0));
        Assert.False(MemoryCleanupPolicy.ShouldRunRoutineCleanup(
            MemoryCleanupPolicy.RoutineCleanupMinimumManagedHeapBytes - 1));
        Assert.True(MemoryCleanupPolicy.ShouldRunRoutineCleanup(
            MemoryCleanupPolicy.RoutineCleanupMinimumManagedHeapBytes));
    }
}
