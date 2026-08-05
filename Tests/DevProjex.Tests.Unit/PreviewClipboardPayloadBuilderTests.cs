using DevProjex.Application.Preview;
using DevProjex.Application.Secrets;

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

    [Fact]
    public void BuildSectionPayload_RedactedSection_ReturnsOnlyTheRequestedSection()
    {
        const string documentText =
            "config.txt:\n\u00A0\ntoken=DEVPROJEX_REDACTED[github-pat#1]";
        using var document = new InMemoryPreviewTextDocument(
            documentText,
            [new PreviewDocumentSection("config.txt", 1, 3, 1, 3)],
            [new PreviewRedactionSpan("occurrence", "github-pat", 3, 6, 38, SecretPreviewSpanState.Redacted)]);

        var payload = PreviewClipboardPayloadBuilder.BuildSectionPayload(document, document.Sections[0]);

		Assert.Equal(
			string.Join(Environment.NewLine, "config.txt:", "\u00A0", "token=DEVPROJEX_REDACTED[github-pat#1]"),
			payload);
    }

    [Fact]
    public void BuildSelectionPayload_IntersectingRedaction_ReturnsOnlySelectedText()
    {
        using var document = new InMemoryPreviewTextDocument(
			"value=DEVPROJEX_REDACTED[aws-access-token#1]",
            redactions:
            [
                new PreviewRedactionSpan(
                    "occurrence",
                    "aws-access-token",
					1,
                    6,
                    45,
                    SecretPreviewSpanState.Redacted)
			]);

        var payload = PreviewClipboardPayloadBuilder.BuildSelectionPayload(
            document,
			1,
            6,
			1,
            51,
            "DEVPROJEX_REDACTED[aws-access-token#1]");

		Assert.Equal("DEVPROJEX_REDACTED[aws-access-token#1]", payload);
    }

    [Fact]
    public void BuildSelectionPayload_KeptValue_ReturnsOnlySelectedText()
    {
        using var document = new InMemoryPreviewTextDocument(
			"original-value",
            redactions:
			[new PreviewRedactionSpan("occurrence", "generic-api-key", 1, 0, 14, SecretPreviewSpanState.KeptAsIs)]);

        var payload = PreviewClipboardPayloadBuilder.BuildSelectionPayload(
            document,
			1,
            0,
			1,
            14,
            "original-value");

        Assert.Equal("original-value", payload);
    }

    private sealed class StubFileContentAnalyzer(IReadOnlyDictionary<string, TextFileContent?> contentByPath)
        : IFileContentAnalyzer
    {
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
