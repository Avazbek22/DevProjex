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
    [InlineData((int)MemoryCleanupReason.WindowDeactivated, false, 400)]
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
}
