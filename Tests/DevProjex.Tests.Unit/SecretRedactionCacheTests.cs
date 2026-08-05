using DevProjex.Application.Secrets;

namespace DevProjex.Tests.Unit;

public sealed class SecretRedactionCacheTests
{
	private const string Secret = "cache-secret-value-0123456789";

	[Fact]
	public void CompactCache_ReusesUnchangedFilesAndInvalidatesOnlyChangedSelectionEntries()
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
		Assert.Equal(1, selectedDiagnostics.EntryCount);
		Assert.InRange(selectedDiagnostics.RetainedBytes, 1, selectedDiagnostics.MaximumRetainedBytes);

		session.Disable();
		var disabledDiagnostics = session.GetCacheDiagnostics();
		Assert.Equal(0, disabledDiagnostics.EntryCount);
		Assert.Equal(0, disabledDiagnostics.RetainedBytes);
		Assert.Null(session.GetRedactionCount(workspace.Path, [firstPath]));
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
			() => SecretRedactionLegendText.English,
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
		var firstScope = session.BeginOutput(workspace.Path, [path]);

		firstScope.AnalyzeBinary(path, SecretFileMetadata.Capture(path));
		_ = firstScope.Complete();

		var secondScope = session.BeginOutput(workspace.Path, [path]);
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
