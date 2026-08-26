using DevProjex.Application.Compression;
using DevProjex.Application.Preview;
using DevProjex.Application.Secrets;
using DevProjex.Infrastructure.Secrets;

namespace DevProjex.Tests.Unit.Avalonia;

public sealed class ProjectTextOutputPipelineTests
{
	[Theory]
	[InlineData((int)ProjectTextOutputMode.Content)]
	[InlineData((int)ProjectTextOutputMode.TreeAndContent)]
	public async Task BuildDocumentAsync_LargeFileBackedOutputMatchesLegacyBytes(int modeValue)
	{
		using var project = new TemporaryDirectory();
		var sourceFile = Path.Combine(project.Path, "large.txt");
		var content = string.Concat(
			Enumerable.Repeat("ASCII\r\nПривет🙂\rline\n", 32_000)) +
			"tail";
		await File.WriteAllTextAsync(
			sourceFile,
			content,
			new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
			TestContext.Current.CancellationToken);
		var root = DirectoryNode(project.Path, FileNode(sourceFile));
		var snapshot = CreateSnapshot(
			project.Path,
			root,
			new HashSet<string>(PathComparer.Default));
		var pipeline = CreatePipeline();
		var mode = (ProjectTextOutputMode)modeValue;

		var legacy = await pipeline.BuildAsync(
			mode,
			snapshot,
			TestContext.Current.CancellationToken);
		using var streamed = await pipeline.BuildDocumentAsync(
			mode,
			snapshot,
			TestContext.Current.CancellationToken);
		Assert.IsType<FileBackedPreviewTextDocument>(streamed.Document);
		await using var legacyBytes = new MemoryStream();
		await using var streamedBytes = new MemoryStream();
		var writer = new TextFileExportService();
		await writer.WriteAsync(
			legacyBytes,
			legacy.Content,
			TestContext.Current.CancellationToken);
		await writer.WriteAsync(
			streamedBytes,
			streamed.Document,
			TestContext.Current.CancellationToken);

		Assert.Equal(legacyBytes.ToArray(), streamedBytes.ToArray());
	}

	[Fact]
	public async Task BuildAsync_ContentUsesTheOriginalPerFilePathFormat()
	{
		using var project = new TemporaryDirectory();
		var sourceFile = project.CreateFile(Path.Combine("src", "Program.cs"), "class Program {}");
		var root = DirectoryNode(project.Path, DirectoryNode(Path.GetDirectoryName(sourceFile)!, FileNode(sourceFile)));
		var displayRoot = "https://github.com/owner/repository";
		var snapshot = CreateSnapshot(project.Path, root, new HashSet<string>(PathComparer.Default)) with
		{
			PathPresentation = new ExportPathPresentation(
				displayRoot,
				_ => $"{displayRoot}/src/Program.cs")
		};

		var result = await CreatePipeline().BuildAsync(
			ProjectTextOutputMode.Content,
			snapshot,
			TestContext.Current.CancellationToken);

		Assert.Equal(
			$"{displayRoot}/src/Program.cs:{Environment.NewLine}" +
			$"\u00A0{Environment.NewLine}" +
			"class Program {}",
			result.Content);
	}

	[Theory]
	[InlineData((int)ProjectTextOutputMode.Tree)]
	[InlineData((int)ProjectTextOutputMode.Content)]
	[InlineData((int)ProjectTextOutputMode.TreeAndContent)]
	public async Task BuildAsync_PrivateDataMasksDisplayRootInEveryMode(int modeValue)
	{
		using var project = new TemporaryDirectory();
		var sourceFile = project.CreateFile("Program.cs", "class Program {}");
		var root = DirectoryNode(project.Path, FileNode(sourceFile));
		using var session = SecretRedactionSession.CreateWithPrivateData(
			new NoFindingsDetector(),
			new PrivateDataDetector());
		var displayRoot = @"C:\Users\alice\repository";
		var snapshot = CreateSnapshot(project.Path, root, new HashSet<string>(PathComparer.Default)) with
		{
			PathPresentation = new ExportPathPresentation(
				displayRoot,
				_ => $@"{displayRoot}\Program.cs"),
			RedactionContext = new ContentTransformationContext(
				Compression: null,
				Redaction: new SecretRedactionContext(
					project.Path,
					session,
					SecretRedactionFeatures.PrivateData))
		};

		var result = await CreatePipeline().BuildAsync(
			(ProjectTextOutputMode)modeValue,
			snapshot,
			TestContext.Current.CancellationToken);

		Assert.Contains(@"C:\Users\[local-user-1]\repository", result.Content, StringComparison.Ordinal);
		Assert.DoesNotContain(displayRoot, result.Content, StringComparison.Ordinal);
	}

	[Fact]
	public async Task BuildAsync_DisabledPrivateDataLeavesDisplayRootUnchanged()
	{
		using var project = new TemporaryDirectory();
		var sourceFile = project.CreateFile("Program.cs", "class Program {}");
		var root = DirectoryNode(project.Path, FileNode(sourceFile));
		var displayRoot = @"C:\Users\alice\repository";
		var snapshot = CreateSnapshot(project.Path, root, new HashSet<string>(PathComparer.Default)) with
		{
			PathPresentation = new ExportPathPresentation(displayRoot, _ => $@"{displayRoot}\Program.cs")
		};

		var result = await CreatePipeline().BuildAsync(
			ProjectTextOutputMode.Content,
			snapshot,
			TestContext.Current.CancellationToken);

		Assert.StartsWith($@"{displayRoot}\Program.cs:{Environment.NewLine}", result.Content, StringComparison.Ordinal);
		Assert.DoesNotContain("[local-user-1]", result.Content, StringComparison.Ordinal);
	}
    [Theory]
    [InlineData((int)ProjectTextOutputMode.Tree)]
    [InlineData((int)ProjectTextOutputMode.Content)]
    [InlineData((int)ProjectTextOutputMode.TreeAndContent)]
    public async Task BuildAsync_CheckedRootMatchesImplicitFullTree(
        int modeValue)
    {
        using var temp = new TemporaryDirectory();
        var sourceFile = temp.CreateFile(
            Path.Combine("src", "Program.cs"),
            "class Program {}");
        var root = DirectoryNode(
            temp.Path,
            DirectoryNode(
                Path.GetDirectoryName(sourceFile)!,
                FileNode(sourceFile)));
        var pipeline = CreatePipeline();
        var implicitResult = await pipeline.BuildAsync(
            (ProjectTextOutputMode)modeValue,
            CreateSnapshot(
                temp.Path,
                root,
                new HashSet<string>(PathComparer.Default)),
            TestContext.Current.CancellationToken);
        var checkedRootResult = await pipeline.BuildAsync(
            (ProjectTextOutputMode)modeValue,
            CreateSnapshot(
                temp.Path,
                root,
                new HashSet<string>(PathComparer.Default)
                {
                    root.FullPath
                }),
            TestContext.Current.CancellationToken);

        Assert.Equal(implicitResult, checkedRootResult);
    }

    [Fact]
    public async Task BuildAsync_SelectedDirectoryIncludesLazyDescriptorDescendantsInBothContentModes()
    {
        using var temp = new TemporaryDirectory();
        var sourceDirectory = temp.CreateFolder("src");
        var sourceFile = temp.CreateFile(Path.Combine("src", "Program.cs"), "class Program {}");
        var root = DirectoryNode(
            temp.Path,
            DirectoryNode(sourceDirectory, FileNode(sourceFile)));
        var snapshot = CreateSnapshot(
            temp.Path,
            root,
            new HashSet<string>(PathComparer.Default) { sourceDirectory });
        var pipeline = CreatePipeline();

        var content = await pipeline.BuildAsync(
            ProjectTextOutputMode.Content,
            snapshot,
            TestContext.Current.CancellationToken);
        var combined = await pipeline.BuildAsync(
            ProjectTextOutputMode.TreeAndContent,
            snapshot,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, content.CandidateFileCount);
        Assert.Contains("class Program {}", content.Content);
        Assert.Contains("src/Program.cs:", combined.Content);
        Assert.Contains("class Program {}", combined.Content);
    }

    [Fact]
    public async Task BuildAsync_ContentSelectionOutsideEffectiveTreeDoesNotReadUnrelatedFile()
    {
        using var temp = new TemporaryDirectory();
        var effectiveFile = temp.CreateFile("effective.txt", "effective");
        var outsideFile = temp.CreateFile("outside.txt", "outside");
        var root = DirectoryNode(temp.Path, FileNode(effectiveFile));
        var snapshot = CreateSnapshot(
            temp.Path,
            root,
            new HashSet<string>(PathComparer.Default) { outsideFile });
        var pipeline = CreatePipeline();

        var result = await pipeline.BuildAsync(
            ProjectTextOutputMode.Content,
            snapshot,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, result.CandidateFileCount);
        Assert.Empty(result.Content);
    }

    [Fact]
    public async Task BuildAsync_TreeSelectionOutsideEffectiveTreeFallsBackToCompleteSnapshot()
    {
        using var temp = new TemporaryDirectory();
        var effectiveFile = temp.CreateFile("effective.txt", "effective");
        var root = DirectoryNode(temp.Path, FileNode(effectiveFile));
        var snapshot = CreateSnapshot(
            temp.Path,
            root,
            new HashSet<string>(PathComparer.Default) { Path.Combine(temp.Path, "missing.txt") });
        var pipeline = CreatePipeline();

        var result = await pipeline.BuildAsync(
            ProjectTextOutputMode.Tree,
            snapshot,
            TestContext.Current.CancellationToken);

        Assert.Contains("effective.txt", result.Content);
    }

    [Theory]
    [InlineData((int)ProjectTextOutputMode.Tree)]
    [InlineData((int)ProjectTextOutputMode.Content)]
    [InlineData((int)ProjectTextOutputMode.TreeAndContent)]
    public async Task BuildAsync_PreCanceledRequestDoesNotProduceOutput(int modeValue)
    {
        var mode = (ProjectTextOutputMode)modeValue;
        var rootPath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "project-text-output-canceled"));
        var snapshot = CreateSnapshot(
            rootPath,
            DirectoryNode(rootPath),
            new HashSet<string>(PathComparer.Default));
        var pipeline = CreatePipeline();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => pipeline.BuildAsync(mode, snapshot, cancellation.Token));
    }

    private static ProjectTextOutputPipeline CreatePipeline()
    {
        var treeExport = new TreeExportService();
        var contentExport = new SelectedContentExportService(new FileContentAnalyzer());
        return new ProjectTextOutputPipeline(
            treeExport,
            contentExport,
			new TreeAndContentExportService(treeExport, contentExport),
			new PreviewDocumentBuilder(new FileContentAnalyzer()),
			new TextFileExportService());
    }

    private static ProjectTextOutputSnapshot CreateSnapshot(
        string rootPath,
        TreeNodeDescriptor root,
        IReadOnlySet<string> selectedPaths) =>
        new(
            rootPath,
            root,
            selectedPaths,
            OrderedFilePaths: null,
            TreeTextFormat.Ascii,
            PathPresentation: null);

    private static TreeNodeDescriptor DirectoryNode(
        string path,
        params TreeNodeDescriptor[] children) =>
        new(
            Path.GetFileName(path),
            path,
            IsDirectory: true,
            IsAccessDenied: false,
            IconKey: "folder",
            Children: children);

    private static TreeNodeDescriptor FileNode(string path) =>
        new(
            Path.GetFileName(path),
            path,
            IsDirectory: false,
            IsAccessDenied: false,
            IconKey: "file",
            Children: []);

	private sealed class NoFindingsDetector : ISecretDetector
	{
		public IReadOnlyList<DetectedSecret> Detect(
			string repositoryRelativePath,
			string content,
			CancellationToken cancellationToken = default) => [];
	}
}
