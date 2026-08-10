using DevProjex.Application.Secrets;
using DevProjex.Application.Compression;

namespace DevProjex.Tests.Unit;

public sealed class SecretRedactionCacheTests
{
	private const string Secret = "cache-secret-value-0123456789";
	private const string SameLengthPublicValue = "cache-public-value-0123456789";

	[Fact]
	public async Task OutputPreparer_RevalidatesContentWhenLengthAndTimestampAreUnchanged()
	{
		Assert.Equal(Secret.Length, SameLengthPublicValue.Length);
		using var workspace = new TemporaryDirectory();
		var path = workspace.CreateFile("src/config.env", $"token={Secret}\n");
		var timestamp = File.GetLastWriteTimeUtc(path);
		var detector = new CountingDetector();
		using var session = new SecretRedactionSession(detector);
		var preparer = new SecretRedactionOutputPreparer(new FileContentAnalyzer());
		var context = new SecretRedactionContext(workspace.Path, session);

		var first = await preparer.AnalyzeAsync(
			context,
			[path],
			TestContext.Current.CancellationToken);
		Assert.Equal(1, first.RedactedCount);
		Assert.Equal(1, detector.CallCount);

		File.WriteAllText(path, $"token={SameLengthPublicValue}\n");
		File.SetLastWriteTimeUtc(path, timestamp);
		Assert.Equal(timestamp, File.GetLastWriteTimeUtc(path));

		var second = await preparer.AnalyzeAsync(
			context,
			[path],
			TestContext.Current.CancellationToken);

		Assert.Equal(0, second.RedactedCount);
		Assert.Equal(2, detector.CallCount);
	}

	[Fact]
	public async Task OutputPreparer_ReclassifiesCachedBinaryWhenMetadataAreUnchanged()
	{
		using var workspace = new TemporaryDirectory();
		var secretText = $"token={Secret}\n";
		var binary = new byte[secretText.Length];
		binary[0] = 0;
		var path = workspace.CreateBinaryFile("src/config.txt", binary);
		var timestamp = File.GetLastWriteTimeUtc(path);
		var detector = new CountingDetector();
		using var session = new SecretRedactionSession(detector);
		var preparer = new SecretRedactionOutputPreparer(new FileContentAnalyzer());
		var context = new SecretRedactionContext(workspace.Path, session);

		var first = await preparer.AnalyzeAsync(
			context,
			[path],
			TestContext.Current.CancellationToken);
		Assert.Equal(0, first.RedactedCount);
		Assert.Equal(0, detector.CallCount);

		File.WriteAllText(path, secretText);
		File.SetLastWriteTimeUtc(path, timestamp);
		Assert.Equal(binary.Length, new FileInfo(path).Length);
		Assert.Equal(timestamp, File.GetLastWriteTimeUtc(path));

		var second = await preparer.AnalyzeAsync(
			context,
			[path],
			TestContext.Current.CancellationToken);

		Assert.Equal(1, second.RedactedCount);
		Assert.Equal(1, detector.CallCount);
	}

	[Fact]
	public void CompactCache_RetainsDeselectedFilesForBoundedReselectionReuse()
	{
		using var workspace = new TemporaryDirectory();
		var firstPath = workspace.CreateFile("src/first.env", $"token={Secret}\n");
		var secondPath = workspace.CreateFile("src/second.env", "name=devprojex\n");
		var paths = new[] { firstPath, secondPath };
		var detector = new CountingDetector();
		var session = new SecretRedactionSession(detector);

		Assert.Equal(1, Scan(session, workspace.Path, paths));
		Assert.Equal(2, detector.CallCount);
		Assert.Equal(1, Scan(session, workspace.Path, paths));
		Assert.Equal(2, detector.CallCount);

		File.WriteAllText(secondPath, $"token={Secret}\n");
		File.SetLastWriteTimeUtc(secondPath, DateTime.UtcNow.AddSeconds(2));
		Assert.Equal(2, Scan(session, workspace.Path, paths));
		Assert.Equal(3, detector.CallCount);

		Assert.Equal(1, Scan(session, workspace.Path, [firstPath]));
		var selectedDiagnostics = session.GetCacheDiagnostics();
		Assert.Equal(2, selectedDiagnostics.EntryCount);
		Assert.InRange(selectedDiagnostics.RetainedBytes, 1, selectedDiagnostics.MaximumRetainedBytes);
		Assert.Equal(2, Scan(session, workspace.Path, paths));
		Assert.Equal(3, detector.CallCount);

		session.Disable();
		var disabledDiagnostics = session.GetCacheDiagnostics();
		Assert.Equal(0, disabledDiagnostics.EntryCount);
		Assert.Equal(0, disabledDiagnostics.RetainedBytes);
		Assert.Null(session.GetRedactionCount(workspace.Path, [firstPath]));
	}

	[Fact]
	public async Task DiscoveryCacheMode_ReusesValidatedContentWithoutWeakeningStrictRevalidation()
	{
		using var workspace = new TemporaryDirectory();
		var paths = new[]
		{
			workspace.CreateFile("src/first.env", $"token={Secret}\n"),
			workspace.CreateFile("src/second.env", "name=devprojex\n")
		};
		var detector = new CountingDetector();
		var analyzer = new CountingContentAnalyzer(new FileContentAnalyzer());
		using var session = new SecretRedactionSession(detector);
		var preparer = new SecretRedactionOutputPreparer(analyzer);
		var context = new SecretRedactionContext(workspace.Path, session);

		var initial = await preparer.DiscoverAsync(
			context,
			paths,
			TestContext.Current.CancellationToken);
		Assert.Equal(1, initial.RedactedCount);
		Assert.Equal(2, analyzer.ReadCount);
		Assert.Equal(2, detector.CallCount);

		var reused = await preparer.DiscoverAsync(
			context,
			paths,
			SecretDiscoveryCacheMode.ReuseValidatedContent,
			TestContext.Current.CancellationToken);
		Assert.Equal(initial.SelectionKey, reused.SelectionKey);
		Assert.Equal(initial.DetectedCount, reused.DetectedCount);
		Assert.Equal(initial.RedactedCount, reused.RedactedCount);
		Assert.Equal(2, analyzer.ReadCount);
		Assert.Equal(2, detector.CallCount);

		var revalidated = await preparer.DiscoverAsync(
			context,
			paths,
			TestContext.Current.CancellationToken);
		Assert.Equal(initial.SelectionKey, revalidated.SelectionKey);
		Assert.Equal(initial.DetectedCount, revalidated.DetectedCount);
		Assert.Equal(initial.RedactedCount, revalidated.RedactedCount);
		Assert.Equal(4, analyzer.ReadCount);
		Assert.Equal(2, detector.CallCount);
	}

	[Fact]
	public void Snapshots_AreScopedByTransformIdentityAndObsoleteScopesCannotPublish()
	{
		using var workspace = new TemporaryDirectory();
		var path = workspace.CreateFile("src/config.env", $"token={Secret}\n");
		var detector = new CountingDetector();
		using var session = new SecretRedactionSession(detector);
		var metadata = SecretFileMetadata.Capture(path);
		var content = File.ReadAllText(path);

		var rawScope = session.BeginOutput(workspace.Path, [path]);
		rawScope.Analyze(path, content, metadata, TestContext.Current.CancellationToken);
		var rawSnapshot = rawScope.Complete();

		var compressedScope = session.BeginOutput(workspace.Path, [path], "signatures-v1");
		compressedScope.Analyze(path, content, metadata, TestContext.Current.CancellationToken);
		var compressedSnapshot = compressedScope.Complete();

		Assert.NotEqual(rawSnapshot.SelectionKey, compressedSnapshot.SelectionKey);
		Assert.Same(rawSnapshot, session.GetSnapshot(workspace.Path, [path]));
		Assert.Same(
			compressedSnapshot,
			session.GetSnapshot(workspace.Path, [path], "signatures-v1"));
		Assert.Equal(2, session.GetCacheDiagnostics().EntryCount);

		var cachedRawScope = session.BeginOutput(workspace.Path, [path]);
		Assert.True(cachedRawScope.TryAnalyzeCached(path));
		_ = cachedRawScope.Complete();
		var cachedCompressedScope = session.BeginOutput(workspace.Path, [path], "signatures-v1");
		Assert.True(cachedCompressedScope.TryAnalyzeCached(path));
		_ = cachedCompressedScope.Complete();
		Assert.Equal(2, detector.CallCount);

		var obsoleteScope = session.BeginOutput(workspace.Path, [path]);
		obsoleteScope.Analyze(path, content, metadata, TestContext.Current.CancellationToken);
		session.InvalidateSnapshots();
		_ = obsoleteScope.Complete();

		Assert.Null(session.GetSnapshot(workspace.Path, [path]));
		Assert.Null(session.GetSnapshot(workspace.Path, [path], "signatures-v1"));
	}

	[Fact]
	public void IdentityCompression_ReusesRawFindingsOnlyWithTheSameContentFingerprint()
	{
		using var workspace = new TemporaryDirectory();
		var path = workspace.CreateFile("src/config.cs", $"const string Token = \"{Secret}\";\n");
		var content = File.ReadAllText(path);
		var fingerprint = ContentFingerprint.Compute(content);
		var detector = new CountingDetector();
		using var session = new SecretRedactionSession(detector);

		var raw = session.BeginOutput(workspace.Path, [path]);
		var rawPlan = raw.CreatePlan(
			path,
			content,
			ContentTransformMap.Identity,
			fingerprint,
			TestContext.Current.CancellationToken);
		_ = raw.Complete();

		var compressed = session.BeginOutput(workspace.Path, [path], "signatures-v1");
		var compressedPlan = compressed.CreatePlan(
			path,
			content,
			ContentTransformMap.Identity,
			fingerprint,
			TestContext.Current.CancellationToken);
		_ = compressed.Complete();

		Assert.Equal(rawPlan.RedactedCount, compressedPlan.RedactedCount);
		Assert.Equal(1, detector.CallCount);
		Assert.Equal(2, session.GetCacheDiagnostics().EntryCount);

		var changed = content.Replace(Secret, SameLengthPublicValue, StringComparison.Ordinal);
		var changedScope = session.BeginOutput(workspace.Path, [path], "signatures-v1");
		var changedPlan = changedScope.CreatePlan(
			path,
			changed,
			ContentTransformMap.Identity,
			ContentFingerprint.Compute(changed),
			TestContext.Current.CancellationToken);
		_ = changedScope.Complete();

		Assert.Equal(0, changedPlan.RedactedCount);
		Assert.Equal(2, detector.CallCount);
	}

	[Fact]
	public void CompactCache_EnforcesEntryAndByteLimitsWithLruEviction()
	{
		using var workspace = new TemporaryDirectory();
		var paths = Enumerable.Range(0, 4)
			.Select(index => workspace.CreateFile($"src/file-{index}.env", $"token={Secret}-{index}\n"))
			.ToArray();
		var cache = new SecretScanCache(maximumEntries: 2, maximumRetainedBytes: 2_048);
		var session = new SecretRedactionSession(
			new CountingDetector(),
			cache);

		_ = Scan(session, workspace.Path, paths);

		var diagnostics = session.GetCacheDiagnostics();
		Assert.Equal(2, diagnostics.MaximumEntries);
		Assert.Equal(2_048, diagnostics.MaximumRetainedBytes);
		Assert.InRange(diagnostics.EntryCount, 1, 2);
		Assert.InRange(diagnostics.RetainedBytes, 1, diagnostics.MaximumRetainedBytes);
	}

	[Fact]
	public void CanceledDetection_DoesNotPublishSnapshotOrRetainIncompleteEntry()
	{
		using var workspace = new TemporaryDirectory();
		var path = workspace.CreateFile("src/config.env", $"token={Secret}\n");
		var session = new SecretRedactionSession(new CancelingDetector());
		var scope = session.BeginOutput(workspace.Path, [path]);
		var content = File.ReadAllText(path);

		Assert.Throws<OperationCanceledException>(() =>
		{
			using var contentLease = scope.TrackFullContentBuffer();
			scope.Analyze(
				path,
				content,
				SecretFileMetadata.Capture(path),
				new CancellationToken(canceled: true));
		});

		var diagnostics = session.GetCacheDiagnostics();
		Assert.Equal(0, diagnostics.EntryCount);
		Assert.Equal(0, diagnostics.RetainedBytes);
		Assert.Equal(0, diagnostics.ActiveFullContentBuffers);
		Assert.Null(session.GetRedactionCount(workspace.Path, [path]));
	}

	[Fact]
	public void BinaryCache_UsesTheScopedRulesIdentityAndReusesUnchangedMetadata()
	{
		using var workspace = new TemporaryDirectory();
		var path = workspace.CreateFile("assets/blob.bin", "\0binary");
		using var session = new SecretRedactionSession(new ScopedIdentityDetector());
		var firstScope = session.BeginOutput(workspace.Path, [path], "signatures-v1");

		firstScope.AnalyzeBinary(path, SecretFileMetadata.Capture(path));
		_ = firstScope.Complete();

		var secondScope = session.BeginOutput(workspace.Path, [path], "signatures-v1");
		Assert.True(secondScope.TryAnalyzeCached(path));
		Assert.Equal(0, secondScope.Complete().RedactedCount);
	}

	private static int Scan(
		SecretRedactionSession session,
		string projectRoot,
		IReadOnlyList<string> paths)
	{
		var scope = session.BeginOutput(projectRoot, paths);
		foreach (var path in paths)
		{
			if (scope.TryAnalyzeCached(path))
				continue;

			var content = File.ReadAllText(path);
			using var contentLease = scope.TrackFullContentBuffer();
			scope.Analyze(
				path,
				content,
				SecretFileMetadata.Capture(path),
				TestContext.Current.CancellationToken);
		}

		return scope.Complete().RedactedCount;
	}

	private sealed class CountingDetector : ISecretDetector
	{
		public int CallCount { get; private set; }

		public IReadOnlyList<DetectedSecret> Detect(
			string repositoryRelativePath,
			string content,
			CancellationToken cancellationToken = default)
		{
			CallCount++;
			cancellationToken.ThrowIfCancellationRequested();
			var start = content.IndexOf(Secret, StringComparison.Ordinal);
			return start < 0
				? []
				: [new DetectedSecret("cache-test", start, Secret.Length, Secret, 0)];
		}
	}

	private sealed class CountingContentAnalyzer(IFileContentAnalyzer inner) : IFileContentAnalyzer
	{
		private int _readCount;

		public int ReadCount => Volatile.Read(ref _readCount);

		public FileContentClassification? ClassifyWithoutReading(string path) =>
			inner.ClassifyWithoutReading(path);

		public ValueTask<bool> IsTextFileAsync(
			string path,
			CancellationToken cancellationToken = default) =>
			inner.IsTextFileAsync(path, cancellationToken);

		public ValueTask<TextFileMetrics?> GetTextFileMetricsAsync(
			string path,
			CancellationToken cancellationToken = default) =>
			inner.GetTextFileMetricsAsync(path, cancellationToken);

		public async ValueTask<ICompleteTextFileBuffer> OpenCompleteTextBufferAsync(
			string path,
			long maximumBytes,
			CancellationToken cancellationToken = default)
		{
			var buffer = await inner
				.OpenCompleteTextBufferAsync(path, maximumBytes, cancellationToken)
				.ConfigureAwait(false);
			Interlocked.Increment(ref _readCount);
			return buffer;
		}

		public ValueTask<TextFileContent?> TryReadAsTextAsync(
			string path,
			CancellationToken cancellationToken = default) =>
			inner.TryReadAsTextAsync(path, cancellationToken);

		public ValueTask<TextFileContent?> TryReadAsTextAsync(
			string path,
			long maxSizeForFullRead,
			CancellationToken cancellationToken = default) =>
			inner.TryReadAsTextAsync(path, maxSizeForFullRead, cancellationToken);
	}

	private sealed class CancelingDetector : ISecretDetector
	{
		public IReadOnlyList<DetectedSecret> Detect(
			string repositoryRelativePath,
			string content,
			CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			throw new InvalidOperationException("The cancellation contract was not honored.");
		}
	}

	private sealed class ScopedIdentityDetector : ISecretDetector
	{
		public string RulesIdentity => "base-rules";

		public ISecretDetectionScope CreateScope(string projectRoot) => new ScopedIdentityDetectionScope();

		public IReadOnlyList<DetectedSecret> Detect(
			string repositoryRelativePath,
			string content,
			CancellationToken cancellationToken = default) => [];

		private sealed class ScopedIdentityDetectionScope : ISecretDetectionScope
		{
			public string GetRulesIdentity(string fullPath, string repositoryRelativePath) => "scoped-rules";

			public IReadOnlyList<DetectedSecret> Detect(
				string fullPath,
				string repositoryRelativePath,
				ReadOnlySpan<char> content,
				CancellationToken cancellationToken = default) => [];
		}
	}
}
