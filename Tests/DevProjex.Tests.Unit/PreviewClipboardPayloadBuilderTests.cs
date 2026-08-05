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
    public void BuildSectionPayload_RedactedSection_PrependsEmbeddedLegend()
    {
        const string documentText =
            "Values redacted by DevProjex before export: 1.\n" +
            "Placeholders like DEVPROJEX_REDACTED[github-pat#1] mark removed secrets.\n" +
            "Do not treat placeholder text as a real value.\n\u00A0\n" +
            "config.txt:\n\u00A0\ntoken=DEVPROJEX_REDACTED[github-pat#1]";
        using var document = new InMemoryPreviewTextDocument(
            documentText,
            [new PreviewDocumentSection("config.txt", 5, 7, 5, 7)],
            [new PreviewRedactionSpan("occurrence", "github-pat", 7, 6, 38, SecretPreviewSpanState.Redacted)],
            new PreviewRedactionSummary(1, 3));

        var payload = PreviewClipboardPayloadBuilder.BuildSectionPayload(document, document.Sections[0]);

        Assert.StartsWith(
            string.Join(
                Environment.NewLine,
                "Values redacted by DevProjex before export: 1.",
                "Placeholders like DEVPROJEX_REDACTED[github-pat#1] mark removed secrets.",
                "Do not treat placeholder text as a real value.",
                string.Empty),
            payload,
            StringComparison.Ordinal);
        Assert.Contains("token=DEVPROJEX_REDACTED[github-pat#1]", payload, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSelectionPayload_IntersectingRedaction_PrependsLegendOnlyOnce()
    {
        const string legend = "line one\nline two\nline three";
        using var document = new InMemoryPreviewTextDocument(
            $"{legend}\n\u00A0\nvalue=DEVPROJEX_REDACTED[aws-access-token#1]",
            redactions:
            [
                new PreviewRedactionSpan(
                    "occurrence",
                    "aws-access-token",
                    5,
                    6,
                    45,
                    SecretPreviewSpanState.Redacted)
            ],
            redactionSummary: new PreviewRedactionSummary(1, 3));

        var payload = PreviewClipboardPayloadBuilder.BuildSelectionPayload(
            document,
            5,
            6,
            5,
            51,
            "DEVPROJEX_REDACTED[aws-access-token#1]");

        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "line one",
                "line two",
                "line three",
                string.Empty,
                "DEVPROJEX_REDACTED[aws-access-token#1]"),
            payload);
    }

    [Fact]
    public void BuildSelectionPayload_KeptValue_DoesNotAddRedactionLegend()
    {
        using var document = new InMemoryPreviewTextDocument(
            "legend\n\u00A0\noriginal-value",
            redactions:
            [new PreviewRedactionSpan("occurrence", "generic-api-key", 3, 0, 14, SecretPreviewSpanState.KeptAsIs)],
            redactionSummary: new PreviewRedactionSummary(0, 1));

        var payload = PreviewClipboardPayloadBuilder.BuildSelectionPayload(
            document,
            3,
            0,
            3,
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
