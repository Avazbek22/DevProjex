using DevProjex.Avalonia.Services;

namespace DevProjex.Tests.Unit.Avalonia;

public sealed class MainWindowMetricsPolicyTests
{
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
    [InlineData(2, 1)]
    [InlineData(4, 3)]
    [InlineData(8, 7)]
    [InlineData(16, 8)]
    [InlineData(64, 8)]
    public void GetBackgroundMetricsParallelism_LeavesUiHeadroom_AndClampsFanOut(
        int processorCount,
        int expected)
    {
        var result = MetricsCalculationPolicy.GetBackgroundMetricsParallelism(processorCount);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(true, 40)]
    [InlineData(false, 0)]
    public void GetInitialWarmupStartDelay_ReturnsSmallPostPaintDelay(
        bool settingsVisible,
        int expectedDelayMilliseconds)
    {
        var result = MetricsCalculationPolicy.GetInitialWarmupStartDelay(settingsVisible);

        Assert.Equal(TimeSpan.FromMilliseconds(expectedDelayMilliseconds), result);
    }
}
