using DevProjex.Application.Compression;
using DevProjex.Application.Preview;
using DevProjex.Application.Secrets;

namespace DevProjex.Tests.Unit;

public sealed class PreviewDocumentBuilderTests
{
    private const string BlankLine = "\u00A0";

	[Fact]
	public async Task BuildContentDocumentAsync_WithRootHeader_UsesRelativeSectionsAndKeepsCoordinatesAligned()
	{
		using var project = new TemporaryDirectory();
		var path = project.CreateFile(Path.Combine("src", "Program.cs"), "class Program {}");
		var builder = new PreviewDocumentBuilder(new FileContentAnalyzer());

		using var document = await builder.BuildContentDocumentAsync(
			[path],
			TestContext.Current.CancellationToken,
			TreeAndContentExportService.CreateRelativeContentHeaderPathMapper(project.Path),
			displayRootPath: project.Path);

		Assert.NotNull(document);
		Assert.Equal($"{project.Path}:", document.GetLineText(1));
		var section = Assert.Single(document.Sections);
		Assert.Equal("src/Program.cs", section.DisplayPath);
		Assert.Equal("src/Program.cs:", document.GetLineText(section.StartLine));
		Assert.Equal("class Program {}", document.GetLineText(section.ContentStartLine));
	}

	[Fact]
	public async Task BuildContentDocumentAsync_EscapesControlCharactersInGeneratedPaths()
	{
		using var project = new TemporaryDirectory();
		var path = project.CreateFile("Program.cs", "class Program {}");
		var builder = new PreviewDocumentBuilder(new FileContentAnalyzer());

		using var document = await builder.BuildContentDocumentAsync(
			[path],
			TestContext.Current.CancellationToken,
			static _ => "src/line\nbreak\t\u001B.cs",
			displayRootPath: "root\rname");

		Assert.NotNull(document);
		Assert.Equal("root\\rname:", document.GetLineText(1));
		var section = Assert.Single(document.Sections);
		Assert.Equal("src/line\\nbreak\\t\\u001B.cs", section.DisplayPath);
		Assert.Equal($"{section.DisplayPath}:", document.GetLineText(section.StartLine));
	}

	[Fact]
	public async Task BuildContentDocumentAsync_CancellationDuringPathEnumerationStopsBeforeNextRead()
	{
		using var cancellation = new CancellationTokenSource();
		var paths = new CancelThenRejectFurtherEnumeration("unused.txt", cancellation);
		var builder = new PreviewDocumentBuilder(new FileContentAnalyzer());

		await Assert.ThrowsAsync<OperationCanceledException>(() =>
			builder.BuildContentDocumentAsync(
				paths,
				cancellation.Token,
				displayPathMapper: null));
	}

	[Fact]
	public void PreviewStorageScavenger_RemovesOnlyStaleUnlockedOwnedFiles()
	{
		using var storage = new TemporaryDirectory();
		var now = DateTime.UtcNow;
		var stalePath = CreatePreviewStorageFile(storage.Path, now.AddDays(-2));
		var freshPath = CreatePreviewStorageFile(storage.Path, now);
		var activePath = CreatePreviewStorageFile(storage.Path, now.AddDays(-2));
		var unrelatedPath = storage.CreateFile("unrelated.preview.txt", "preserve");
		using var activeLease = new FileStream(
			activePath,
			FileMode.Open,
			FileAccess.ReadWrite,
			FileShare.None);

		var removed = PreviewDocumentBuilder.PreviewTextStorageScavenger.Scavenge(
			storage.Path,
			now,
			PreviewDocumentBuilder.PreviewTextStorageScavenger.MinimumAge);

		Assert.Equal(1, removed);
		Assert.False(File.Exists(stalePath));
		Assert.True(File.Exists(freshPath));
		Assert.True(File.Exists(activePath));
		Assert.True(File.Exists(unrelatedPath));
	}

	[Fact]
	public async Task BuildContentDocumentAsync_RejectsAPathOutsideTheProjectRoot()
	{
		using var workspace = new TemporaryDirectory();
		var projectRoot = workspace.CreateFolder("project");
		var externalPath = workspace.CreateFile("outside.txt", "external private content");
		var builder = new PreviewDocumentBuilder(new FileContentAnalyzer());

		using var document = await builder.BuildContentDocumentAsync(
			[externalPath],
			TestContext.Current.CancellationToken,
			Path.GetFileName,
			includeOmissionMarkers: true,
			projectRoot: projectRoot);

		Assert.NotNull(document);
		Assert.DoesNotContain(
			"external private content",
			document.GetFullText(),
			StringComparison.Ordinal);
		Assert.Contains("[File could not be read]", document.GetFullText(), StringComparison.Ordinal);
	}

	[Fact]
	public async Task BuildContentDocumentAsync_RejectsAStaleSymbolicLinkFromTheSelection()
	{
		using var workspace = new TemporaryDirectory();
		var projectRoot = workspace.CreateFolder("project");
		var externalPath = workspace.CreateFile("outside.txt", "external private content");
		var selectedPath = Path.Combine(projectRoot, "selected.txt");
		try
		{
			File.CreateSymbolicLink(selectedPath, externalPath);
			if (!File.GetAttributes(selectedPath).HasFlag(FileAttributes.ReparsePoint))
				Assert.Skip("File symbolic links are not exposed as reparse points on this host.");
		}
		catch (Exception exception) when (
			exception is IOException or UnauthorizedAccessException or NotSupportedException)
		{
			Assert.Skip($"File symbolic links are unavailable: {exception.GetType().Name}.");
		}

		var builder = new PreviewDocumentBuilder(new FileContentAnalyzer());
		using var document = await builder.BuildContentDocumentAsync(
			[selectedPath],
			TestContext.Current.CancellationToken,
			Path.GetFileName,
			includeOmissionMarkers: true,
			projectRoot: projectRoot);

		Assert.NotNull(document);
		Assert.DoesNotContain(
			"external private content",
			document.GetFullText(),
			StringComparison.Ordinal);
		Assert.Contains("[File could not be read]", document.GetFullText(), StringComparison.Ordinal);
	}

	[Fact]
	public void PreviewStorage_RejectsLinkedProductDirectory()
	{
		using var storage = new TemporaryDirectory();
		var target = Path.Combine(storage.Path, "target");
		Directory.CreateDirectory(target);
		var productDirectory = Path.Combine(storage.Path, "DevProjex");
		try
		{
			Directory.CreateSymbolicLink(productDirectory, target);
		}
		catch (Exception exception) when (
			exception is IOException or UnauthorizedAccessException or NotSupportedException)
		{
			Assert.Skip($"Directory symbolic links are unavailable: {exception.GetType().Name}.");
		}

		var failure = Assert.Throws<IOException>(() =>
			PreviewDocumentBuilder.PrepareStorageDirectory(storage.Path));

		Assert.Contains("symbolic link or reparse point", failure.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void PreviewStorage_PreparesOwnedDirectory()
	{
		using var storage = new TemporaryDirectory();
		var productDirectory = Path.Combine(storage.Path, "DevProjex");
		Directory.CreateDirectory(productDirectory);
		if (!OperatingSystem.IsWindows())
		{
			File.SetUnixFileMode(productDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite |
			                                           UnixFileMode.UserExecute | UnixFileMode.GroupRead |
			                                           UnixFileMode.OtherRead);
		}

		var previewDirectory = PreviewDocumentBuilder.PrepareStorageDirectory(storage.Path);

		Assert.True(Directory.Exists(previewDirectory));
		if (OperatingSystem.IsWindows())
			return;

		const UnixFileMode expected =
			UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
		Assert.Equal(expected, File.GetUnixFileMode(productDirectory));
		Assert.Equal(expected, File.GetUnixFileMode(previewDirectory));
	}

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
    public async Task BuildContentDocumentAsync_NormalizesStandaloneCarriageReturnLines()
    {
        using var temp = new TemporaryDirectory();
        var path = temp.CreateFile("legacy.txt", string.Empty);
        var analyzer = new StubFileContentAnalyzer(new Dictionary<string, TextFileContent?>
        {
            [path] = CreateTextContent("alpha\rbeta\rgamma\r")
        });
        var builder = new PreviewDocumentBuilder(analyzer);

        using var document = await builder.BuildContentDocumentAsync(
            [path],
            TestContext.Current.CancellationToken,
            Path.GetFileName);

        Assert.NotNull(document);
        Assert.Equal(5, document.LineCount);
        Assert.Equal(
            string.Join('\n', "legacy.txt:", BlankLine, "alpha", "beta", "gamma"),
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
    public async Task CreateDocumentAsync_LargePayloadUsesFileBackingAndPreservesFinalLine()
    {
        var builder = new PreviewDocumentBuilder(new StubFileContentAnalyzer());
        var largeLine = new string('x', 600_000);
        const string finalLine = "final-marker-日本語";

        using var document = await builder.CreateDocumentAsync(
            async (stream, cancellationToken) =>
            {
                await using var writer = new StreamWriter(
                    stream,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    bufferSize: 8192,
                    leaveOpen: true);
                await writer.WriteLineAsync(largeLine);
                await writer.WriteAsync(finalLine.AsMemory(), cancellationToken);
                await writer.FlushAsync(cancellationToken);
            },
            TestContext.Current.CancellationToken);

        var fileBacked = Assert.IsType<FileBackedPreviewTextDocument>(document);
        Assert.Equal(2, fileBacked.LineCount);
        Assert.Equal(largeLine.Length, fileBacked.MaxLineLength);
        Assert.Equal(largeLine.Length + Environment.NewLine.Length + finalLine.Length, fileBacked.CharacterCount);
        Assert.Equal(finalLine, fileBacked.GetLineText(2));
    }

    [Fact]
    public async Task CreateDocumentAsync_FailedWriterDeletesTemporaryBackingFile()
    {
        var builder = new PreviewDocumentBuilder(new StubFileContentAnalyzer());
        string? storagePath = null;

        await Assert.ThrowsAsync<IOException>(() => builder.CreateDocumentAsync(
            (stream, _) =>
            {
                storagePath = Assert.IsType<FileStream>(stream).Name;
                throw new IOException("deterministic test failure");
            },
            TestContext.Current.CancellationToken));

        Assert.NotNull(storagePath);
        Assert.False(File.Exists(storagePath));
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
				Assert.Null(section.CoordinateMap);
            },
            section =>
            {
                Assert.Equal("beta.txt", section.DisplayPath);
                Assert.Equal(7, section.StartLine);
                Assert.Equal(9, section.EndLine);
                Assert.Equal(7, section.HeaderLine);
                Assert.Equal(9, section.ContentStartLine);
				Assert.Null(section.CoordinateMap);
            });
    }

	[Fact]
	public async Task BuildContentDocumentAsync_SourceCoordinateMapsAreOptInForInteractivePreview()
	{
		using var temp = new TemporaryDirectory();
		var path = temp.CreateFile("config.txt", "first\r\nTOKEN=secret-value-42");
		var builder = new PreviewDocumentBuilder(new FileContentAnalyzer());

		using var document = await builder.BuildContentDocumentAsync(
			[path],
			TestContext.Current.CancellationToken,
			Path.GetFileName,
			includeSourceCoordinateMaps: true);

		var section = Assert.Single(document!.Sections);
		var map = Assert.IsType<PreviewContentCoordinateMap>(section.CoordinateMap);
		Assert.True(map.TryToSourceOffset(1, "TOKEN=".Length, out var sourceOffset));
		Assert.Equal("first\r\nTOKEN=".Length, sourceOffset);
	}

	[Fact]
	public async Task BuildContentDocumentAsync_GeneratedPathMaskUsesOneOccurrenceAcrossFileHeaders()
	{
		using var temp = new TemporaryDirectory();
		var first = temp.CreateFile("first.txt", "first");
		var second = temp.CreateFile("second.txt", "second");
		var builder = new PreviewDocumentBuilder(new FileContentAnalyzer());
		var decision = new OutputPathRedactionDecision("generated-path", Keep: false);

		using var document = await builder.BuildContentDocumentAsync(
			[first, second],
			TestContext.Current.CancellationToken,
			path => $@"C:\Users\alice\repo\{Path.GetFileName(path)}",
			outputPathRedaction: decision);

		Assert.NotNull(document);
		var redactions = document!.Redactions;
		Assert.Equal(2, redactions.Count);
		Assert.All(redactions, span =>
		{
			Assert.Equal("generated-path", span.OccurrenceId);
			Assert.Equal(OutputRootPathPresentation.LocalUserRuleId, span.RuleId);
			Assert.Equal(SecretFindingSource.GeneratedPath, span.Source);
			Assert.Equal(OutputRootPathPresentation.LocalUserPlaceholder.Length, span.Length);
		});
		Assert.Equal(2, document.GetLineRangeText(1, document.LineCount)
			.Split(OutputRootPathPresentation.LocalUserPlaceholder, StringSplitOptions.None).Length - 1);
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

	[Fact]
	public async Task BuildContentDocumentAsync_WithCompression_PreparesFilesConcurrentlyAndKeepsOrder()
	{
		using var temp = new TemporaryDirectory();
		var paths = Enumerable.Range(0, 16)
			.Select(index => temp.CreateFile($"file-{index:D2}.cs", $"content-{index:D2}"))
			.Reverse()
			.ToArray();
		using var compressor = new DelayedCodeCompressor(
			TimeSpan.Zero,
			coordinateFirstPair: Environment.ProcessorCount > 1);
		using var session = new CodeCompressionSession(compressor);
		var context = ContentTransformationContext.For(
			new CodeCompressionContext(temp.Path, session),
			redaction: null);

		using var document = await new PreviewDocumentBuilder(new FileContentAnalyzer())
			.BuildContentDocumentAsync(
				paths,
				TestContext.Current.CancellationToken,
				Path.GetFileName,
				transformationContext: context);

		Assert.NotNull(document);
		Assert.True(
			Environment.ProcessorCount == 1 || compressor.MaximumConcurrency > 1,
			$"Expected concurrent preparation; maximum concurrency was {compressor.MaximumConcurrency}.");
		Assert.Equal(
			paths.OrderBy(static path => path, PathComparer.Default).Select(Path.GetFileName),
			document.Sections.Select(static section => section.DisplayPath));
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task EmptySelection_PublishesAnEmptyCompressionSnapshotAndAllowsReuse(bool includeTree)
	{
		using var temp = new TemporaryDirectory();
		var path = temp.CreateFile("sample.cs", "abcdefghij");
		using var compressor = new SnapshotProducingCompressor();
		using var session = new CodeCompressionSession(compressor);
		var transformation = ContentTransformationContext.For(
			new CodeCompressionContext(temp.Path, session),
			redaction: null);
		var builder = new PreviewDocumentBuilder(new FileContentAnalyzer());

		using (var first = await BuildPreviewAsync(builder, [path], transformation, includeTree))
		{
			Assert.NotNull(first);
		}
		Assert.Equal(1, session.Snapshot.TotalFiles);
		Assert.Equal(1, session.Snapshot.CompressedFiles);

		using (var empty = await BuildPreviewAsync(builder, [], transformation, includeTree))
		{
			Assert.Equal(includeTree, empty is not null);
		}
		Assert.Equal(0, session.Snapshot.TotalFiles);
		Assert.Equal(0, session.Snapshot.CompressedFiles);
		Assert.Equal(0, session.Snapshot.UnchangedFiles);
		Assert.Equal(0, session.Snapshot.SourceCharacters);
		Assert.Equal(0, session.Snapshot.TransformedCharacters);
		Assert.Equal(
			CodeCompressionSession.BuildSelectionKey(temp.Path, []),
			session.Snapshot.SelectionKey);

		using (var repeated = await BuildPreviewAsync(builder, [path], transformation, includeTree))
		{
			Assert.NotNull(repeated);
		}
		Assert.Equal(1, session.Snapshot.TotalFiles);
		Assert.Equal(1, session.Snapshot.CompressedFiles);
	}

	private static async Task<IPreviewTextDocument?> BuildPreviewAsync(
		PreviewDocumentBuilder builder,
		IReadOnlyList<string> paths,
		ContentTransformationContext? transformation,
		bool includeTree)
	{
		if (includeTree)
		{
			return await builder.BuildTreeAndContentDocumentAsync(
				"root",
				paths,
				TestContext.Current.CancellationToken,
				Path.GetFileName,
				transformationContext: transformation);
		}

		return await builder.BuildContentDocumentAsync(
				paths,
				TestContext.Current.CancellationToken,
				Path.GetFileName,
				transformationContext: transformation);
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

	private static string CreatePreviewStorageFile(string directory, DateTime lastWriteTimeUtc)
	{
		var path = Path.Combine(directory, $"{Guid.NewGuid():N}.preview.txt");
		File.WriteAllText(path, "preview");
		if (!OperatingSystem.IsWindows())
			File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
		File.SetLastWriteTimeUtc(path, lastWriteTimeUtc);
		return path;
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

	private sealed class DelayedCodeCompressor(
		TimeSpan delay,
		bool coordinateFirstPair = false) : ICodeCompressor, IDisposable
	{
		private readonly ManualResetEventSlim _firstPairReady = new(false);
		private int _active;
		private int _firstPairArrivals;
		private int _maximumConcurrency;

		public string TransformIdentity => "preview-concurrency:v1";
		public int MaximumConcurrency => Volatile.Read(ref _maximumConcurrency);
		public bool IsSupported(string relativePath) => true;
		public ICodeCompressionScope CreateScope(string projectRoot) => new Scope(this, delay);
		public void Dispose() => _firstPairReady.Dispose();

		private void CoordinateFirstPair(CancellationToken cancellationToken)
		{
			if (!coordinateFirstPair || Volatile.Read(ref _firstPairArrivals) >= 2)
				return;

			if (Interlocked.Increment(ref _firstPairArrivals) >= 2)
				_firstPairReady.Set();

			if (!_firstPairReady.Wait(TimeSpan.FromSeconds(5), cancellationToken))
				throw new TimeoutException("Parallel Preview preparation did not start a second worker.");
		}

		private sealed class Scope(DelayedCodeCompressor owner, TimeSpan delay) : ICodeCompressionScope
		{
			public CodeCompressionAnalysis Analyze(
				string fullPath,
				string relativePath,
				string content,
				CancellationToken cancellationToken)
			{
				var active = Interlocked.Increment(ref owner._active);
				UpdateMaximum(ref owner._maximumConcurrency, active);
				try
				{
					owner.CoordinateFirstPair(cancellationToken);
					cancellationToken.WaitHandle.WaitOne(delay);
					cancellationToken.ThrowIfCancellationRequested();
					return new CodeCompressionAnalysis(
						CodeCompressionPlan.Unchanged(
							relativePath,
							"test",
							CodeCompressionOutcome.UnchangedNoBenefit,
							content.Length,
							owner.TransformIdentity),
						null);
				}
				finally
				{
					Interlocked.Decrement(ref owner._active);
				}
			}

			public void Dispose()
			{
			}
		}

		private static void UpdateMaximum(ref int target, int candidate)
		{
			var current = Volatile.Read(ref target);
			while (candidate > current)
			{
				var observed = Interlocked.CompareExchange(ref target, candidate, current);
				if (observed == current)
					return;
				current = observed;
			}
		}
	}

	private sealed class SnapshotProducingCompressor : ICodeCompressor, IDisposable
	{
		public string TransformIdentity => "preview-empty-selection:v1";
		public bool IsSupported(string relativePath) => true;
		public ICodeCompressionScope CreateScope(string projectRoot) => new Scope(TransformIdentity);
		public void Dispose()
		{
		}

		private sealed class Scope(string transformIdentity) : ICodeCompressionScope
		{
			public CodeCompressionAnalysis Analyze(
				string fullPath,
				string relativePath,
				string content,
				CancellationToken cancellationToken)
			{
				cancellationToken.ThrowIfCancellationRequested();
				var editLength = Math.Max(1, content.Length / 2);
				var plan = CodeCompressionPlan.Create(
					relativePath,
					"test",
					[new CodeCompressionEdit(content.Length - editLength, editLength, string.Empty)],
					content.Length,
					transformIdentity);
				return new CodeCompressionAnalysis(plan, plan.Apply(content));
			}

			public void Dispose()
			{
			}
		}
	}

	private sealed class CancelThenRejectFurtherEnumeration(
		string item,
		CancellationTokenSource cancellation) : IEnumerable<string>
	{
		public IEnumerator<string> GetEnumerator() => new Enumerator(item, cancellation);

		System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

		private sealed class Enumerator(
			string item,
			CancellationTokenSource cancellation) : IEnumerator<string>
		{
			private int _state;

			public string Current { get; private set; } = string.Empty;

			object System.Collections.IEnumerator.Current => Current;

			public bool MoveNext()
			{
				if (_state++ != 0)
					throw new InvalidOperationException("Enumeration continued after cancellation.");

				Current = item;
				cancellation.Cancel();
				return true;
			}

			public void Reset() => throw new NotSupportedException();

			public void Dispose()
			{
			}
		}
	}
}
