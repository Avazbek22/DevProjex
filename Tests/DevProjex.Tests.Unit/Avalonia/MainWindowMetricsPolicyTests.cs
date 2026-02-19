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
        var method = typeof(MainWindow).GetMethod(
            "ShouldProceedWithMetricsCalculation",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var result = (bool)method!.Invoke(null, [hasAnyCheckedNodes, hasCompleteMetricsBaseline])!;
        Assert.Equal(expected, result);
    }
}
