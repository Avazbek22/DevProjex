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

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void Calculate_UnicodeAndEmptyLines_PreservesPartialAndOutOfBoundsRanges(bool fileBacked)
	{
		using var directory = new TemporaryDirectory();
		using var document = CreateDocument(directory, fileBacked, "😀alpha\r\n\r\nβeta 文書\r\ngamma");
		(PreviewSelectionRange Range, ExportOutputMetrics Expected)[] cases =
		[
			(new(1, 1, 4, 3), new(4, 19, 5)),
			(new(4, 3, 1, 1), new(4, 19, 5)),
			(new(2, 0, 3, 5), new(2, 6, 2)),
			(new(3, -5, 3, 100), new(1, 7, 2)),
			(new(3, 7, 4, 0), new(2, 1, 1)),
			(new(2, 0, 2, 99), ExportOutputMetrics.Empty),
			(new(1, 100, 4, 100), new(4, 15, 4)),
			(new(3, 2, 6, 3), new(4, 21, 6)),
			(new(6, 1, 7, 3), new(2, 8, 2))
		];

		foreach (var (range, expected) in cases)
		{
			Assert.Equal(expected, PreviewSelectionMetricsCalculator.Calculate(
				document,
				range,
				TestContext.Current.CancellationToken));
		}
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void Calculate_CanceledSelection_ThrowsWithTheSuppliedToken(bool fileBacked)
	{
		using var directory = new TemporaryDirectory();
		using var document = CreateDocument(directory, fileBacked, "alpha\r\nbeta");
		using var cancellation = new CancellationTokenSource();
		cancellation.Cancel();

		var exception = Assert.ThrowsAny<OperationCanceledException>(() =>
			PreviewSelectionMetricsCalculator.Calculate(
				document,
				new PreviewSelectionRange(1, 0, 2, 4),
				cancellation.Token));

		Assert.Equal(cancellation.Token, exception.CancellationToken);
	}

	private static IPreviewTextDocument CreateDocument(
		TemporaryDirectory directory,
		bool fileBacked,
		string text)
	{
		if (!fileBacked)
			return new InMemoryPreviewTextDocument(text);

		var bytes = Encoding.UTF8.GetBytes(text);
		var path = directory.CreateBinaryFile("selection.preview.txt", bytes);
		var offsets = new List<long> { 0 };
		for (var index = 0; index < bytes.Length; index++)
		{
			if (bytes[index] == '\n')
				offsets.Add(index + 1);
		}
		return new FileBackedPreviewTextDocument(path, offsets.ToArray(), bytes.Length, text.Length, text.Length);
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
