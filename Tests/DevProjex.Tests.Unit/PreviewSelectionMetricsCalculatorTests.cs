using DevProjex.Application.Preview;

namespace DevProjex.Tests.Unit;

public sealed class PreviewSelectionMetricsCalculatorTests
{
    [Fact]
    public void Calculate_SingleLineSelection_ReturnsExpectedMetrics()
    {
        using var document = new InMemoryPreviewTextDocument("alpha\nbeta\ngamma");

        var metrics = PreviewSelectionMetricsCalculator.Calculate(
            document,
            new PreviewSelectionRange(1, 1, 1, 4), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(new ExportOutputMetrics(1, 3, 1), metrics);
    }

    [Fact]
    public void Calculate_MultiLineSelection_IncludesNormalizedLineBreaks()
    {
        using var document = new InMemoryPreviewTextDocument("alpha\nbeta\ngamma");

        var metrics = PreviewSelectionMetricsCalculator.Calculate(
            document,
            new PreviewSelectionRange(1, 2, 2, 2), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(new ExportOutputMetrics(2, 6, 2), metrics);
    }

    [Fact]
    public void Calculate_LineBreakOnlySelection_CountsTwoVisualLines()
    {
        using var document = new InMemoryPreviewTextDocument("alpha\nbeta");

        var metrics = PreviewSelectionMetricsCalculator.Calculate(
            document,
            new PreviewSelectionRange(1, 5, 2, 0), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(new ExportOutputMetrics(2, 1, 1), metrics);
    }

    [Fact]
    public void Calculate_ReversedRange_NormalizesSelectionBeforeCounting()
    {
        using var document = new InMemoryPreviewTextDocument("alpha\r\nbeta\r\ngamma");

        var metrics = PreviewSelectionMetricsCalculator.Calculate(
            document,
            new PreviewSelectionRange(3, 2, 1, 3), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(new ExportOutputMetrics(3, 10, 3), metrics);
    }

    [Fact]
    public void Calculate_CollapsedSelection_ReturnsEmptyMetrics()
    {
        using var document = new InMemoryPreviewTextDocument("alpha");

        var metrics = PreviewSelectionMetricsCalculator.Calculate(
            document,
            new PreviewSelectionRange(1, 2, 1, 2), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(ExportOutputMetrics.Empty, metrics);
    }
}
