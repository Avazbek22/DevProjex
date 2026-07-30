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

    [Fact]
    public void Calculate_LargeSelectionBeyondInt32Chars_RemainsExact()
    {
        using var document = new RepeatedLinePreviewDocument(lineCount: 30_000, lineLength: 100_000);

        var metrics = PreviewSelectionMetricsCalculator.Calculate(
            document,
            new PreviewSelectionRange(1, 0, 30_000, 100_000),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(new ExportOutputMetrics(30_000, 3_000_029_999, 750_007_500), metrics);
        Assert.True(metrics.Chars > int.MaxValue);
    }

    private sealed class RepeatedLinePreviewDocument(int lineCount, int lineLength) : IPreviewTextDocument
    {
        private readonly string _line = new('x', lineLength);

        public int LineCount { get; } = lineCount;

        public int MaxLineLength => _line.Length;

        public long CharacterCount => ((long)_line.Length * LineCount) + Math.Max(0, LineCount - 1);

        public IReadOnlyList<PreviewDocumentSection> Sections => [];

        public string GetFullText() =>
            throw new NotSupportedException("This synthetic document intentionally exceeds in-memory string limits.");

        public string GetLineText(int lineNumber) => _line;

        public string GetLineRangeText(int firstLine, int lastLine) =>
            throw new NotSupportedException();

        public void Dispose()
        {
        }
    }
}
