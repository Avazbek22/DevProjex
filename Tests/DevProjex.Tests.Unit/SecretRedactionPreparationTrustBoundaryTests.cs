using DevProjex.Application.Compression;
using DevProjex.Application.Secrets;

namespace DevProjex.Tests.Unit;

public sealed class SecretRedactionPreparationTrustBoundaryTests
{
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task PreparationRejectsSelectedSymlinkBeforeReadingItsTarget(bool compressionOnly)
	{
		using var workspace = new TemporaryDirectory();
		var projectRoot = workspace.CreateFolder("project");
		var outside = workspace.CreateFile("outside.txt", "outside project content");
		var selectedPath = Path.Combine(projectRoot, "selected.cs");
		try
		{
			File.CreateSymbolicLink(selectedPath, outside);
			if (!File.GetAttributes(selectedPath).HasFlag(FileAttributes.ReparsePoint))
				Assert.Skip("File symbolic links are not exposed as reparse points on this host.");
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
		{
			Assert.Skip(OperatingSystem.IsWindows()
				? "Windows file symbolic-link creation requires privileges unavailable on this host."
				: "File symbolic links are unavailable on this host.");
		}

		var analyzer = new CountingSourceIoAnalyzer(new FileContentAnalyzer());
		using var redactionSession = new SecretRedactionSession(new NoFindingsDetector());
		using var compressor = CodeCompressionTestHarness.CreateCompressor();
		using var compressionSession = new CodeCompressionSession(compressor);
		var context = compressionOnly
			? new ContentTransformationContext(new CodeCompressionContext(projectRoot, compressionSession), null)
			: new ContentTransformationContext(null, new SecretRedactionContext(projectRoot, redactionSession));
		var preparer = new SecretRedactionOutputPreparer(analyzer);

		await Assert.ThrowsAsync<SecretDetectionException>(() => preparer.PrepareAsync(
			context,
			[selectedPath],
			TestContext.Current.CancellationToken));
		Assert.Equal(0, analyzer.SourceIoCalls);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task PreparationRejectsOutsideRootFileBeforeReadingOrPassingItThrough(bool compressionOnly)
	{
		using var workspace = new TemporaryDirectory();
		var projectRoot = workspace.CreateFolder("project");
		var outside = workspace.CreateFile("outside.txt", "outside project content");
		var analyzer = new CountingSourceIoAnalyzer(new FileContentAnalyzer());
		using var redactionSession = new SecretRedactionSession(new NoFindingsDetector());
		using var compressor = CodeCompressionTestHarness.CreateCompressor();
		using var compressionSession = new CodeCompressionSession(compressor);
		var preparer = new SecretRedactionOutputPreparer(analyzer);
		var context = compressionOnly
			? new ContentTransformationContext(new CodeCompressionContext(projectRoot, compressionSession), null)
			: new ContentTransformationContext(null, new SecretRedactionContext(projectRoot, redactionSession));

		await Assert.ThrowsAsync<SecretDetectionException>(() => preparer.PrepareAsync(
			context,
			[outside],
			TestContext.Current.CancellationToken));
		Assert.Equal(0, analyzer.SourceIoCalls);
	}

	private sealed class CountingSourceIoAnalyzer(IFileContentAnalyzer inner) : IFileContentAnalyzer
	{
		private int sourceIoCalls;

		public int SourceIoCalls => Volatile.Read(ref sourceIoCalls);

		public ValueTask<bool> IsTextFileAsync(string path, CancellationToken cancellationToken = default)
		{
			Interlocked.Increment(ref sourceIoCalls);
			return inner.IsTextFileAsync(path, cancellationToken);
		}

		public ValueTask<TextFileMetrics?> GetTextFileMetricsAsync(
			string path,
			CancellationToken cancellationToken = default)
		{
			Interlocked.Increment(ref sourceIoCalls);
			return inner.GetTextFileMetricsAsync(path, cancellationToken);
		}

		public ValueTask<TextFileContent?> TryReadAsTextAsync(
			string path,
			CancellationToken cancellationToken = default)
		{
			Interlocked.Increment(ref sourceIoCalls);
			return inner.TryReadAsTextAsync(path, cancellationToken);
		}

		public ValueTask<TextFileContent?> TryReadAsTextAsync(
			string path,
			long maxSizeForFullRead,
			CancellationToken cancellationToken = default)
		{
			Interlocked.Increment(ref sourceIoCalls);
			return inner.TryReadAsTextAsync(path, maxSizeForFullRead, cancellationToken);
		}

		public ValueTask<ContentReadFact> ReadFactAsync(
			string path,
			long maxSizeForFullRead,
			CancellationToken cancellationToken = default)
		{
			Interlocked.Increment(ref sourceIoCalls);
			return inner.ReadFactAsync(path, maxSizeForFullRead, cancellationToken);
		}
	}

	private sealed class NoFindingsDetector : ISecretDetector
	{
		public IReadOnlyList<DetectedSecret> Detect(
			string repositoryRelativePath,
			string content,
			CancellationToken cancellationToken = default) => [];
	}
}
