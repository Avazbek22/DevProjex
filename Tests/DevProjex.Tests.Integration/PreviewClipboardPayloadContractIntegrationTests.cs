namespace DevProjex.Tests.Integration;

public sealed class PreviewClipboardPayloadContractIntegrationTests
{
    [Fact]
    public async Task BuildFullDocumentPayload_ContentPreview_MatchesSelectedContentExport()
    {
        using var temp = new TemporaryDirectory();
        var alphaPath = temp.CreateFile("src\\alpha.txt", "alpha\nbeta\n");
        var betaPath = temp.CreateFile("docs\\beta.txt", "gamma");

        var analyzer = new FileContentAnalyzer();
        var previewBuilder = new PreviewDocumentBuilder(analyzer);

        using var document = await previewBuilder.BuildContentDocumentAsync(
            [betaPath, alphaPath],
            CancellationToken.None,
            displayPathMapper: null);

        var expected = string.Join(
            Environment.NewLine,
            betaPath + ":",
            "\u00A0",
            "gamma",
            "\u00A0",
            "\u00A0",
            alphaPath + ":",
            "\u00A0",
            "alpha",
            "beta");
        var actual = PreviewClipboardPayloadBuilder.BuildFullDocumentPayload(document);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void BuildFullDocumentPayload_TreePreview_MatchesTreeExport()
    {
        using var temp = new TemporaryDirectory();
        var alphaPath = temp.CreateFile("src\\alpha.txt", "alpha");
        var treeRoot = CreateSampleTree(temp.Path, alphaPath);
        var treeExport = new TreeExportService();
        var previewBuilder = new PreviewDocumentBuilder(new FileContentAnalyzer());

        var treeText = treeExport.BuildFullTree(temp.Path, treeRoot, TreeTextFormat.Ascii);
        using var document = previewBuilder.CreateInMemory(treeText);

        var actual = PreviewClipboardPayloadBuilder.BuildFullDocumentPayload(document);
        var expected = NormalizeForClipboard(treeText);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task BuildFullDocumentPayload_TreeAndContentPreview_MatchesCombinedExport()
    {
        using var temp = new TemporaryDirectory();
        var alphaPath = temp.CreateFile("src\\alpha.txt", "alpha\nbeta\n");
        var betaPath = temp.CreateFile("docs\\beta.txt", "gamma");
        var treeRoot = CreateSampleTree(temp.Path, alphaPath, betaPath);

        var analyzer = new FileContentAnalyzer();
        var previewBuilder = new PreviewDocumentBuilder(analyzer);
        var treeExport = new TreeExportService();

        var treeText = treeExport.BuildFullTree(temp.Path, treeRoot, TreeTextFormat.Ascii);
        using var document = await previewBuilder.BuildTreeAndContentDocumentAsync(
            treeText,
            [alphaPath, betaPath],
            CancellationToken.None,
            TreeAndContentExportService.CreateRelativeContentHeaderPathMapper(temp.Path));

        var expected = new TreeAndContentExportService(
                treeExport,
                new SelectedContentExportService(analyzer))
            .Build(temp.Path, treeRoot, new HashSet<string>(), TreeTextFormat.Ascii);
        var actual = PreviewClipboardPayloadBuilder.BuildFullDocumentPayload(document);

        Assert.Equal(NormalizeForClipboard(expected), actual);
        Assert.Contains("docs/beta.txt:", actual, StringComparison.Ordinal);
        Assert.Contains("src/alpha.txt:", actual, StringComparison.Ordinal);
        Assert.DoesNotContain(alphaPath.Replace('\\', '/'), actual.Replace('\\', '/'), StringComparison.Ordinal);
        Assert.DoesNotContain(betaPath.Replace('\\', '/'), actual.Replace('\\', '/'), StringComparison.Ordinal);
    }

    private static TreeNodeDescriptor CreateSampleTree(string rootPath, params string[] filePaths)
    {
        var rootName = Path.GetFileName(rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var srcDirectoryPath = Path.Combine(rootPath, "src");
        var docsDirectoryPath = Path.Combine(rootPath, "docs");

        var srcChildren = filePaths
            .Where(path => path.StartsWith(srcDirectoryPath, PathInternalComparison()))
            .Select(path => new TreeNodeDescriptor(Path.GetFileName(path), path, false, false, "text", []))
            .ToArray();
        var docsChildren = filePaths
            .Where(path => path.StartsWith(docsDirectoryPath, PathInternalComparison()))
            .Select(path => new TreeNodeDescriptor(Path.GetFileName(path), path, false, false, "text", []))
            .ToArray();

        return new TreeNodeDescriptor(
            rootName,
            rootPath,
            true,
            false,
            "folder",
            [
                new TreeNodeDescriptor("docs", docsDirectoryPath, true, false, "folder", docsChildren),
                new TreeNodeDescriptor("src", srcDirectoryPath, true, false, "folder", srcChildren)
            ]);
    }

    private static StringComparison PathInternalComparison() =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static string NormalizeForClipboard(string text)
    {
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        return Environment.NewLine == "\n"
            ? normalized
            : normalized.Replace("\n", Environment.NewLine, StringComparison.Ordinal);
    }
}
