using DevProjex.Application.Secrets;
using DevProjex.Application.Compression;
using DevProjex.Application.Diagnostics;

namespace DevProjex.Tests.Unit;

public sealed class SecretRedactionCacheTests
{
	private const string Secret = "cache-secret-value-0123456789";
	private const string SameLengthPublicValue = "cache-public-value-0123456789";

	[Fact]
	public async Task OutputPreparer_UnsupportedEncoding_IsWithheldAndReportedWithItsReason()
	{
		using var workspace = new TemporaryDirectory();
		var path = workspace.CreateFile("src/legacy.txt", "legacy content must not escape");
		using var session = new SecretRedactionSession(new EmptyDetector());
		var context = new SecretRedactionContext(workspace.Path, session);
		var preparer = new SecretRedactionOutputPreparer(
			new ClassifiedContentAnalyzer(FileContentClassification.UnsupportedEncoding));

		await using var prepared = await preparer.PrepareAsync(
			new ContentTransformationContext(null, context),
			[path],
			TestContext.Current.CancellationToken);

		var file = prepared.GetFile(path);
		Assert.True(file.IsUnscannable);
		Assert.Equal(FileContentClassification.UnsupportedEncoding, file.Classification);
		var unscannable = Assert.Single(prepared.UnscannableFiles);
		Assert.Equal(path, unscannable.Path);
		Assert.Equal(FileContentClassification.UnsupportedEncoding, unscannable.Classification);
		Assert.Equal(1, prepared.Snapshot!.SkippedFileCount);
		var preparedRead = await preparer
			.CreatePreparedAnalyzer(prepared)
			.ReadClassifiedAsync(path, long.MaxValue, TestContext.Current.CancellationToken);
		Assert.Equal(FileContentClassification.UnsupportedEncoding, preparedRead.Classification);
		Assert.Null(preparedRead.Content);
	}

	[Fact]
	public async Task OutputPreparer_DecoderFallback_IsClassifiedAsUnsupportedEncoding()
	{
		using var workspace = new TemporaryDirectory();
		var path = workspace.CreateFile("src/late-decode.txt", "source bytes remain operation-owned");
		using var session = new SecretRedactionSession(new EmptyDetector());
		var context = new SecretRedactionContext(workspace.Path, session);
		var preparer = new SecretRedactionOutputPreparer(new ThrowingDecodeContentAnalyzer());

		var discovery = await preparer.DiscoverAsync(
			context,
			[path],
			TestContext.Current.CancellationToken);
		await using var prepared = await preparer.PrepareAsync(
			new ContentTransformationContext(null, context),
			[path],
			TestContext.Current.CancellationToken);

		Assert.Equal(FileContentClassification.UnsupportedEncoding,
			Assert.Single(discovery.UnscannableFiles).Classification);
		Assert.Equal(FileContentClassification.UnsupportedEncoding,
			Assert.Single(prepared.UnscannableFiles).Classification);
		Assert.True(prepared.GetFile(path).IsUnscannable);
	}

	[Fact]
	public async Task OutputPreparer_PreservesCaseDistinctSourcesAndTheirContent()
	{
		using var workspace = new TemporaryDirectory();
		var upperContent = $"upper={Secret}-upper";
		var lowerContent = $"lower={Secret}-lower";
		Assert.Equal(upperContent.Length, lowerContent.Length);
		var physicalContent = new string('x', upperContent.Length);
		var upperPath = workspace.CreateFile("Foo.cs", physicalContent);
		var lowerPath = Path.Combine(workspace.Path, "foo.cs");
		if (!File.Exists(lowerPath))
			File.WriteAllText(lowerPath, physicalContent);
		var sourceContent = new Dictionary<string, string>(ProjectTreePathIdentity.CanonicalComparer)
		{
			[upperPath] = upperContent,
			[lowerPath] = lowerContent
		};
		var detector = new CountingDetector();
		using var session = new SecretRedactionSession(detector);
		using var compression = new CodeCompressionSession(new CaseIdentityCompressor());
		var analyzer = new CaseMappedContentAnalyzer(sourceContent);
		var preparer = new SecretRedactionOutputPreparer(analyzer, new FileContentAnalyzer());

		await using var prepared = await preparer.PrepareAsync(
			new ContentTransformationContext(
				new CodeCompressionContext(workspace.Path, compression),
				new SecretRedactionContext(workspace.Path, session)),
			[upperPath, lowerPath],
			TestContext.Current.CancellationToken);

		Assert.Equal(upperPath, prepared.GetFile(upperPath).SourcePath, StringComparer.Ordinal);
		Assert.Equal(lowerPath, prepared.GetFile(lowerPath).SourcePath, StringComparer.Ordinal);
		Assert.Equal(2, detector.CallCount);
		Assert.Equal(2, Assert.IsType<CodeCompressionSnapshot>(prepared.CompressionSnapshot).TotalFiles);

		var preparedAnalyzer = preparer.CreatePreparedAnalyzer(prepared);
		var upper = await preparedAnalyzer.TryReadAsTextAsync(
			upperPath,
			TestContext.Current.CancellationToken);
		var lower = await preparedAnalyzer.TryReadAsTextAsync(
			lowerPath,
			TestContext.Current.CancellationToken);
		Assert.NotNull(upper);
		Assert.NotNull(lower);
		Assert.NotEqual(upper.Content, lower.Content);
		Assert.Contains("upper", upper.Content, StringComparison.Ordinal);
		Assert.Contains("lower", lower.Content, StringComparison.Ordinal);
	}

	[Fact]
	public void Snapshot_CapturesUnscannableDetailsInsteadOfRetainingMutableOperationList()
	{
		using var workspace = new TemporaryDirectory();
		var path = workspace.CreateFile("src/legacy.txt", "legacy");
		using var session = new SecretRedactionSession(new EmptyDetector());
		var scope = session.BeginOutput(workspace.Path, [path]);
		scope.AnalyzeUnscannable(
			path,
			SecretFileMetadata.Capture(path),
			FileContentClassification.UnsupportedEncoding);
		var details = new List<UnscannableFile>
		{
			new(path, FileContentClassification.UnsupportedEncoding)
		};

		var snapshot = scope.Complete(skippedFileCount: 1, failedFileCount: 0, details);
		details.Clear();

		var captured = Assert.Single(snapshot.UnscannableFiles);
		Assert.Equal(path, captured.Path);
		Assert.Equal(FileContentClassification.UnsupportedEncoding, captured.Classification);
	}

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
	public async Task Discovery_RevalidatesSameLengthSameTimestampContent()
	{
		Assert.Equal(Secret.Length, SameLengthPublicValue.Length);
		using var workspace = new TemporaryDirectory();
		var path = workspace.CreateFile("src/config.env", $"token={Secret}\n");
		var timestamp = File.GetLastWriteTimeUtc(path);
		var detector = new CountingDetector();
		using var session = new SecretRedactionSession(detector);
		var preparer = new SecretRedactionOutputPreparer(new FileContentAnalyzer());
		var context = new SecretRedactionContext(workspace.Path, session);

		var first = await preparer.DiscoverAsync(
			context,
			[path],
			TestContext.Current.CancellationToken);
		Assert.Equal(1, first.RedactedCount);

		File.WriteAllText(path, $"token={SameLengthPublicValue}\n");
		File.SetLastWriteTimeUtc(path, timestamp);

		var second = await preparer.DiscoverAsync(
			context,
			[path],
			TestContext.Current.CancellationToken);

		Assert.Equal(0, second.RedactedCount);
		Assert.Equal(2, detector.CallCount);
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
	public void EquivalentProjectRootAliasesReuseDetectionCacheAndSnapshotIdentity()
	{
		using var workspace = new TemporaryDirectory();
		var path = workspace.CreateFile("src/config.env", $"token={Secret}\n");
		var content = File.ReadAllText(path);
		var metadata = SecretFileMetadata.Capture(path);
		var detector = new CountingDetector();
		using var session = new SecretRedactionSession(detector);
		var firstScope = session.BeginOutput(workspace.Path, [path]);
		firstScope.Analyze(path, content, metadata, TestContext.Current.CancellationToken);
		var first = firstScope.Complete();

		var aliasScope = session.BeginOutput(
			workspace.Path + Path.DirectorySeparatorChar,
			[path]);
		var reused = aliasScope.TryAnalyzeCached(path);
		var alias = aliasScope.Complete();

		Assert.True(reused);
		Assert.Equal(first.SelectionKey, alias.SelectionKey);
		Assert.Equal(1, detector.CallCount);
		Assert.Equal(1, session.GetCacheDiagnostics().DetectionRuns);
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
		using var measurement = ContentPipelineDiagnostics.BeginMeasurement();

		var raw = session.BeginOutput(workspace.Path, [path]);
		var rawPlan = raw.CreatePlan(
			path,
			content,
			ContentTransformMap.Identity,
			fingerprint,
			TestContext.Current.CancellationToken);
		_ = raw.Complete();
		var rawOccurrenceId = Assert.Single(rawPlan.Spans).OccurrenceId;
		var rawOccurrenceComputations = measurement.Capture().OccurrenceIdComputations;

		var compressed = session.BeginOutput(workspace.Path, [path], "signatures-v1");
		var compressedPlan = compressed.CreatePlan(
			path,
			content,
			ContentTransformMap.Identity,
			fingerprint,
			TestContext.Current.CancellationToken);
		_ = compressed.Complete();

		Assert.Equal(rawPlan.RedactedCount, compressedPlan.RedactedCount);
		Assert.Equal(1, rawOccurrenceComputations);
		Assert.Same(rawOccurrenceId, Assert.Single(compressedPlan.Spans).OccurrenceId);
		Assert.Equal(rawOccurrenceComputations, measurement.Capture().OccurrenceIdComputations);
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

	[Theory]
	[InlineData(GenerationInvalidation.Disable)]
	[InlineData(GenerationInvalidation.Reset)]
	[InlineData(GenerationInvalidation.ProjectSwitch)]
	public async Task ObsoleteScope_CannotRepopulateCacheOrPublishSnapshot(
		GenerationInvalidation invalidation)
	{
		using var workspace = new TemporaryDirectory();
		var firstRoot = Directory.CreateDirectory(Path.Combine(workspace.Path, "first")).FullName;
		var secondRoot = Directory.CreateDirectory(Path.Combine(workspace.Path, "second")).FullName;
		var firstPath = workspace.CreateFile("first/config.env", $"token={Secret}\n");
		var secondPath = workspace.CreateFile("second/config.env", $"token={Secret}\n");
		var detector = new BlockingFirstDetector();
		using var session = new SecretRedactionSession(detector);
		var published = 0;
		session.SnapshotPublished += (_, _) => Interlocked.Increment(ref published);
		var staleScope = session.BeginOutput(firstRoot, [firstPath]);

		var staleTask = Task.Run(
			() => staleScope.Redact(
				firstPath,
				File.ReadAllText(firstPath),
				TestContext.Current.CancellationToken),
			TestContext.Current.CancellationToken);
		Assert.True(detector.Entered.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

		SecretRedactionScope? currentScope = null;
		switch (invalidation)
		{
			case GenerationInvalidation.Disable:
				session.Disable();
				break;
			case GenerationInvalidation.Reset:
				session.Reset();
				break;
			case GenerationInvalidation.ProjectSwitch:
				currentScope = session.BeginOutput(secondRoot, [secondPath]);
				break;
			default:
				throw new ArgumentOutOfRangeException(nameof(invalidation), invalidation, null);
		}
		detector.Release.Set();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => staleTask);
		Assert.Equal(0, published);
		Assert.Equal(0, session.GetCacheDiagnostics().EntryCount);

		currentScope ??= session.BeginOutput(firstRoot, [firstPath]);
		var currentPath = invalidation == GenerationInvalidation.ProjectSwitch ? secondPath : firstPath;
		var current = currentScope.Redact(
			currentPath,
			File.ReadAllText(currentPath),
			TestContext.Current.CancellationToken);
		Assert.Equal(1, current.RedactedCount);
		_ = currentScope.Complete();
		Assert.Equal(1, session.GetCacheDiagnostics().EntryCount);
	}

	[Fact]
	public void ProjectSwitch_InvalidatesSelectionKeyCacheEvenWithoutPublishedSnapshots()
	{
		using var workspace = new TemporaryDirectory();
		var firstRoot = Directory.CreateDirectory(Path.Combine(workspace.Path, "first")).FullName;
		var secondRoot = Directory.CreateDirectory(Path.Combine(workspace.Path, "second")).FullName;
		var firstPath = workspace.CreateFile("first/config.env", $"token={Secret}\n");
		var secondPath = workspace.CreateFile("second/config.env", $"token={Secret}\n");
		using var session = new SecretRedactionSession(new CountingDetector());
		_ = session.BeginOutput(firstRoot, [firstPath]);
		var reference = CacheSelectionList(session, firstRoot, firstPath);

		_ = session.BeginOutput(secondRoot, [secondPath]);
		ForceCollection();

		Assert.False(reference.IsAlive);
	}

	[Fact]
	public async Task PublicRedact_RejectsConcurrentConsumersWithoutCorruptingScope()
	{
		using var workspace = new TemporaryDirectory();
		var path = workspace.CreateFile("src/config.env", $"token={Secret}\n");
		var detector = new BlockingFirstDetector();
		using var session = new SecretRedactionSession(detector);
		var scope = session.BeginOutput(workspace.Path, [path]);
		var content = File.ReadAllText(path);
		var first = Task.Run(
			() => scope.Redact(path, content, TestContext.Current.CancellationToken),
			TestContext.Current.CancellationToken);
		Assert.True(detector.Entered.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

		var exception = Assert.Throws<InvalidOperationException>(
			() => scope.Redact(path, content, TestContext.Current.CancellationToken));
		Assert.Contains("ordered consumer", exception.Message, StringComparison.Ordinal);
		Assert.Throws<InvalidOperationException>(() => scope.Complete());

		detector.Release.Set();
		var result = await first;
		Assert.Equal(1, result.RedactedCount);
		Assert.Equal(1, scope.Complete().RedactedCount);
	}

	[Fact]
	public void ProjectSwitch_DoesNotCarryManualMarksIntoTheNewRoot()
	{
		using var workspace = new TemporaryDirectory();
		var firstRoot = workspace.CreateFolder("first");
		var secondRoot = workspace.CreateFolder("second");
		var firstPath = workspace.CreateFile("first/config.env", $"token={Secret}\n");
		var secondPath = workspace.CreateFile("second/config.env", $"token={Secret}\n");
		using var session = new SecretRedactionSession(new EmptyDetector());
		session.ReplaceMarkedSecrets([
			new MarkedSecretProfileEntry(
				MarkedSecretValueNormalizer.ComputeHash(Secret),
				"TOKEN",
				Secret.Length)
		]);
		var first = session.BeginOutput(firstRoot, [firstPath]);
		Assert.Equal(1, first.Redact(
			firstPath,
			File.ReadAllText(firstPath),
			TestContext.Current.CancellationToken).RedactedCount);

		var second = session.BeginOutput(secondRoot, [secondPath]);

		Assert.Equal(0, second.Redact(
			secondPath,
			File.ReadAllText(secondPath),
			TestContext.Current.CancellationToken).RedactedCount);
	}

	[Fact]
	public void Snapshots_UseBoundedLruAndEvictOldestSelection()
	{
		using var workspace = new TemporaryDirectory();
		var path = workspace.CreateFile("src/config.env", "name=devprojex\n");
		using var session = new SecretRedactionSession(new CountingDetector());

		for (var index = 0; index < SecretRedactionSession.MaximumSnapshots + 8; index++)
		{
			var scope = session.BeginOutput(workspace.Path, [path], $"transform-{index:D2}");
			_ = scope.Complete();
		}

		Assert.Equal(SecretRedactionSession.MaximumSnapshots, session.SnapshotCount);
		Assert.Null(session.GetSnapshot(workspace.Path, [path], "transform-00"));
		Assert.NotNull(session.GetSnapshot(workspace.Path, [path], "transform-39"));
	}

	[Fact]
	public void InvalidateSnapshots_ReleasesCachedSelectionList()
	{
		using var workspace = new TemporaryDirectory();
		var path = workspace.CreateFile("src/config.env", "name=devprojex\n");
		using var session = new SecretRedactionSession(new CountingDetector());
		var reference = CacheSelectionList(session, workspace.Path, path);

		session.InvalidateSnapshots();
		ForceCollection();

		Assert.False(reference.IsAlive);
	}

	private static WeakReference CacheSelectionList(
		SecretRedactionSession session,
		string projectRoot,
		string path)
	{
		IReadOnlyList<string> selected = new[] { path };
		_ = session.GetSnapshot(projectRoot, selected);
		return new WeakReference(selected);
	}

	private static void ForceCollection()
	{
		for (var attempt = 0; attempt < 3; attempt++)
		{
			GC.Collect();
			GC.WaitForPendingFinalizers();
		}
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
		private int _callCount;

		public int CallCount => Volatile.Read(ref _callCount);

		public IReadOnlyList<DetectedSecret> Detect(
			string repositoryRelativePath,
			string content,
			CancellationToken cancellationToken = default)
		{
			Interlocked.Increment(ref _callCount);
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

	private sealed class CaseMappedContentAnalyzer(IReadOnlyDictionary<string, string> contentByPath)
		: IFileContentAnalyzer
	{
		public ValueTask<bool> IsTextFileAsync(
			string path,
			CancellationToken cancellationToken = default) =>
			ValueTask.FromResult(contentByPath.ContainsKey(path));

		public ValueTask<TextFileMetrics?> GetTextFileMetricsAsync(
			string path,
			CancellationToken cancellationToken = default)
		{
			var content = CreateContent(path);
			return ValueTask.FromResult<TextFileMetrics?>(new TextFileMetrics(
				content.SizeBytes,
				content.LineCount,
				content.CharCount,
				content.IsEmpty,
				content.IsWhitespaceOnly));
		}

		public ValueTask<TextFileContent?> TryReadAsTextAsync(
			string path,
			CancellationToken cancellationToken = default) =>
			ValueTask.FromResult<TextFileContent?>(CreateContent(path));

		public ValueTask<TextFileContent?> TryReadAsTextAsync(
			string path,
			long maxSizeForFullRead,
			CancellationToken cancellationToken = default) =>
			ValueTask.FromResult<TextFileContent?>(CreateContent(path));

		private TextFileContent CreateContent(string path)
		{
			var content = contentByPath[path];
			return new TextFileContent(
				content,
				Encoding.UTF8.GetByteCount(content),
				LineCount: 1,
				CharCount: content.Length,
				IsEmpty: false,
				IsWhitespaceOnly: false);
		}
	}

	private sealed class CaseIdentityCompressor : ICodeCompressor
	{
		public string TransformIdentity => "case-identity:v1";

		public bool IsSupported(string relativePath) => true;

		public ICodeCompressionScope CreateScope(string projectRoot) => new Scope(this);

		private sealed class Scope(CaseIdentityCompressor owner) : ICodeCompressionScope
		{
			public CodeCompressionAnalysis Analyze(
				string fullPath,
				string relativePath,
				string content,
				CancellationToken cancellationToken) =>
				new(
					CodeCompressionPlan.Unchanged(
						relativePath,
						content,
						CodeCompressionOutcome.UnchangedNoBenefit,
						content.Length,
						owner.TransformIdentity),
					null);

			public void Dispose()
			{
			}
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

	private sealed class BlockingFirstDetector : ISecretDetector
	{
		private int _calls;

		public ManualResetEventSlim Entered { get; } = new();
		public ManualResetEventSlim Release { get; } = new();

		public IReadOnlyList<DetectedSecret> Detect(
			string repositoryRelativePath,
			string content,
			CancellationToken cancellationToken = default)
		{
			if (Interlocked.Increment(ref _calls) == 1)
			{
				Entered.Set();
				Release.Wait(cancellationToken);
			}
			var start = content.IndexOf(Secret, StringComparison.Ordinal);
			return start < 0
				? []
				: [new DetectedSecret("cache-test", start, Secret.Length, Secret, 0)];
		}
	}

	private sealed class EmptyDetector : ISecretDetector
	{
		public IReadOnlyList<DetectedSecret> Detect(
			string repositoryRelativePath,
			string content,
			CancellationToken cancellationToken = default) => [];
	}

	private sealed class ClassifiedContentAnalyzer(FileContentClassification classification)
		: IFileContentAnalyzer
	{
		public ValueTask<bool> IsTextFileAsync(
			string path,
			CancellationToken cancellationToken = default) =>
			ValueTask.FromResult(false);

		public ValueTask<TextFileMetrics?> GetTextFileMetricsAsync(
			string path,
			CancellationToken cancellationToken = default) =>
			ValueTask.FromResult<TextFileMetrics?>(null);

		public ValueTask<TextFileContent?> TryReadAsTextAsync(
			string path,
			CancellationToken cancellationToken = default) =>
			ValueTask.FromResult<TextFileContent?>(null);

		public ValueTask<TextFileContent?> TryReadAsTextAsync(
			string path,
			long maxSizeForFullRead,
			CancellationToken cancellationToken = default) =>
			ValueTask.FromResult<TextFileContent?>(null);

		public ValueTask<FileContentReadResult> ReadClassifiedAsync(
			string path,
			long maxSizeForFullRead,
			CancellationToken cancellationToken = default) =>
			ValueTask.FromResult(new FileContentReadResult(classification));

		public ValueTask<ContentReadFact> ReadFactAsync(
			string path,
			long maxSizeForFullRead,
			CancellationToken cancellationToken = default) =>
			ValueTask.FromResult(new ContentReadFact(null, classification, null, null));
	}

	private sealed class ThrowingDecodeContentAnalyzer : IFileContentAnalyzer
	{
		public ValueTask<bool> IsTextFileAsync(
			string path,
			CancellationToken cancellationToken = default) =>
			ValueTask.FromException<bool>(new DecoderFallbackException("late decode failure"));

		public ValueTask<TextFileMetrics?> GetTextFileMetricsAsync(
			string path,
			CancellationToken cancellationToken = default) =>
			ValueTask.FromException<TextFileMetrics?>(new DecoderFallbackException("late decode failure"));

		public ValueTask<TextFileContent?> TryReadAsTextAsync(
			string path,
			CancellationToken cancellationToken = default) =>
			ValueTask.FromException<TextFileContent?>(new DecoderFallbackException("late decode failure"));

		public ValueTask<TextFileContent?> TryReadAsTextAsync(
			string path,
			long maxSizeForFullRead,
			CancellationToken cancellationToken = default) =>
			ValueTask.FromException<TextFileContent?>(new DecoderFallbackException("late decode failure"));

		public ValueTask<FileContentReadResult> ReadClassifiedAsync(
			string path,
			long maxSizeForFullRead,
			CancellationToken cancellationToken = default) =>
			ValueTask.FromException<FileContentReadResult>(new DecoderFallbackException("late decode failure"));

		public ValueTask<ContentReadFact> ReadFactAsync(
			string path,
			long maxSizeForFullRead,
			CancellationToken cancellationToken = default) =>
			ValueTask.FromException<ContentReadFact>(new DecoderFallbackException("late decode failure"));
	}

	public enum GenerationInvalidation
	{
		Disable,
		Reset,
		ProjectSwitch
	}
}
