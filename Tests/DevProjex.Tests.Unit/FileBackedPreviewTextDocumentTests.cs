using DevProjex.Application.Preview;

namespace DevProjex.Tests.Unit;

public sealed class FileBackedPreviewTextDocumentTests
{
    [Fact]
    public void GetLineText_ClampsIndexesAndTrimsLineTerminators()
    {
        using var temp = new TemporaryDirectory();
        using var document = CreateDocument(
            temp,
            ("alpha\r", "alpha"),
            ("", string.Empty),
            ("gamma", "gamma"));

        Assert.Equal(3, document.LineCount);
        Assert.Equal("alpha", document.GetLineText(1));
        Assert.Equal(string.Empty, document.GetLineText(2));
        Assert.Equal("gamma", document.GetLineText(3));
        Assert.Equal("alpha", document.GetLineText(0));
        Assert.Equal("gamma", document.GetLineText(99));
    }

    [Fact]
    public void GetLineRangeText_AndDispose_PreserveContentAndCleanUpStorage()
    {
        using var temp = new TemporaryDirectory();
        var (document, storagePath) = CreateDocumentWithPath(
            temp,
            ("alpha", "alpha"),
            ("", string.Empty),
            ("gamma", "gamma"));

        Assert.Equal("alpha\n\ngamma", document.GetLineRangeText(1, 99));

        document.Dispose();

        Assert.False(File.Exists(storagePath));
        Assert.Throws<ObjectDisposedException>(() => document.GetLineText(1));
    }

	[Fact]
	public void VisitLines_StreamsUnicodeAcrossChunksAndStopsAtVisitorBoundary()
	{
		using var temp = new TemporaryDirectory();
		var lines = Enumerable.Range(0, 2_050)
			.Select(static index =>
			{
				var visible = $"文書-{index:D4}";
				return (RawLine: index % 2 == 0 ? visible : visible + "\r", VisibleLine: visible);
			})
			.ToArray();
		using var document = CreateDocument(temp, lines);
		var visited = new List<(int Line, string Text)>();

		document.VisitLines(
			2,
			document.LineCount,
			(lineNumber, line) =>
			{
				visited.Add((lineNumber, line.ToString()));
				return lineNumber < 1_500;
			},
			TestContext.Current.CancellationToken);

		Assert.Equal(1_499, visited.Count);
		Assert.Equal((2, "文書-0001"), visited[0]);
		Assert.Equal((1_500, "文書-1499"), visited[^1]);
	}

	[Fact]
	public void VisitLines_EmptyDocument_VisitsTheLogicalEmptyLine()
	{
		using var temp = new TemporaryDirectory();
		using var document = CreateDocument(temp);
		var visited = new List<(int Line, string Text)>();

		document.VisitLines(
			1,
			1,
			(lineNumber, line) =>
			{
				visited.Add((lineNumber, line.ToString()));
				return true;
			},
			TestContext.Current.CancellationToken);

		Assert.Equal([(1, string.Empty)], visited);
	}

	[Fact]
	public void VisitLines_NonIntersectingOrInvertedRange_DoesNotVisitLines()
	{
		using var temp = new TemporaryDirectory();
		using var document = CreateDocument(temp, ("alpha", "alpha"));
		var visits = 0;

		document.VisitLines(
			2,
			1,
			(_, _) =>
			{
				visits++;
				return true;
			},
			TestContext.Current.CancellationToken);
		document.VisitLines(
			2,
			3,
			(_, _) =>
			{
				visits++;
				return true;
			},
			TestContext.Current.CancellationToken);

		Assert.Equal(0, visits);
	}

    private static FileBackedPreviewTextDocument CreateDocument(
        TemporaryDirectory temp,
        params (string RawLine, string VisibleLine)[] lines)
        => CreateDocumentWithPath(temp, lines).Document;

    private static (FileBackedPreviewTextDocument Document, string StoragePath) CreateDocumentWithPath(
        TemporaryDirectory temp,
        params (string RawLine, string VisibleLine)[] lines)
    {
        var storagePath = Path.Combine(temp.Path, $"{Guid.NewGuid():N}.preview.txt");
        var lineOffsets = new long[lines.Length];
        long currentOffset = 0;

        using (var stream = new FileStream(storagePath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            for (var i = 0; i < lines.Length; i++)
            {
                lineOffsets[i] = currentOffset;
                var bytes = Encoding.UTF8.GetBytes(lines[i].RawLine);
                stream.Write(bytes, 0, bytes.Length);
                stream.WriteByte((byte)'\n');
                currentOffset += bytes.Length + 1;
            }
        }

        var document = new FileBackedPreviewTextDocument(
            storagePath,
            lineOffsets,
            currentOffset,
            lines.Length == 0 ? 0 : lines.Max(static line => line.VisibleLine.Length),
            lines.Sum(static line => line.RawLine.Length + 1L));

        return (document, storagePath);
    }
}
