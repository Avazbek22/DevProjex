using DevProjex.Application.Preview;

namespace DevProjex.Tests.Unit;

public sealed class PreviewDocumentBuilderTests
{
    private const string BlankLine = "\u00A0";

    [Fact]
    public async Task BuildContentDocumentAsync_NoReadableFiles_ReturnsNull()
    {
        using var temp = new TemporaryDirectory();
        var path = temp.CreateFile("missing.txt", "ignored");
        var analyzer = new StubFileContentAnalyzer();
        var builder = new PreviewDocumentBuilder(analyzer);

        var document = await builder.BuildContentDocumentAsync([path], CancellationToken.None, null);

        Assert.Null(document);
        Assert.Equal([path], analyzer.RequestedPaths);
    }

    [Fact]
    public async Task BuildContentDocumentAsync_FormatsRegularWhitespaceAndEmptyEntries()
    {
        using var temp = new TemporaryDirectory();
        var alphaPath = temp.CreateFile("alpha.txt", string.Empty);
        var whitespacePath = temp.CreateFile("whitespace.txt", string.Empty);
        var emptyPath = temp.CreateFile("empty.txt", string.Empty);

        var analyzer = new StubFileContentAnalyzer(new Dictionary<string, TextFileContent?>
        {
            [alphaPath] = CreateTextContent("alpha\r\nbeta\r\n"),
            [whitespacePath] = new TextFileContent("   ", 3, 1, 3, false, true),
            [emptyPath] = new TextFileContent(string.Empty, 0, 0, 0, true, false)
        });
        var builder = new PreviewDocumentBuilder(analyzer);

        using var document = await builder.BuildContentDocumentAsync(
            [whitespacePath, emptyPath, alphaPath],
            CancellationToken.None,
            Path.GetFileName);

        Assert.NotNull(document);
        Assert.IsType<InMemoryPreviewTextDocument>(document);
        Assert.Equal(14, document.LineCount);
        Assert.Equal(
            string.Join(
                '\n',
                "alpha.txt:",
                BlankLine,
                "alpha",
                "beta",
                BlankLine,
                BlankLine,
                "empty.txt:",
                BlankLine,
                "[No Content, 0 bytes]",
                BlankLine,
                BlankLine,
                "whitespace.txt:",
                BlankLine,
                "[Whitespace, 3 bytes]"),
            document.GetLineRangeText(1, document.LineCount));
    }

    [Fact]
    public async Task BuildContentDocumentAsync_FinalEstimatedEntry_DoesNotLeaveTrailingEmptyLine()
    {
        using var temp = new TemporaryDirectory();
        var estimatedPath = temp.CreateFile("estimate.txt", string.Empty);

        var analyzer = new StubFileContentAnalyzer(new Dictionary<string, TextFileContent?>
        {
            [estimatedPath] = new TextFileContent(
                Content: string.Empty,
                SizeBytes: 25_000_000,
                LineCount: 10,
                CharCount: 2000,
                IsEmpty: false,
                IsWhitespaceOnly: false,
                IsEstimated: true)
        });
        var builder = new PreviewDocumentBuilder(analyzer);

        using var document = await builder.BuildContentDocumentAsync(
            [estimatedPath],
            CancellationToken.None,
            Path.GetFileName);

        Assert.NotNull(document);
        Assert.Equal(2, document.LineCount);
        Assert.Equal(
            string.Join('\n', "estimate.txt:", BlankLine),
            document.GetLineRangeText(1, document.LineCount));
    }

    [Fact]
    public async Task BuildContentDocumentAsync_LargePayload_UsesFileBackedDocument()
    {
        using var temp = new TemporaryDirectory();
        var largePath = temp.CreateFile("large.txt", string.Empty);
        var largeContent = new string('x', 600_000);

        var analyzer = new StubFileContentAnalyzer(new Dictionary<string, TextFileContent?>
        {
            [largePath] = CreateTextContent(largeContent)
        });
        var builder = new PreviewDocumentBuilder(analyzer);

        using var document = await builder.BuildContentDocumentAsync(
            [largePath],
            CancellationToken.None,
            Path.GetFileName);

        var fileBacked = Assert.IsType<FileBackedPreviewTextDocument>(document);
        Assert.Equal(3, fileBacked.LineCount);
        Assert.Equal("large.txt:", fileBacked.GetLineText(1));
        Assert.Equal(BlankLine, fileBacked.GetLineText(2));
        Assert.Equal(600_000, fileBacked.GetLineText(3).Length);
    }

    [Fact]
    public async Task BuildContentDocumentAsync_PopulatesSectionMetadata()
    {
        using var temp = new TemporaryDirectory();
        var alphaPath = temp.CreateFile("alpha.txt", string.Empty);
        var betaPath = temp.CreateFile("beta.txt", string.Empty);

        var analyzer = new StubFileContentAnalyzer(new Dictionary<string, TextFileContent?>
        {
            [alphaPath] = CreateTextContent("alpha\nbeta"),
            [betaPath] = CreateTextContent("gamma")
        });
        var builder = new PreviewDocumentBuilder(analyzer);

        using var document = await builder.BuildContentDocumentAsync(
            [betaPath, alphaPath],
            CancellationToken.None,
            Path.GetFileName);

        Assert.NotNull(document);
        Assert.Collection(
            document.Sections,
            section =>
            {
                Assert.Equal("alpha.txt", section.DisplayPath);
                Assert.Equal(1, section.StartLine);
                Assert.Equal(4, section.EndLine);
                Assert.Equal(1, section.HeaderLine);
                Assert.Equal(3, section.ContentStartLine);
            },
            section =>
            {
                Assert.Equal("beta.txt", section.DisplayPath);
                Assert.Equal(7, section.StartLine);
                Assert.Equal(9, section.EndLine);
                Assert.Equal(7, section.HeaderLine);
                Assert.Equal(9, section.ContentStartLine);
            });
    }

    [Fact]
    public async Task BuildTreeAndContentDocumentAsync_WithoutFiles_ReturnsTrimmedTreeText()
    {
        var builder = new PreviewDocumentBuilder(new StubFileContentAnalyzer());

        using var document = await builder.BuildTreeAndContentDocumentAsync(
            "root\r\n  child\r\n\r\n",
            [],
            CancellationToken.None,
            null);

        Assert.IsType<InMemoryPreviewTextDocument>(document);
        Assert.Equal("root\n  child", document.GetLineRangeText(1, document.LineCount));
    }

    [Fact]
    public async Task BuildTreeAndContentDocumentAsync_WithContent_AddsSectionSeparatorAndMappedPath()
    {
        using var temp = new TemporaryDirectory();
        var filePath = temp.CreateFile("folder\\note.txt", string.Empty);

        var analyzer = new StubFileContentAnalyzer(new Dictionary<string, TextFileContent?>
        {
            [filePath] = CreateTextContent("body")
        });
        var builder = new PreviewDocumentBuilder(analyzer);

        using var document = await builder.BuildTreeAndContentDocumentAsync(
            "root\n  note.txt\n",
            [filePath],
            CancellationToken.None,
            _ => "mapped/note.txt");

        Assert.Equal(
            string.Join(
                '\n',
                "root",
                "  note.txt",
                BlankLine,
                BlankLine,
                "mapped/note.txt:",
                BlankLine,
                "body"),
            document.GetLineRangeText(1, document.LineCount));
        Assert.Collection(
            document.Sections,
            section =>
            {
                Assert.Equal("mapped/note.txt", section.DisplayPath);
                Assert.Equal(5, section.StartLine);
                Assert.Equal(7, section.EndLine);
                Assert.Equal(5, section.HeaderLine);
                Assert.Equal(7, section.ContentStartLine);
            });
    }

    private static TextFileContent CreateTextContent(string content)
    {
        var normalized = content.Replace("\r\n", "\n");
        var lineCount = string.IsNullOrEmpty(normalized)
            ? 0
            : normalized.Count(static ch => ch == '\n') + 1;
        return new TextFileContent(
            Content: content,
            SizeBytes: content.Length,
            LineCount: lineCount,
            CharCount: normalized.Replace("\n", string.Empty).Length,
            IsEmpty: false,
            IsWhitespaceOnly: false);
    }

    private sealed class StubFileContentAnalyzer(IReadOnlyDictionary<string, TextFileContent?> contentByPath)
        : IFileContentAnalyzer
    {
        public StubFileContentAnalyzer() : this(new Dictionary<string, TextFileContent?>())
        {
        }

        public List<string> RequestedPaths { get; } = [];

        public ValueTask<bool> IsTextFileAsync(string path, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<TextFileMetrics?> GetTextFileMetricsAsync(
            string path,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<TextFileContent?> TryReadAsTextAsync(
            string path,
            CancellationToken cancellationToken = default)
        {
            RequestedPaths.Add(path);
            contentByPath.TryGetValue(path, out var content);
            return ValueTask.FromResult(content);
        }

        public ValueTask<TextFileContent?> TryReadAsTextAsync(
            string path,
            long maxSizeForFullRead,
            CancellationToken cancellationToken = default)
            => TryReadAsTextAsync(path, cancellationToken);
    }
}
