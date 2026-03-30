using DevProjex.Application.Preview;

namespace DevProjex.Tests.Unit;

public sealed class PreviewClipboardPayloadBuilderTests
{
    [Fact]
    public void BuildFullDocumentPayload_NullDocument_ReturnsEmpty()
    {
        var payload = PreviewClipboardPayloadBuilder.BuildFullDocumentPayload(document: null);

        Assert.Equal(string.Empty, payload);
    }

    [Fact]
    public void BuildFullDocumentPayload_InMemoryDocument_ReturnsEntireText()
    {
        using var document = new InMemoryPreviewTextDocument("alpha\nbeta\n\ngamma");

        var payload = PreviewClipboardPayloadBuilder.BuildFullDocumentPayload(document);

        Assert.Equal(string.Join(Environment.NewLine, "alpha", "beta", string.Empty, "gamma"), payload);
    }

    [Fact]
    public async Task BuildFullDocumentPayload_FileBackedDocument_ReturnsEntireText()
    {
        using var temp = new TemporaryDirectory();
        var largeFile = temp.CreateFile("large.txt", string.Empty);
        var largeContent = new string('x', 600_000);
        var analyzer = new StubFileContentAnalyzer(new Dictionary<string, TextFileContent?>
        {
            [largeFile] = new TextFileContent(
                Content: largeContent,
                SizeBytes: largeContent.Length,
                LineCount: 1,
                CharCount: largeContent.Length,
                IsEmpty: false,
                IsWhitespaceOnly: false)
        });
        var builder = new PreviewDocumentBuilder(analyzer);

        using var document = await builder.BuildContentDocumentAsync([largeFile], CancellationToken.None, Path.GetFileName);

        var payload = PreviewClipboardPayloadBuilder.BuildFullDocumentPayload(document);
        var expectedPrefix = string.Join(Environment.NewLine, "large.txt:", "\u00A0", string.Empty);

        Assert.StartsWith(expectedPrefix, payload, StringComparison.Ordinal);
        Assert.EndsWith(largeContent, payload, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSectionPayload_ReturnsOnlyRequestedSection()
    {
        const string documentText = "alpha.txt:\n\u00A0\nalpha\nbeta\n\u00A0\n\u00A0\nbeta.txt:\n\u00A0\ngamma";
        using var document = new InMemoryPreviewTextDocument(
            documentText,
            [
                new PreviewDocumentSection("alpha.txt", 1, 4, 1, 3),
                new PreviewDocumentSection("beta.txt", 7, 9, 7, 9)
            ]);

        var payload = PreviewClipboardPayloadBuilder.BuildSectionPayload(document, document.Sections[1]);

        Assert.Equal(string.Join(Environment.NewLine, "beta.txt:", "\u00A0", "gamma"), payload);
    }

    private sealed class StubFileContentAnalyzer(IReadOnlyDictionary<string, TextFileContent?> contentByPath)
        : IFileContentAnalyzer
    {
        public Task<bool> IsTextFileAsync(string path, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<TextFileMetrics?> GetTextFileMetricsAsync(string path, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<TextFileContent?> TryReadAsTextAsync(string path, CancellationToken cancellationToken = default)
        {
            contentByPath.TryGetValue(path, out var content);
            return Task.FromResult(content);
        }

        public Task<TextFileContent?> TryReadAsTextAsync(
            string path,
            long maxSizeForFullRead,
            CancellationToken cancellationToken = default)
            => TryReadAsTextAsync(path, cancellationToken);
    }
}
