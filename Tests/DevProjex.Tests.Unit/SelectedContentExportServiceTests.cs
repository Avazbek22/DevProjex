namespace DevProjex.Tests.Unit;

using DevProjex.Application.Compression;
using DevProjex.Application.Secrets;
using DevProjex.Infrastructure.ProjectProfiles;
using DevProjex.Infrastructure.Secrets;

public sealed class SelectedContentExportServiceTests
{
	[Fact]
	public async Task BuildAsync_WithRootHeader_WritesRootOnceAndUsesRelativeFileHeaders()
	{
		using var project = new TemporaryDirectory();
		var file = project.CreateFile(Path.Combine("src", "Program.cs"), "class Program {}");
		var service = new SelectedContentExportService(new FileContentAnalyzer());

		var result = await service.BuildAsync(
			[file],
			TestContext.Current.CancellationToken,
			TreeAndContentExportService.CreateRelativeContentHeaderPathMapper(project.Path),
			transformationContext: null,
			displayRootPath: project.Path);

		var rootLine = ContextRootPresentation.FormatLine(project.Path);
		Assert.StartsWith($"{rootLine}{Environment.NewLine}", result, StringComparison.Ordinal);
		Assert.Equal(1, result.Split(rootLine, StringSplitOptions.None).Length - 1);
		Assert.Contains("src/Program.cs:", result, StringComparison.Ordinal);
		Assert.DoesNotContain($"{file}:", result, StringComparison.Ordinal);
	}

	// Verifies missing or empty files are ignored when exporting content.
	[Fact]
	public void Build_SkipsMissingAndEmptyFiles()
	{
		using var temp = new TemporaryDirectory();
		var empty = temp.CreateFile("empty.txt", string.Empty);
		var valid = temp.CreateFile("note.txt", "hello");
		var missing = Path.Combine(temp.Path, "missing.txt");

		var service = new SelectedContentExportService(new FileContentAnalyzer());
		var result = service.Build([missing, empty, valid]);

		Assert.Contains("note.txt:", result);
		Assert.Contains("empty.txt:", result);
		Assert.Contains("[No Content, 0 bytes]", result);
		Assert.DoesNotContain("missing.txt", result);
	}

	// Verifies binary files are excluded from clipboard content.
	[Fact]
	public void Build_SkipsBinaryFiles()
	{
		using var temp = new TemporaryDirectory();
		var binary = temp.CreateBinaryFile("bin.dat", [0, 1, 2, 3]);

		var service = new SelectedContentExportService(new FileContentAnalyzer());
		var result = service.Build([binary]);

		Assert.Equal(string.Empty, result);
	}

	// Verifies exported content is ordered by file path.
	[Fact]
	public void Build_WritesFilesInSortedOrder()
	{
		using var temp = new TemporaryDirectory();
		var fileB = temp.CreateFile("b.txt", "b");
		var fileA = temp.CreateFile("a.txt", "a");

		var service = new SelectedContentExportService(new FileContentAnalyzer());
		var result = service.Build([fileB, fileA]);

		var firstIndex = result.IndexOf("a.txt:", StringComparison.Ordinal);
		var secondIndex = result.IndexOf("b.txt:", StringComparison.Ordinal);
		Assert.True(firstIndex < secondIndex);
	}

	// Verifies whitespace-only file content is treated as empty.
	[Fact]
	public void Build_SkipsWhitespaceOnlyFiles()
	{
		using var temp = new TemporaryDirectory();
		var whitespace = temp.CreateFile("space.txt", "   ");

		var service = new SelectedContentExportService(new FileContentAnalyzer());
		var result = service.Build([whitespace]);

		Assert.Contains("space.txt:", result);
		Assert.Contains("[Whitespace, 3 bytes]", result);
	}

	// Verifies duplicate file paths are included once.
	[Fact]
	public void Build_DeduplicatesPaths()
	{
		using var temp = new TemporaryDirectory();
		var file = temp.CreateFile("dup.txt", "content");

		var service = new SelectedContentExportService(new FileContentAnalyzer());
		var result = service.Build([file, file]);

		Assert.Equal(1, result.Split("dup.txt:").Length - 1);
	}

	// Verifies whitespace-only paths yield empty output.
	[Fact]
	public void Build_ReturnsEmptyForWhitespacePaths()
	{
		var service = new SelectedContentExportService(new FileContentAnalyzer());
		var result = service.Build([" ", "\t", string.Empty]);

		Assert.Equal(string.Empty, result);
	}

	// Verifies trailing newlines are trimmed from file content.
	[Fact]
	public void Build_TrimsTrailingNewlinesFromContent()
	{
		using var temp = new TemporaryDirectory();
		var file = temp.CreateFile("trim.txt", "line\n\n");

		var service = new SelectedContentExportService(new FileContentAnalyzer());
		var result = service.Build([file]);

		Assert.EndsWith("line", result, StringComparison.Ordinal);
	}

	// Verifies separator lines are inserted between multiple files.
	[Fact]
	public void Build_IncludesBlankLinesBetweenFiles()
	{
		using var temp = new TemporaryDirectory();
		var fileA = temp.CreateFile("a.txt", "A");
		var fileB = temp.CreateFile("b.txt", "B");


		var service = new SelectedContentExportService(new FileContentAnalyzer());
		var result = service.Build([fileA, fileB]);


		var nl = Environment.NewLine;
		Assert.Contains($"\u00A0{nl}\u00A0{nl}", result);
	}

	// Verifies files with embedded null bytes in the first bytes are skipped.
	[Fact]
	public void Build_SkipsFilesWithNullBytes()
	{
		using var temp = new TemporaryDirectory();
		var file = temp.CreateBinaryFile("mixed.txt", [1, 2, 0, 3]);

		var service = new SelectedContentExportService(new FileContentAnalyzer());
		var result = service.Build([file]);

		Assert.Equal(string.Empty, result);
	}

	// Verifies content entries include the file path heading.
	[Fact]
	public void Build_IncludesFilePathHeading()
	{
		using var temp = new TemporaryDirectory();
		var file = temp.CreateFile("header.txt", "Header");

		var service = new SelectedContentExportService(new FileContentAnalyzer());
		var result = service.Build([file]);

		Assert.Contains("header.txt:", result);
	}

	// Verifies null paths are ignored safely.
	[Fact]
	public void Build_IgnoresNullPaths()
	{
		var service = new SelectedContentExportService(new FileContentAnalyzer());
		var result = service.Build(new string?[] { null }.Where(p => p is not null)!.Cast<string>());

		Assert.Equal(string.Empty, result);
	}

	// Verifies sorting follows the current platform path semantics.
	[Fact]
	public void Build_SortsPathsCaseInsensitive()
	{
		using var temp = new TemporaryDirectory();
		var fileB = temp.CreateFile("B.txt", "B");
		var fileA = temp.CreateFile("a.txt", "A");

		var service = new SelectedContentExportService(new FileContentAnalyzer());
		var result = service.Build([fileB, fileA]);

		var orderedPaths = new[] { fileB, fileA }
			.OrderBy(path => path, PathComparer.Default)
			.Select(Path.GetFileName)
			.ToArray();
		var firstIndex = result.IndexOf($"{orderedPaths[0]}:", StringComparison.Ordinal);
		var secondIndex = result.IndexOf($"{orderedPaths[1]}:", StringComparison.Ordinal);
		Assert.True(firstIndex < secondIndex);
	}

	// Verifies blank lines are not appended when only one file is written.
	[Fact]
	public void Build_DoesNotInsertSeparatorForSingleFile()
	{
		using var temp = new TemporaryDirectory();
		var file = temp.CreateFile("single.txt", "One");

		var service = new SelectedContentExportService(new FileContentAnalyzer());
		var result = service.Build([file]);

		var nl = Environment.NewLine;
		Assert.DoesNotContain($"\u00A0{nl}\u00A0{nl}", result);
	}

	[Fact]
	public async Task BuildBoundedPreviewAsync_EnforcesFileAndCharacterBudgets()
	{
		using var temp = new TemporaryDirectory();
		var first = temp.CreateFile("a.txt", new string('a', 120));
		var second = temp.CreateFile("b.txt", new string('b', 120));
		var third = temp.CreateFile("c.txt", new string('c', 120));
		var service = new SelectedContentExportService(new FileContentAnalyzer());

		var result = await service.BuildBoundedPreviewAsync(
			[third, second, first],
			maxFileCount: 2,
			maxFileSizeForFullRead: 1024,
			maxOutputCharacters: 180,
			CancellationToken.None,
			displayPathMapper: Path.GetFileName);

		Assert.Equal(180, result.Length);
		Assert.Contains("a.txt:", result, StringComparison.Ordinal);
		Assert.Contains("b.txt:", result, StringComparison.Ordinal);
		Assert.DoesNotContain("c.txt:", result, StringComparison.Ordinal);
	}

	[Fact]
	public async Task BuildBoundedPreviewAsync_ExhaustedRootBudgetSkipsFileIo()
	{
		using var temp = new TemporaryDirectory();
		var binary = Path.Combine(temp.Path, "binary.dat");
		await File.WriteAllBytesAsync(binary, [0, 1, 2, 0, 3], TestContext.Current.CancellationToken);
		var analyzer = new RecordingContentAnalyzer();
		var service = new SelectedContentExportService(analyzer);

		var result = await service.BuildBoundedPreviewAsync(
			[binary],
			maxFileCount: 1,
			maxFileSizeForFullRead: 1024,
			maxOutputCharacters: 32,
			TestContext.Current.CancellationToken,
			displayPathMapper: Path.GetFileName,
			displayRootPath: new string('r', 128));

		Assert.Equal(32, result.Length);
		Assert.StartsWith(ContextRootPresentation.Prefix, result, StringComparison.Ordinal);
		Assert.Empty(analyzer.ReadPaths);
	}

	[Fact]
	public async Task BuildBoundedPreviewAsync_DoesNotSplitUnicodeScalarAtRootBudgetBoundary()
	{
		using var temp = new TemporaryDirectory();
		var binary = Path.Combine(temp.Path, "binary.dat");
		await File.WriteAllBytesAsync(binary, [0, 1, 2, 0, 3], TestContext.Current.CancellationToken);
		var service = new SelectedContentExportService(new FileContentAnalyzer());

		var result = await service.BuildBoundedPreviewAsync(
			[binary],
			maxFileCount: 1,
			maxFileSizeForFullRead: 1024,
			maxOutputCharacters: 8,
			TestContext.Current.CancellationToken,
			displayPathMapper: Path.GetFileName,
			displayRootPath: "a🙂");

		Assert.Equal("Root: a", result);
	}

	[Fact]
	public async Task BuildBoundedPreviewAsync_DoesNotLoadFileBeyondReadBudget()
	{
		using var temp = new TemporaryDirectory();
		var largePayload = new string('x', 256 * 1024);
		var largeFile = temp.CreateFile("large.txt", largePayload);
		var service = new SelectedContentExportService(new FileContentAnalyzer());

		var result = await service.BuildBoundedPreviewAsync(
			[largeFile],
			maxFileCount: 1,
			maxFileSizeForFullRead: 4096,
			maxOutputCharacters: 8192,
			CancellationToken.None,
			displayPathMapper: Path.GetFileName);

		Assert.Contains("large.txt:", result, StringComparison.Ordinal);
		Assert.DoesNotContain(largePayload[..4096], result, StringComparison.Ordinal);
		Assert.True(result.Length < 256);
	}

	[Fact]
	public async Task BuildAsync_PreparesSmallFilesConcurrentlyWithinBoundAndPreservesExactOutput()
	{
		using var temp = new TemporaryDirectory();
		var paths = Enumerable.Range(0, 16)
			.Select(index => temp.CreateFile($"{index:D2}.txt", $"value-{index:D2}"))
			.Reverse()
			.ToArray();
		var expected = await new SelectedContentExportService(new FileContentAnalyzer()).BuildAsync(
			paths,
			TestContext.Current.CancellationToken,
			Path.GetFileName);
		var analyzer = new ConcurrentProbeContentAnalyzer(minimumConcurrentReads: 2);
		var service = new SelectedContentExportService(analyzer);

		var actual = await service.BuildAsync(
			paths,
			TestContext.Current.CancellationToken,
			Path.GetFileName);

		Assert.Equal(expected, actual);
		Assert.InRange(
			analyzer.MaximumConcurrentReads,
			2,
			SelectedContentExportService.MaximumParallelPreparations);
	}

	[Fact]
	public async Task BuildAsync_CancellationDrainsEveryScheduledPreparation()
	{
		using var temp = new TemporaryDirectory();
		var paths = Enumerable.Range(0, 16)
			.Select(index => temp.CreateFile($"{index:D2}.txt", $"value-{index:D2}"))
			.ToArray();
		var analyzer = new BlockingContentAnalyzer(returnFirstResult: false);
		var service = new SelectedContentExportService(analyzer);
		using var cancellation = new CancellationTokenSource();

		var operation = service.BuildAsync(paths, cancellation.Token, Path.GetFileName);
		await analyzer.MinimumConcurrencyReached.WaitAsync(
			TimeSpan.FromSeconds(5),
			TestContext.Current.CancellationToken);
		await cancellation.CancelAsync();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
		await analyzer.AllReadsDrained.WaitAsync(
			TimeSpan.FromSeconds(5),
			TestContext.Current.CancellationToken);
		Assert.Equal(0, analyzer.ActiveReads);
	}

	[Fact]
	public async Task WriteAsync_OutputFailureCancelsAndDrainsEveryScheduledPreparation()
	{
		using var temp = new TemporaryDirectory();
		var paths = Enumerable.Range(0, 16)
			.Select(index => temp.CreateFile($"{index:D2}.txt", $"value-{index:D2}"))
			.ToArray();
		var analyzer = new BlockingContentAnalyzer(returnFirstResult: true);
		var service = new SelectedContentExportService(analyzer);
		await using var output = new FailingWriteStream();

		await Assert.ThrowsAsync<IOException>(() => service.WriteAsync(
			output,
			paths,
			TestContext.Current.CancellationToken,
			Path.GetFileName));

		await analyzer.AllReadsDrained.WaitAsync(
			TimeSpan.FromSeconds(5),
			TestContext.Current.CancellationToken);
		Assert.Equal(0, analyzer.ActiveReads);
	}

	[Fact]
	public async Task BuildBoundedPreviewAsync_DoesNotReadFilesAfterFileCutoff()
	{
		using var temp = new TemporaryDirectory();
		var first = temp.CreateFile("a.txt", "first");
		var second = temp.CreateFile("b.txt", "second");
		var third = temp.CreateFile("c.txt", "third");
		var analyzer = new RecordingContentAnalyzer();
		var service = new SelectedContentExportService(analyzer);

		var result = await service.BuildBoundedPreviewAsync(
			[third, second, first],
			maxFileCount: 1,
			maxFileSizeForFullRead: 4096,
			maxOutputCharacters: 8192,
			TestContext.Current.CancellationToken,
			Path.GetFileName);

		Assert.Contains("a.txt:", result, StringComparison.Ordinal);
		Assert.Equal([first], analyzer.ReadPaths);
	}

	[Fact]
	public async Task BuildBoundedPreviewAsync_CompressesWarmupWithoutPublishingPartialSnapshot()
	{
		using var temp = new TemporaryDirectory();
		const string source = "prefix { verbose body } suffix";
		var file = temp.CreateFile("sample.cs", source);
		var compressor = new RecordingCodeCompressor();
		using var session = new CodeCompressionSession(compressor);
		var context = new CodeCompressionContext(temp.Path, session);
		var publishedSnapshots = 0;
		session.SnapshotPublished += (_, _) => publishedSnapshots++;
		var service = new SelectedContentExportService(new FileContentAnalyzer());

		var warmup = await service.BuildBoundedPreviewAsync(
			[file],
			maxFileCount: 1,
			maxFileSizeForFullRead: 4096,
			maxOutputCharacters: 8192,
			CancellationToken.None,
			displayPathMapper: Path.GetFileName,
			compressionContext: context);

		Assert.Contains("prefix {...} suffix", warmup, StringComparison.Ordinal);
		Assert.Equal(1, compressor.AnalysisCount);
		Assert.Equal(CodeCompressionSnapshot.Empty, session.Snapshot);
		Assert.Equal(0, publishedSnapshots);

		var complete = await service.BuildAsync(
			[file],
			CancellationToken.None,
			Path.GetFileName,
			ContentTransformationContext.For(context, redaction: null));

		Assert.Contains("prefix {...} suffix", complete, StringComparison.Ordinal);
		Assert.Equal(1, compressor.AnalysisCount);
		Assert.Equal(1, session.Snapshot.CompressedFiles);
		Assert.Equal(1, publishedSnapshots);
	}

	[Fact]
	public async Task BuildAsync_RefreshesPersistentMarksAddedByAnotherStoreBeforeClipboardOutput()
	{
		const string secret = "clipboard-persistent-secret-012345";
		using var workspace = new TemporaryDirectory();
		var projectRoot = workspace.CreateFolder("project");
		var appData = workspace.CreateFolder("app-data");
		var file = workspace.CreateFile("project/config.env", $"TOKEN={secret}\n");
		var firstStore = new ProjectProfileStore(() => appData);
		var secondStore = new ProjectProfileStore(() => appData);
		var identityProvider = new PersistentSecretIdentityProvider(() => appData);
		using var session = new SecretRedactionSession(
			new EmptySecretDetector(),
			firstStore,
			identityProvider);
		var initiallyLoaded = await firstStore.LoadMarksAsync(
			projectRoot,
			TestContext.Current.CancellationToken);
		session.ReplacePersistentMarks(projectRoot, initiallyLoaded.Snapshot!);
		Assert.True(MarkedSecretValueNormalizer.TryCreate(secret, out var value, out _));
		var mark = await session.CreatePersistentMarkedSecretAsync(
			value,
			"TOKEN",
			TestContext.Current.CancellationToken);
		Assert.NotNull(mark);
		Assert.True((await secondStore.AddMarkAsync(
			projectRoot,
			mark!,
			TestContext.Current.CancellationToken)).Succeeded);

		var output = await new SelectedContentExportService(new FileContentAnalyzer()).BuildAsync(
			[file],
			TestContext.Current.CancellationToken,
			Path.GetFileName,
			ContentTransformationContext.For(
				compression: null,
				new SecretRedactionContext(projectRoot, session)));

		Assert.DoesNotContain(secret, output, StringComparison.Ordinal);
		Assert.Contains("DEVPROJEX_REDACTED[manual-secret#1]", output, StringComparison.Ordinal);
	}

	private sealed class EmptySecretDetector : ISecretDetector
	{
		public IReadOnlyList<DetectedSecret> Detect(
			string repositoryRelativePath,
			string content,
			CancellationToken cancellationToken = default) => [];
	}

	private sealed class RecordingCodeCompressor : ICodeCompressor
	{
		private int _analysisCount;

		public string TransformIdentity => "preview-warmup:v1";

		public int AnalysisCount => Volatile.Read(ref _analysisCount);

		public bool IsSupported(string relativePath) => true;

		public ICodeCompressionScope CreateScope(string projectRoot) => new Scope(this);

		private sealed class Scope(RecordingCodeCompressor owner) : ICodeCompressionScope
		{
			public CodeCompressionAnalysis Analyze(
				string fullPath,
				string relativePath,
				string content,
				CancellationToken cancellationToken)
			{
				cancellationToken.ThrowIfCancellationRequested();
				Interlocked.Increment(ref owner._analysisCount);
				var bodyStart = content.IndexOf("{ verbose body }", StringComparison.Ordinal);
				var plan = CodeCompressionPlan.Create(
					relativePath,
					"csharp",
					[new CodeCompressionEdit(bodyStart, "{ verbose body }".Length, "{...}")],
					content.Length,
					owner.TransformIdentity);
				return new CodeCompressionAnalysis(plan, plan.Apply(content));
			}

			public void Dispose()
			{
			}
		}
	}

	private sealed class ConcurrentProbeContentAnalyzer(int minimumConcurrentReads) : IFileContentAnalyzer
	{
		private readonly TaskCompletionSource _releaseReads = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		private int _activeReads;
		private int _maximumConcurrentReads;

		public int MaximumConcurrentReads => Volatile.Read(ref _maximumConcurrentReads);

		public ValueTask<bool> IsTextFileAsync(string path, CancellationToken cancellationToken = default) =>
			ValueTask.FromResult(true);

		public ValueTask<TextFileMetrics?> GetTextFileMetricsAsync(
			string path,
			CancellationToken cancellationToken = default) =>
			ValueTask.FromResult<TextFileMetrics?>(null);

		public ValueTask<TextFileContent?> TryReadAsTextAsync(
			string path,
			CancellationToken cancellationToken = default) =>
			new(ReadAsync(path, cancellationToken));

		public ValueTask<TextFileContent?> TryReadAsTextAsync(
			string path,
			long maxSizeForFullRead,
			CancellationToken cancellationToken = default) =>
			new(ReadAsync(path, cancellationToken));

		private async Task<TextFileContent?> ReadAsync(string path, CancellationToken cancellationToken)
		{
			var active = Interlocked.Increment(ref _activeReads);
			UpdateMaximum(active);
			if (active >= minimumConcurrentReads)
				_releaseReads.TrySetResult();
			try
			{
				await _releaseReads.Task.WaitAsync(cancellationToken);
				await Task.Delay(20, cancellationToken);
				return CreateContent(await File.ReadAllTextAsync(path, cancellationToken));
			}
			finally
			{
				Interlocked.Decrement(ref _activeReads);
			}
		}

		private void UpdateMaximum(int active)
		{
			while (true)
			{
				var current = Volatile.Read(ref _maximumConcurrentReads);
				if (active <= current ||
				    Interlocked.CompareExchange(ref _maximumConcurrentReads, active, current) == current)
				{
					return;
				}
			}
		}
	}

	private sealed class BlockingContentAnalyzer(bool returnFirstResult) : IFileContentAnalyzer
	{
		private readonly TaskCompletionSource _minimumConcurrencyReached = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		private readonly TaskCompletionSource _allReadsDrained = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		private int _activeReads;

		public Task MinimumConcurrencyReached => _minimumConcurrencyReached.Task;
		public Task AllReadsDrained => _allReadsDrained.Task;
		public int ActiveReads => Volatile.Read(ref _activeReads);

		public ValueTask<bool> IsTextFileAsync(string path, CancellationToken cancellationToken = default) =>
			ValueTask.FromResult(true);

		public ValueTask<TextFileMetrics?> GetTextFileMetricsAsync(
			string path,
			CancellationToken cancellationToken = default) =>
			ValueTask.FromResult<TextFileMetrics?>(null);

		public ValueTask<TextFileContent?> TryReadAsTextAsync(
			string path,
			CancellationToken cancellationToken = default) =>
			new(ReadAsync(path, cancellationToken));

		public ValueTask<TextFileContent?> TryReadAsTextAsync(
			string path,
			long maxSizeForFullRead,
			CancellationToken cancellationToken = default) =>
			new(ReadAsync(path, cancellationToken));

		private async Task<TextFileContent?> ReadAsync(string path, CancellationToken cancellationToken)
		{
			var active = Interlocked.Increment(ref _activeReads);
			if (active >= 2)
				_minimumConcurrencyReached.TrySetResult();
			try
			{
				if (returnFirstResult && string.Equals(Path.GetFileName(path), "00.txt", StringComparison.Ordinal))
				{
					await _minimumConcurrencyReached.Task.WaitAsync(cancellationToken);
					return CreateContent(new string('x', 64 * 1024));
				}

				await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
				return null;
			}
			finally
			{
				if (Interlocked.Decrement(ref _activeReads) == 0)
					_allReadsDrained.TrySetResult();
			}
		}
	}

	private sealed class RecordingContentAnalyzer : IFileContentAnalyzer
	{
		private readonly List<string> _readPaths = [];

		public IReadOnlyList<string> ReadPaths => _readPaths;

		public ValueTask<bool> IsTextFileAsync(string path, CancellationToken cancellationToken = default) =>
			ValueTask.FromResult(true);

		public ValueTask<TextFileMetrics?> GetTextFileMetricsAsync(
			string path,
			CancellationToken cancellationToken = default) =>
			ValueTask.FromResult<TextFileMetrics?>(null);

		public ValueTask<TextFileContent?> TryReadAsTextAsync(
			string path,
			CancellationToken cancellationToken = default) =>
			ReadAsync(path, cancellationToken);

		public ValueTask<TextFileContent?> TryReadAsTextAsync(
			string path,
			long maxSizeForFullRead,
			CancellationToken cancellationToken = default) =>
			ReadAsync(path, cancellationToken);

		private ValueTask<TextFileContent?> ReadAsync(string path, CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			_readPaths.Add(path);
			return ValueTask.FromResult<TextFileContent?>(
				CreateContent(File.ReadAllText(path)));
		}
	}

	private sealed class FailingWriteStream : Stream
	{
		public override bool CanRead => false;
		public override bool CanSeek => false;
		public override bool CanWrite => true;
		public override long Length => throw new NotSupportedException();
		public override long Position
		{
			get => throw new NotSupportedException();
			set => throw new NotSupportedException();
		}

		public override void Flush()
		{
		}

		public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
		public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
		public override void SetLength(long value) => throw new NotSupportedException();

		public override void Write(byte[] buffer, int offset, int count) =>
			throw new IOException("Synthetic output failure.");

		public override Task WriteAsync(
			byte[] buffer,
			int offset,
			int count,
			CancellationToken cancellationToken) =>
			Task.FromException(new IOException("Synthetic output failure."));

		public override ValueTask WriteAsync(
			ReadOnlyMemory<byte> buffer,
			CancellationToken cancellationToken = default) =>
			ValueTask.FromException(new IOException("Synthetic output failure."));
	}

	private static TextFileContent CreateContent(string text) =>
		new(
			text,
			Encoding.UTF8.GetByteCount(text),
			LineCount: 1,
			CharCount: text.Length,
			IsEmpty: text.Length == 0,
			IsWhitespaceOnly: string.IsNullOrWhiteSpace(text));
}
