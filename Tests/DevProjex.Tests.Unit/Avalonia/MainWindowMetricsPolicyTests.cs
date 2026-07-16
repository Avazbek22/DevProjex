using DevProjex.Avalonia.Services;

namespace DevProjex.Tests.Unit.Avalonia;

public sealed class MainWindowMetricsPolicyTests
{
    [Theory]
    [InlineData(true, false, false, true)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, false, false)]
    [InlineData(false, false, false, false)]
    public void ShouldRunInitialReveal_OnlyForVisibleCollapsedIdlePanel(
        bool settingsVisible,
        bool settingsAnimating,
        bool hasVisiblePanelWidth,
        bool expected)
    {
        var actual = SettingsPanelRevealPolicy.ShouldRunInitialReveal(
            settingsVisible,
            settingsAnimating,
            hasVisiblePanelWidth);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public void ShouldProceedWithMetricsCalculation_ReturnsExpectedDecision(
        bool hasAnyCheckedNodes,
        bool hasCompleteMetricsBaseline,
        bool expected)
    {
        var result = MetricsCalculationPolicy.ShouldProceedWithMetricsCalculation(
            hasAnyCheckedNodes,
            hasCompleteMetricsBaseline);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 4)]
    [InlineData(4, 4)]
    [InlineData(8, 8)]
    [InlineData(16, 16)]
    [InlineData(64, 64)]
    public void GetBaselineWarmupParallelism_RestoresAggressiveWholeProjectThroughput(
        int processorCount,
        int expected)
    {
        var result = MetricsCalculationPolicy.GetBaselineWarmupParallelism(processorCount);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(4, 3)]
    [InlineData(8, 7)]
    [InlineData(16, 8)]
    [InlineData(64, 8)]
    public void GetSelectionRecoveryParallelism_LeavesUiHeadroom_AndClampsFanOut(
        int processorCount,
        int expected)
    {
        var result = MetricsCalculationPolicy.GetSelectionRecoveryParallelism(processorCount);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void InitialVisualReadyTimeout_CoversWorstCaseSettingsRevealChoreography()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(1400), MetricsCalculationPolicy.InitialVisualReadyTimeout);
    }

    [Fact]
    public async Task WaitForInitialVisualReadyAsync_PendingTask_WaitsForCompletion()
    {
        var visualReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waitTask = MetricsCalculationPolicy.WaitForInitialVisualReadyAsync(
            visualReady.Task,
            TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken);

        Assert.False(waitTask.IsCompleted);

        visualReady.SetResult();
        await waitTask;
    }

    [Fact]
    public async Task WaitForInitialVisualReadyAsync_Timeout_AllowsBackgroundWorkToContinue()
    {
        var visualReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await MetricsCalculationPolicy.WaitForInitialVisualReadyAsync(
            visualReady.Task,
            TimeSpan.FromMilliseconds(10),
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task WaitForInitialVisualReadyAsync_FaultedAnimation_AllowsBackgroundWorkToContinue()
    {
        var failedAnimation = Task.FromException(new InvalidOperationException("animation failed"));

        await MetricsCalculationPolicy.WaitForInitialVisualReadyAsync(
            failedAnimation,
            TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task WaitForInitialVisualReadyAsync_ProjectCancellation_StopsBackgroundWork()
    {
        var visualReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            MetricsCalculationPolicy.WaitForInitialVisualReadyAsync(
                visualReady.Task,
                TimeSpan.FromSeconds(1),
                cancellation.Token));
    }
}
