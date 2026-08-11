using System.Collections.Concurrent;
using DevProjex.Application.Compression;
using DevProjex.Application.Diagnostics;

namespace DevProjex.Tests.Unit;

public sealed class CodeCompressionSessionTests
{
	[Fact]
	public void MeasurementScope_ResolvesPlanWithoutPublishingOrApplyingIt()
	{
		const string content = "prefix-0123456789-suffix";
		using var compressor = new FixedPlanCompressor();
		using var session = new CodeCompressionSession(compressor);
		var published = 0;
		session.SnapshotPublished += (_, _) => published++;
		using var measurement = ContentPipelineDiagnostics.BeginMeasurement();
		using var scope = session.BeginMeasurement("project");

		var plan = scope.ResolvePlan(
			"sample.cs",
			"sample.cs",
			content,
			CancellationToken.None);

		Assert.True(plan.HasEdits);
		Assert.Equal(0, measurement.Capture().PlanApplications);
		Assert.Same(CodeCompressionSnapshot.Empty, session.Snapshot);
		Assert.Equal(0, published);
		Assert.Throws<InvalidOperationException>(() => scope.Complete());
	}

	[Fact]
	public void SelectionKey_UsesTheFileSetRatherThanCallerEnumerationOrder()
	{
		var treeOrder = CodeCompressionSession.BuildSelectionKey(
			"project",
			["src/z.cs", "README.md", "src/a.cs"]);
		var previewOrder = CodeCompressionSession.BuildSelectionKey(
			"project",
			["README.md", "src/a.cs", "src/z.cs"]);
		var differentSelection = CodeCompressionSession.BuildSelectionKey(
			"project",
			["README.md", "src/a.cs"]);

		Assert.Equal(treeOrder, previewOrder);
		Assert.NotEqual(treeOrder, differentSelection);
	}

	[Fact]
	public void SamePathAndLengthWithDifferentContent_NeverReusesAPlan()
	{
		using var compressor = new RecordingCompressor();
		using var session = new CodeCompressionSession(compressor);

		using (var first = session.BeginOutput("project", ["sample.cs"]))
		{
			_ = first.Transform("sample.cs", "sample.cs", "aaaaaaaa", CancellationToken.None);
			Assert.Equal("aaaaaaaa", Assert.Single(first.Complete().Unchanged).LanguageId);
		}
		using (var second = session.BeginOutput("project", ["sample.cs"]))
		{
			_ = second.Transform("sample.cs", "sample.cs", "bbbbbbbb", CancellationToken.None);
			Assert.Equal("bbbbbbbb", Assert.Single(second.Complete().Unchanged).LanguageId);
		}

		Assert.Equal(2, compressor.AnalysisCount);
	}

	[Fact]
	public async Task ConcurrentRequestsForTheSameContent_UseOneAnalysis()
	{
		using var compressor = new RecordingCompressor(delayMilliseconds: 30);
		using var session = new CodeCompressionSession(compressor);
		using var scope = session.BeginOutput("project", ["sample.cs"]);

		await Task.WhenAll(Enumerable.Range(0, 24).Select(_ => Task.Run(() =>
			scope.Transform("sample.cs", "sample.cs", "same-content", CancellationToken.None))));
		var snapshot = scope.Complete();

		Assert.Equal(1, compressor.AnalysisCount);
		Assert.Equal(24, snapshot.UnchangedFiles);
	}

	[Fact]
	public async Task IndependentFilesCanBeAnalyzedConcurrently()
	{
		using var compressor = new RecordingCompressor(coordinateFirstPair: true);
		using var session = new CodeCompressionSession(compressor);
		string[] paths = ["first.cs", "second.cs"];
		using var scope = session.BeginOutput("project", paths);
		var cancellationToken = TestContext.Current.CancellationToken;

		// Dedicated workers make the concurrency contract independent from ThreadPool load on CI.
		var transforms = paths.Select((path, index) => Task.Factory.StartNew(
			() => scope.Transform(path, path, $"content-{index}", cancellationToken),
			cancellationToken,
			TaskCreationOptions.LongRunning,
			TaskScheduler.Default));
		await Task.WhenAll(transforms);
		_ = scope.Complete();

		Assert.Equal(2, compressor.MaximumConcurrency);
		Assert.Equal(2, compressor.AnalysisCount);
	}

	[Fact]
	public void UnsupportedFile_BypassesHashAndCompressor()
	{
		using var compressor = new RecordingCompressor(isSupported: false);
		using var session = new CodeCompressionSession(compressor);
		using var scope = session.BeginOutput("project", ["notes.txt"]);

		var result = scope.Transform("notes.txt", "notes.txt", "plain text", CancellationToken.None);
		var snapshot = scope.Complete();

		Assert.Equal("plain text", result.Text);
		Assert.Equal(0, compressor.AnalysisCount);
		Assert.Equal(0, session.Diagnostics.HashComputations);
		Assert.Equal(1, session.Diagnostics.UnsupportedFastPaths);
		Assert.Equal(CodeCompressionOutcome.UnchangedUnsupportedLanguage, Assert.Single(snapshot.Unchanged).Outcome);
	}

	[Fact]
	public async Task PrewarmThenTransform_ReusesThePreparedPlan()
	{
		using var temp = new TemporaryDirectory();
		var path = temp.CreateFile("sample.cs", "same-content");
		using var compressor = new RecordingCompressor();
		using var session = new CodeCompressionSession(compressor);
		var context = new CodeCompressionContext(temp.Path, session);

		var warmup = await new CodeCompressionPrewarmer(new FileContentAnalyzer())
			.WarmAsync(context, [path], TestContext.Current.CancellationToken);
		Assert.Equal(0, session.Diagnostics.HashComputations);
		Assert.NotNull(warmup.ReadFacts);
		Assert.True(warmup.ReadFacts.TryGet(path, out var retainedFact));
		Assert.Equal(ContentFingerprint.Compute("same-content"), retainedFact.Fingerprint);
		using var output = context.BeginOutput([path]);
		_ = output.Transform(path, "sample.cs", "same-content", TestContext.Current.CancellationToken);
		_ = output.Complete();

		Assert.Equal(1, warmup.WarmedFiles);
		Assert.Equal(1, compressor.AnalysisCount);
		Assert.Equal(1, session.Diagnostics.PrewarmAnalyses);
		Assert.Equal(1, session.Diagnostics.CacheHits);
		Assert.Equal(1, session.Snapshot.TotalFiles);
		Assert.Equal(0, session.Snapshot.CompressedFiles);
	}

	[Fact]
	public async Task Prewarm_DeduplicatesSelectionAndSkipsUnsupportedAndEmptyFiles()
	{
		using var temp = new TemporaryDirectory();
		var supported = temp.CreateFile("src/Supported.cs", "same-content");
		var empty = temp.CreateFile("src/Empty.cs", string.Empty);
		var unsupported = temp.CreateFile("notes/readme.txt", "plain text");
		using var compressor = new RecordingCompressor(
			isSupportedPath: static path =>
				Path.GetExtension(path).Equals(".cs", StringComparison.OrdinalIgnoreCase));
		using var session = new CodeCompressionSession(compressor);
		var progressValues = new ConcurrentQueue<CodeCompressionWarmupProgress>();
		string[] orderedPaths = [supported, supported, empty, unsupported, string.Empty];

		var result = await new CodeCompressionPrewarmer(new FileContentAnalyzer()).WarmAsync(
			new CodeCompressionContext(temp.Path, session),
			orderedPaths,
			TestContext.Current.CancellationToken,
			new InlineProgress<CodeCompressionWarmupProgress>(progressValues.Enqueue));

		Assert.Equal(3, result.CandidateFiles);
		Assert.Equal(3, result.WarmedFiles);
		Assert.Equal(0, result.SkippedFiles);
		Assert.Equal(0, result.FailedFiles);
		Assert.Equal(1, compressor.AnalysisCount);
		Assert.Equal(1, session.Diagnostics.PrewarmAnalyses);
		Assert.Equal([1, 2, 3], progressValues.Select(static value => value.ProcessedFiles).Order());
		Assert.All(progressValues, static value => Assert.Equal(3, value.TotalFiles));
		Assert.Equal(3, session.Snapshot.TotalFiles);
		Assert.Equal(
			Path.Combine("src", "Supported.cs"),
			session.Snapshot.Unchanged[0].RelativePath);
		Assert.Equal(
			CodeCompressionSession.BuildSelectionKey(
				temp.Path,
				orderedPaths
					.Where(static path => !string.IsNullOrWhiteSpace(path))
					.Distinct(PathComparer.Default)
					.ToArray()),
			session.Snapshot.SelectionKey);

		var prewarmedSnapshot = session.Snapshot;
		var selectionPaths = orderedPaths
			.Where(static path => !string.IsNullOrWhiteSpace(path))
			.Distinct(PathComparer.Default)
			.ToArray();
		using var output = new CodeCompressionContext(temp.Path, session).BeginOutput(selectionPaths);
		_ = output.Transform(
			supported,
			Path.Combine("src", "Supported.cs"),
			"same-content",
			TestContext.Current.CancellationToken);
		_ = output.Transform(
			empty,
			Path.Combine("src", "Empty.cs"),
			string.Empty,
			TestContext.Current.CancellationToken);
		_ = output.Transform(
			unsupported,
			Path.Combine("notes", "readme.txt"),
			"plain text",
			TestContext.Current.CancellationToken);
		var outputSnapshot = output.Complete();

		Assert.Equal(prewarmedSnapshot.SelectionKey, outputSnapshot.SelectionKey);
		Assert.Equal(prewarmedSnapshot.CompressedFiles, outputSnapshot.CompressedFiles);
		Assert.Equal(prewarmedSnapshot.UnchangedFiles, outputSnapshot.UnchangedFiles);
		Assert.Equal(prewarmedSnapshot.SourceCharacters, outputSnapshot.SourceCharacters);
		Assert.Equal(prewarmedSnapshot.TransformedCharacters, outputSnapshot.TransformedCharacters);
	}

	[Fact]
	public async Task Prewarm_BodiesModeStreamsCommentOnlyMetricsWithoutMaterializingContent()
	{
		const string source = "/* remove */\n.card { color: red; }\n";
		using var temp = new TemporaryDirectory();
		var path = temp.CreateFile("web/site.css", source);
		using var compressor = CodeCompressionTestHarness.CreateCompressor();
		using var session = new CodeCompressionSession(compressor);
		var context = new CodeCompressionContext(temp.Path, session, CodeTransformKinds.Bodies);
		var analyzer = new TrackingFileContentAnalyzer();
		using var measurement = ContentPipelineDiagnostics.BeginMeasurement();

		var result = await new CodeCompressionPrewarmer(analyzer).WarmAsync(
			context,
			[path],
			TestContext.Current.CancellationToken);
		var contentDiagnostics = measurement.Capture();

		Assert.Equal(1, result.WarmedFiles);
		Assert.Equal(0, analyzer.ReadFactCalls);
		Assert.Equal(1, analyzer.ClassifiedMetricsCalls);
		Assert.Equal(1, contentDiagnostics.FullFileReads);
		Assert.Equal(0, contentDiagnostics.ContentFingerprintComputations);
		Assert.NotNull(result.ReadFacts);
		Assert.Equal(128, result.ReadFacts.RetainedBytes);
		Assert.True(result.ReadFacts.TryGet(path, out var retainedFact));
		Assert.Null(retainedFact.Content);
		Assert.Null(retainedFact.Fingerprint);
		Assert.Equal(source.Length, retainedFact.RawMetrics?.CharCount);
		Assert.Equal(0, session.Diagnostics.AnalysisExecutions);
		Assert.Equal(1, session.Diagnostics.UnsupportedFastPaths);
		Assert.Equal(0, compressor.RuntimeDiagnostics.CompiledQuerySets);
		Assert.Equal(0, compressor.RuntimeDiagnostics.MaterializedWorkers);
		var outcome = Assert.Single(session.Snapshot.Unchanged);
		Assert.Equal(
			CodeCompressionOutcome.UnchangedUnsupportedLanguage,
			outcome.Outcome);
		Assert.Equal(source.Length, outcome.SourceCharacters);
		Assert.Equal(source.Length, session.Snapshot.SourceCharacters);
		Assert.Equal(source.Length, session.Snapshot.TransformedCharacters);
	}

	[Fact]
	public async Task Prewarm_SmallStatCannotUnderReserveLargeMaterializedFacts()
	{
		const int materializedCharacters = 9 * 1024 * 1024;
		const long maximumInFlightBytes = 32L * 1024 * 1024;
		const long oneDecodeScratch = 10L * 1024 * 1024 * sizeof(char);
		using var temp = new TemporaryDirectory();
		var paths = Enumerable.Range(0, 4)
			.Select(index => temp.CreateFile($"small-{index}.cs", "x"))
			.ToArray();
		var analyzer = new RetainedBytesTrackingAnalyzer(materializedCharacters);
		using var compressor = new RetainedBytesReleasingCompressor(analyzer);
		using var session = new CodeCompressionSession(compressor);

		var result = await new CodeCompressionPrewarmer(analyzer).WarmAsync(
			new CodeCompressionContext(temp.Path, session),
			paths,
			TestContext.Current.CancellationToken);

		Assert.Equal(paths.Length, result.WarmedFiles);
		Assert.Equal(0, analyzer.CurrentRetainedBytes);
		Assert.InRange(
			analyzer.PeakRetainedBytes,
			1,
			128L + materializedCharacters * sizeof(char));
		Assert.InRange(
			analyzer.PeakRetainedBytes,
			1,
			maximumInFlightBytes + oneDecodeScratch);
	}

	[Fact]
	public async Task Prewarm_UnknownLengthReservesForTheMaximumMaterializedFact()
	{
		const int materializedCharacters = 1024 * 1024;
		const long maximumInFlightBytes = 32L * 1024 * 1024;
		const long oneDecodeScratch = 10L * 1024 * 1024 * sizeof(char);
		using var temp = new TemporaryDirectory();
		var missingPath = Path.Combine(temp.Path, "stat-fails.cs");
		var analyzer = new RetainedBytesTrackingAnalyzer(materializedCharacters);
		using var compressor = new RetainedBytesReleasingCompressor(analyzer);
		using var session = new CodeCompressionSession(compressor);

		var result = await new CodeCompressionPrewarmer(analyzer).WarmAsync(
			new CodeCompressionContext(temp.Path, session),
			[missingPath],
			TestContext.Current.CancellationToken);

		Assert.Equal(1, result.WarmedFiles);
		Assert.Equal(0, analyzer.CurrentRetainedBytes);
		Assert.InRange(
			analyzer.PeakRetainedBytes,
			1,
			maximumInFlightBytes + oneDecodeScratch);
	}

	[Fact]
	public async Task Prewarm_EmptyUnsupportedFilePreservesNoBenefitOutcomeWithoutMaterializingContent()
	{
		using var temp = new TemporaryDirectory();
		var path = temp.CreateFile("web/empty.css", string.Empty);
		using var compressor = CodeCompressionTestHarness.CreateCompressor();
		using var session = new CodeCompressionSession(compressor);
		var analyzer = new TrackingFileContentAnalyzer();

		var result = await new CodeCompressionPrewarmer(analyzer).WarmAsync(
			new CodeCompressionContext(temp.Path, session, CodeTransformKinds.Bodies),
			[path],
			TestContext.Current.CancellationToken);

		Assert.Equal(1, result.WarmedFiles);
		Assert.Equal(0, analyzer.ReadFactCalls);
		Assert.Equal(1, analyzer.ClassifiedMetricsCalls);
		Assert.Equal(0, session.Diagnostics.UnsupportedFastPaths);
		Assert.Equal(CodeCompressionOutcome.UnchangedNoBenefit, Assert.Single(session.Snapshot.Unchanged).Outcome);
		Assert.Equal(0, session.Snapshot.SourceCharacters);
		Assert.Equal(0, session.Snapshot.TransformedCharacters);
	}

	[Fact]
	public async Task Prewarm_CancellationAfterUnsupportedMetricsDoesNotPublishOrCountTheFile()
	{
		using var temp = new TemporaryDirectory();
		var path = temp.CreateFile("web/site.css", ".card { color: red; }");
		using var cancellation = new CancellationTokenSource();
		using var compressor = CodeCompressionTestHarness.CreateCompressor();
		using var session = new CodeCompressionSession(compressor);
		var analyzer = new CancelingMetricsFileContentAnalyzer(cancellation);
		var publishedSnapshots = 0;
		session.SnapshotPublished += (_, _) => publishedSnapshots++;

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
			new CodeCompressionPrewarmer(analyzer).WarmAsync(
				new CodeCompressionContext(temp.Path, session, CodeTransformKinds.Bodies),
				[path],
				cancellation.Token));

		Assert.Equal(1, analyzer.ClassifiedMetricsCalls);
		Assert.Equal(0, publishedSnapshots);
		Assert.Same(CodeCompressionSnapshot.Empty, session.Snapshot);
		Assert.Equal(0, session.Diagnostics.UnsupportedFastPaths);
	}

	[Fact]
	public async Task Prewarm_SchedulesSynchronousUnsupportedMetricReadsConcurrently()
	{
		var paths = Enumerable.Range(0, 4)
			.Select(index => $"C:/project/site-{index}.css")
			.ToArray();
		using var compressor = CodeCompressionTestHarness.CreateCompressor();
		using var session = new CodeCompressionSession(compressor);
		using var analyzer = new SynchronousMetricsConcurrencyAnalyzer(requiredConcurrency: 2);

		var result = await Task.Run(() =>
				new CodeCompressionPrewarmer(analyzer).WarmAsync(
					new CodeCompressionContext("C:/project", session, CodeTransformKinds.Bodies),
					paths,
					TestContext.Current.CancellationToken),
			TestContext.Current.CancellationToken).WaitAsync(
			TimeSpan.FromSeconds(10),
			TestContext.Current.CancellationToken);

		Assert.Equal(paths.Length, result.WarmedFiles);
		Assert.True(analyzer.PeakConcurrentReads >= 2);
	}

	[Fact]
	public async Task Prewarm_ProducerFailureWaitsForActiveWorkerAndPreservesPrimaryException()
	{
		const string primaryMessage = "producer failed";
		using var compressor = new DisposalTrackingBlockingCompressor();
		using var session = new CodeCompressionSession(compressor);
		var analyzer = new CoordinatedFailureFileContentAnalyzer(compressor.Started.Task, primaryMessage);
		var cancellationToken = TestContext.Current.CancellationToken;
		var warmup = new CodeCompressionPrewarmer(analyzer).WarmAsync(
			new CodeCompressionContext("C:/project", session),
			["C:/project/blocked.cs", "C:/project/failure.cs"],
			cancellationToken);

		await compressor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
		await analyzer.FailureObserved.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
		try
		{
			var completed = await Task.WhenAny(
				warmup,
				Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken));
			Assert.NotSame(warmup, completed);
			Assert.False(compressor.DisposedWhileAnalyzing);
		}
		finally
		{
			compressor.Release();
		}

		var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => warmup);
		Assert.Equal(primaryMessage, exception.Message);
		Assert.False(compressor.DisposedWhileAnalyzing);
	}

	[Fact]
	public async Task Prewarm_WorkerFailureCancelsBlockedProducersAndPreservesPrimaryException()
	{
		const string primaryMessage = "analysis failed after the channel filled";
		var analyzer = new PipelineFillFileContentAnalyzer(requiredReads: 4);
		using var compressor = new PipelineFillFailureCompressor(analyzer.RequiredReadsReached.Task, primaryMessage);
		using var session = new CodeCompressionSession(compressor);
		var paths = Enumerable.Range(0, 6)
			.Select(index => $"C:/project/file-{index}.cs")
			.ToArray();

		var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
			new CodeCompressionPrewarmer(analyzer).WarmAsync(
				new CodeCompressionContext("C:/project", session),
				paths,
				TestContext.Current.CancellationToken).WaitAsync(
				TimeSpan.FromSeconds(5),
				TestContext.Current.CancellationToken));

		Assert.Equal(primaryMessage, exception.Message);
		Assert.True(analyzer.ReadFactCalls >= 4);
		Assert.Equal(0, compressor.ActiveAnalyses);
		Assert.Equal(0, session.Diagnostics.CacheEntries);
		Assert.Same(CodeCompressionSnapshot.Empty, session.Snapshot);
	}

	[Fact]
	public async Task Prewarm_PublishesExactCompressionFactsBeforeAnyOutputIsBuilt()
	{
		const string source = "prefix-1234567890-suffix";
		using var temp = new TemporaryDirectory();
		var path = temp.CreateFile("sample.cs", source);
		using var compressor = new FixedPlanCompressor();
		using var session = new CodeCompressionSession(compressor);
		var publishedSnapshots = 0;
		session.SnapshotPublished += (_, _) => publishedSnapshots++;

		await new CodeCompressionPrewarmer(new FileContentAnalyzer()).WarmAsync(
			new CodeCompressionContext(temp.Path, session),
			[path],
			TestContext.Current.CancellationToken);

		Assert.Equal(1, publishedSnapshots);
		Assert.Equal(1, session.Snapshot.CompressedFiles);
		Assert.Equal(0, session.Snapshot.UnchangedFiles);
		Assert.Equal(source.Length, session.Snapshot.SourceCharacters);
		Assert.Equal(source.Length - 7, session.Snapshot.TransformedCharacters);

		var prewarmedSnapshot = session.Snapshot;
		using var output = session.BeginOutput(temp.Path, [path]);
		_ = output.Transform(
			path,
			"sample.cs",
			source,
			TestContext.Current.CancellationToken);
		var outputSnapshot = output.Complete();

		Assert.Equal(prewarmedSnapshot.SelectionKey, outputSnapshot.SelectionKey);
		Assert.Equal(prewarmedSnapshot.CompressedFiles, outputSnapshot.CompressedFiles);
		Assert.Equal(prewarmedSnapshot.UnchangedFiles, outputSnapshot.UnchangedFiles);
		Assert.Equal(prewarmedSnapshot.SourceCharacters, outputSnapshot.SourceCharacters);
		Assert.Equal(prewarmedSnapshot.TransformedCharacters, outputSnapshot.TransformedCharacters);
	}

	[Fact]
	public void PrewarmAndOutputDisplayPathVariants_ShareAnalysisBySourceFile()
	{
		using var compressor = new RecordingCompressor();
		using var session = new CodeCompressionSession(compressor);
		const string fullPath = "C:/project/src/sample.cs";
		const string content = "same-content";

		using (var warmup = session.BeginOutput("C:/project", [fullPath]))
			warmup.Warm(fullPath, @"src\sample.cs", content, CancellationToken.None);
		using var output = session.BeginOutput("C:/project", [fullPath]);
		_ = output.Transform(fullPath, "src/sample.cs", content, CancellationToken.None);
		var snapshot = output.Complete();

		Assert.Equal(1, compressor.AnalysisCount);
		Assert.Equal(1, session.Diagnostics.CacheHits);
		Assert.Equal("src/sample.cs", Assert.Single(snapshot.Unchanged).RelativePath);
	}

	[Fact]
	public async Task TransformDuringPrewarm_SharesTheInFlightAnalysis()
	{
		using var compressor = new BlockingCompressor();
		using var session = new CodeCompressionSession(compressor);
		using var warmScope = session.BeginOutput("project", ["sample.cs"]);
		using var outputScope = session.BeginOutput("project", ["sample.cs"]);
		var cancellationToken = TestContext.Current.CancellationToken;

		var warmup = StartBlockingOperation(
			() => warmScope.Warm("sample.cs", "sample.cs", "same-content", cancellationToken),
			cancellationToken);
		await compressor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
		var output = StartBlockingOperation(
			() => outputScope.Transform("sample.cs", "sample.cs", "same-content", cancellationToken),
			cancellationToken);

		try
		{
			Assert.True(
				SpinWait.SpinUntil(
					() => session.Diagnostics.PrewarmReuses == 1,
					TimeSpan.FromSeconds(5)),
				"The output request did not join the active prewarm analysis.");
		}
		finally
		{
			compressor.Release();
		}
		await Task.WhenAll(warmup, output);
		_ = outputScope.Complete();

		Assert.Equal(1, compressor.AnalysisCount);
		Assert.Equal(1, session.Diagnostics.PrewarmReuses);
	}

	[Fact]
	public async Task CanceledPrewarmWaiter_DoesNotDiscardGenerationOwnedSharedResult()
	{
		using var compressor = new BlockingCompressor();
		using var session = new CodeCompressionSession(compressor);
		using var canceledScope = session.BeginOutput("project", ["sample.cs"]);
		using var survivingScope = session.BeginOutput("project", ["sample.cs"]);
		using var callerCancellation = new CancellationTokenSource();
		var testCancellation = TestContext.Current.CancellationToken;

		var canceledWaiter = StartBlockingOperation(
			() => canceledScope.Warm(
				"sample.cs",
				"sample.cs",
				"same-content",
				callerCancellation.Token),
			testCancellation);
		await compressor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), testCancellation);
		callerCancellation.Cancel();
		var survivingWaiter = StartBlockingOperation(
			() => survivingScope.Warm(
				"sample.cs",
				"sample.cs",
				"same-content",
				testCancellation),
			testCancellation);

		try
		{
			Assert.True(
				SpinWait.SpinUntil(
					() => session.Diagnostics.PrewarmRequests == 2,
					TimeSpan.FromSeconds(5)),
				"The surviving waiter did not reach the shared prewarm operation.");
		}
		finally
		{
			compressor.Release();
		}

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledWaiter);
		Assert.True(await survivingWaiter);

		Assert.Equal(1, compressor.AnalysisCount);
		Assert.Equal(1, session.Diagnostics.AnalysisExecutions);
		Assert.Equal(1, session.Diagnostics.CacheEntries);
	}

	[Fact]
	public async Task PrewarmDuringTransform_SharesTheInFlightAnalysis()
	{
		using var compressor = new BlockingCompressor();
		using var session = new CodeCompressionSession(compressor);
		using var outputScope = session.BeginOutput("project", ["sample.cs"]);
		using var warmScope = session.BeginOutput("project", ["sample.cs"]);
		var cancellationToken = TestContext.Current.CancellationToken;

		var output = StartBlockingOperation(
			() => outputScope.Transform("sample.cs", "sample.cs", "same-content", cancellationToken),
			cancellationToken);
		await compressor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
		var warmup = StartBlockingOperation(
			() => warmScope.Warm("sample.cs", "sample.cs", "same-content", cancellationToken),
			cancellationToken);

		try
		{
			Assert.True(
				SpinWait.SpinUntil(
					() => session.Diagnostics.PrewarmReuses == 1,
					TimeSpan.FromSeconds(5)),
				"The prewarm request did not join the active output analysis.");
		}
		finally
		{
			compressor.Release();
		}

		await Task.WhenAll(output, warmup);
		_ = outputScope.Complete();

		Assert.Equal(1, compressor.AnalysisCount);
		Assert.Equal(1, session.Diagnostics.PrewarmReuses);
	}

	[Fact]
	public async Task Prewarm_LargerThanPlanCache_EvaluatesTheWholeSelectionAndKeepsCacheBounded()
	{
		var paths = Enumerable.Range(0, CodeCompressionSession.PlanCacheCapacity + 1)
			.Select(index => $"sample-{index:D5}.cs")
			.ToArray();
		using var compressor = new RecordingCompressor();
		using var session = new CodeCompressionSession(compressor);

		var result = await new CodeCompressionPrewarmer(new ConstantFileContentAnalyzer())
			.WarmAsync(
				new CodeCompressionContext("project", session),
				paths,
				TestContext.Current.CancellationToken);

		Assert.Equal(paths.Length, result.CandidateFiles);
		Assert.Equal(paths.Length, compressor.AnalysisCount);
		Assert.Equal(paths.Length, session.Snapshot.TotalFiles);
		Assert.Equal(CodeCompressionSession.PlanCacheCapacity, session.Diagnostics.CacheEntries);
	}

	[Fact]
	public void FiveThousandSequentialFiles_AreWarmOnTheSecondFullPass()
	{
		const int fileCount = 5_000;
		var paths = Enumerable.Range(0, fileCount)
			.Select(index => $"src/sample-{index:D5}.cs")
			.ToArray();
		using var compressor = new RecordingCompressor();
		using var session = new CodeCompressionSession(compressor);

		for (var pass = 0; pass < 2; pass++)
		{
			using var scope = session.BeginOutput("project", paths);
			foreach (var path in paths)
				_ = scope.Transform(path, path, $"content:{path}", CancellationToken.None);
		}

		var diagnostics = session.Diagnostics;
		Assert.Equal(fileCount, compressor.AnalysisCount);
		Assert.Equal(fileCount, diagnostics.CacheHits);
		Assert.Equal(fileCount, diagnostics.CacheEntries);
		Assert.InRange(
			diagnostics.RetainedCacheBytes,
			1,
			diagnostics.MaximumRetainedCacheBytes);
	}

	[Fact]
	public void Snapshot_BoundsUnchangedDetailsWithoutChangingExactAggregates()
	{
		var paths = Enumerable.Range(0, 300)
			.Select(index => $"src/sample-{index:D3}.cs")
			.ToArray();
		using var compressor = new RecordingCompressor();
		using var session = new CodeCompressionSession(compressor);
		using var scope = session.BeginOutput("project", paths);
		foreach (var path in paths.Reverse())
			_ = scope.Transform(path, path, $"content:{path}", CancellationToken.None);

		var snapshot = scope.Complete();

		Assert.Equal(300, snapshot.UnchangedFiles);
		Assert.Equal(CodeCompressionScope.MaximumUnchangedDiagnosticExamples, snapshot.Unchanged.Count);
		Assert.Equal(44, snapshot.AdditionalUnchangedFiles);
		Assert.Equal(paths[0], snapshot.Unchanged[0].RelativePath);
		Assert.Equal(paths[255], snapshot.Unchanged[^1].RelativePath);
		Assert.Equal(
			300,
			snapshot.UnchangedOutcomeCounts![CodeCompressionOutcome.UnchangedNoBenefit]);
	}

	[Fact]
	public void PlanCache_EvictsByRetainedBytesAndDoesNotRetainOversizedEntry()
	{
		using var compressor = new RecordingCompressor();
		using var bounded = new CodeCompressionSession(
			compressor,
			maximumCacheEntries: 100,
			maximumRetainedCacheBytes: 1_500);
		using (var scope = bounded.BeginOutput("project", []))
		{
			for (var index = 0; index < 20; index++)
			{
				var path = $"src/long-file-name-{index:D4}.cs";
				_ = scope.Transform(path, path, $"content-{index:D4}", CancellationToken.None);
			}
		}
		var boundedDiagnostics = bounded.Diagnostics;
		Assert.InRange(boundedDiagnostics.RetainedCacheBytes, 1, 1_500);
		Assert.InRange(boundedDiagnostics.CacheEntries, 1, 19);

		using var oversizedCompressor = new RecordingCompressor();
		using var oversized = new CodeCompressionSession(
			oversizedCompressor,
			maximumCacheEntries: 100,
			maximumRetainedCacheBytes: 128);
		for (var pass = 0; pass < 2; pass++)
		{
			using var scope = oversized.BeginOutput("project", ["sample.cs"]);
			_ = scope.Transform("sample.cs", "sample.cs", "same-content", CancellationToken.None);
		}

		Assert.Equal(2, oversizedCompressor.AnalysisCount);
		Assert.Equal(0, oversized.Diagnostics.CacheEntries);
		Assert.Equal(0, oversized.Diagnostics.RetainedCacheBytes);
	}

	[Fact]
	public async Task ResetWhileAnalysisIsActive_CannotRestoreAnObsoletePlanOrSnapshot()
	{
		using var compressor = new BlockingCompressor();
		using var session = new CodeCompressionSession(compressor);
		using var obsoleteScope = session.BeginOutput("old-project", ["sample.cs"]);
		var cancellationToken = TestContext.Current.CancellationToken;

		var obsoleteTransform = Task.Run(
			() => obsoleteScope.Transform("sample.cs", "sample.cs", "same-content", cancellationToken),
			cancellationToken);
		await compressor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);

		session.Reset();
		compressor.Release();
		await obsoleteTransform;
		_ = obsoleteScope.Complete();

		Assert.Equal(CodeCompressionSnapshot.Empty, session.Snapshot);
		using var currentScope = session.BeginOutput("new-project", ["sample.cs"]);
		_ = currentScope.Transform("sample.cs", "sample.cs", "same-content", cancellationToken);
		var currentSnapshot = currentScope.Complete();

		Assert.Equal(2, compressor.AnalysisCount);
		Assert.Same(currentSnapshot, session.Snapshot);
		Assert.Equal(1, session.Diagnostics.AnalysisExecutions);
	}

	[Fact]
	public void ResetBeforeObsoleteScopeTransforms_AllowsCompletionWithoutCachingOrPublishing()
	{
		using var compressor = new RecordingCompressor();
		using var session = new CodeCompressionSession(compressor);
		using var obsoleteScope = session.BeginOutput("old-project", ["sample.cs"]);
		var publishedSnapshots = 0;
		session.SnapshotPublished += (_, _) => publishedSnapshots++;

		session.Reset();
		var obsoleteResult = obsoleteScope.Transform(
			"sample.cs",
			"sample.cs",
			"same-content",
			CancellationToken.None);
		var obsoleteSnapshot = obsoleteScope.Complete();

		Assert.Equal("same-content", obsoleteResult.Text);
		Assert.Single(obsoleteSnapshot.Unchanged);
		Assert.Equal(CodeCompressionSnapshot.Empty, session.Snapshot);
		Assert.Equal(0, publishedSnapshots);
		Assert.Equal(0, session.Diagnostics.CacheEntries);
		Assert.Equal(0, session.Diagnostics.RetainedCacheBytes);
		Assert.Equal(0, session.Diagnostics.HashComputations);

		using var currentScope = session.BeginOutput("new-project", ["sample.cs"]);
		_ = currentScope.Transform(
			"sample.cs",
			"sample.cs",
			"same-content",
			CancellationToken.None);
		var currentSnapshot = currentScope.Complete();

		Assert.Equal(2, compressor.AnalysisCount);
		Assert.Same(currentSnapshot, session.Snapshot);
		Assert.Equal(1, publishedSnapshots);
		Assert.Equal(1, session.Diagnostics.CacheEntries);
	}

	[Fact]
	public void DisposeBeforeExistingScopeTransforms_AllowsSafeLocalCompletionWithoutPublishing()
	{
		using var compressor = new RecordingCompressor();
		var session = new CodeCompressionSession(compressor);
		using var existingScope = session.BeginOutput("project", ["sample.cs"]);
		var publishedSnapshots = 0;
		session.SnapshotPublished += (_, _) => publishedSnapshots++;

		session.Dispose();
		var result = existingScope.Transform(
			"sample.cs",
			"sample.cs",
			"same-content",
			CancellationToken.None);
		var localSnapshot = existingScope.Complete();

		Assert.Equal("same-content", result.Text);
		Assert.Single(localSnapshot.Unchanged);
		Assert.Equal(CodeCompressionSnapshot.Empty, session.Snapshot);
		Assert.Equal(0, publishedSnapshots);
	}

	[Fact]
	public void ResetBeforeQueuedPrewarmBegins_CannotPopulateTheCurrentGenerationCache()
	{
		using var compressor = new RecordingCompressor();
		using var session = new CodeCompressionSession(compressor);
		using var obsoleteWarmup = session.BeginOutput("old-project", ["sample.cs"]);

		session.Reset();
		obsoleteWarmup.Warm(
			"sample.cs",
			"sample.cs",
			"same-content",
			CancellationToken.None);

		Assert.Equal(0, compressor.AnalysisCount);
		Assert.Equal(0, session.Diagnostics.PrewarmRequests);
		using var current = session.BeginOutput("new-project", ["sample.cs"]);
		_ = current.Transform(
			"sample.cs",
			"sample.cs",
			"same-content",
			CancellationToken.None);
		_ = current.Complete();

		Assert.Equal(1, compressor.AnalysisCount);
		Assert.Equal(0, session.Diagnostics.CacheHits);
		Assert.Equal(1, session.Diagnostics.CacheMisses);
	}

	[Fact]
	public async Task CanceledForegroundAnalysis_DoesNotPoisonIndependentWarmup()
	{
		using var compressor = new CancelFirstAnalysisCompressor();
		using var session = new CodeCompressionSession(compressor);
		using var outputScope = session.BeginOutput("project", ["sample.cs"]);
		using var warmScope = session.BeginOutput("project", ["sample.cs"]);
		using var outputCancellation = new CancellationTokenSource();
		var cancellationToken = TestContext.Current.CancellationToken;

		var output = StartBlockingOperation(
			() => outputScope.Transform(
				"sample.cs",
				"sample.cs",
				"same-content",
				outputCancellation.Token),
			cancellationToken);
		await compressor.FirstAnalysisStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
		var warmup = StartBlockingOperation(
			() => warmScope.Warm("sample.cs", "sample.cs", "same-content", cancellationToken),
			cancellationToken);
		Assert.True(
			SpinWait.SpinUntil(
				() => session.Diagnostics.PrewarmReuses == 1,
				TimeSpan.FromSeconds(5)),
			"Warmup did not join the active foreground analysis.");

		outputCancellation.Cancel();
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => output);
		await warmup;

		using var verificationScope = session.BeginOutput("project", ["sample.cs"]);
		_ = verificationScope.Transform(
			"sample.cs",
			"sample.cs",
			"same-content",
			cancellationToken);
		_ = verificationScope.Complete();

		Assert.Equal(2, compressor.AnalysisCount);
		Assert.Equal(1, session.Diagnostics.CacheHits);
	}

	private static Task<TResult> StartBlockingOperation<TResult>(
		Func<TResult> operation,
		CancellationToken cancellationToken) =>
		Task.Factory.StartNew(
			operation,
			cancellationToken,
			TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
			TaskScheduler.Default);

	private sealed class RecordingCompressor(
		int delayMilliseconds = 0,
		bool isSupported = true,
		bool coordinateFirstPair = false,
		Func<string, bool>? isSupportedPath = null) : ICodeCompressor, IDisposable
	{
		private readonly ManualResetEventSlim _firstPairReady = new(false);
		private readonly int _delayMilliseconds = delayMilliseconds;
		private int _analysisCount;
		private int _active;
		private int _firstPairArrivals;
		private int _maximumConcurrency;

		public string TransformIdentity => "recording:v1";
		public int AnalysisCount => Volatile.Read(ref _analysisCount);
		public int MaximumConcurrency => Volatile.Read(ref _maximumConcurrency);
		public bool IsSupported(string relativePath) =>
			isSupportedPath?.Invoke(relativePath) ?? isSupported;
		public ICodeCompressionScope CreateScope(string projectRoot) => new Scope(this);
		public void Dispose() => _firstPairReady.Dispose();

		private void CoordinateFirstPair(CancellationToken cancellationToken)
		{
			if (!coordinateFirstPair || Volatile.Read(ref _firstPairArrivals) >= 2)
				return;

			if (Interlocked.Increment(ref _firstPairArrivals) >= 2)
				_firstPairReady.Set();

			if (!_firstPairReady.Wait(TimeSpan.FromSeconds(5), cancellationToken))
				throw new TimeoutException("Independent compression analyses were serialized.");
		}

		private sealed class Scope(RecordingCompressor owner) : ICodeCompressionScope
		{
			public CodeCompressionAnalysis Analyze(
				string fullPath,
				string relativePath,
				string content,
				CancellationToken cancellationToken)
			{
				Interlocked.Increment(ref owner._analysisCount);
				var active = Interlocked.Increment(ref owner._active);
				UpdateMaximum(ref owner._maximumConcurrency, active);
				try
				{
					owner.CoordinateFirstPair(cancellationToken);
					if (owner._delayMilliseconds > 0)
						Thread.Sleep(owner._delayMilliseconds);
					return new CodeCompressionAnalysis(
						CodeCompressionPlan.Unchanged(
							relativePath,
							content,
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

			public void Dispose() { }
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

	private sealed class BlockingCompressor : ICodeCompressor, IDisposable
	{
		private readonly ManualResetEventSlim _release = new(false);
		private int _analysisCount;

		public string TransformIdentity => "blocking:v1";
		public int AnalysisCount => Volatile.Read(ref _analysisCount);
		public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public bool IsSupported(string relativePath) => true;
		public ICodeCompressionScope CreateScope(string projectRoot) => new Scope(this);
		public void Release() => _release.Set();

		public void Dispose() => _release.Dispose();

		private sealed class Scope(BlockingCompressor owner) : ICodeCompressionScope
		{
			public CodeCompressionAnalysis Analyze(
				string fullPath,
				string relativePath,
				string content,
				CancellationToken cancellationToken)
			{
				Interlocked.Increment(ref owner._analysisCount);
				owner.Started.TrySetResult();
				owner._release.Wait(cancellationToken);
				return new CodeCompressionAnalysis(
					CodeCompressionPlan.Unchanged(
						relativePath,
						"test",
						CodeCompressionOutcome.UnchangedNoBenefit,
						content.Length,
						owner.TransformIdentity),
					null);
			}

			public void Dispose()
			{
			}
		}
	}

	private sealed class FixedPlanCompressor : ICodeCompressor, IDisposable
	{
		public string TransformIdentity => "fixed-plan:v1";

		public bool IsSupported(string relativePath) => true;

		public ICodeCompressionScope CreateScope(string projectRoot) => new Scope(this);

		public void Dispose()
		{
		}

		private sealed class Scope(FixedPlanCompressor owner) : ICodeCompressionScope
		{
			public CodeCompressionAnalysis Analyze(
				string fullPath,
				string relativePath,
				string content,
				CancellationToken cancellationToken) =>
				new(
					CodeCompressionPlan.Create(
						relativePath,
						"csharp",
						[new CodeCompressionEdit(7, 10, "...")],
						content.Length,
						owner.TransformIdentity),
					null);

			public void Dispose()
			{
			}
		}
	}

	private sealed class CancelFirstAnalysisCompressor : ICodeCompressor, IDisposable
	{
		private int _analysisCount;

		public string TransformIdentity => "cancel-first:v1";
		public int AnalysisCount => Volatile.Read(ref _analysisCount);
		public TaskCompletionSource FirstAnalysisStarted { get; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		public bool IsSupported(string relativePath) => true;
		public ICodeCompressionScope CreateScope(string projectRoot) => new Scope(this);
		public void Dispose() { }

		private sealed class Scope(CancelFirstAnalysisCompressor owner) : ICodeCompressionScope
		{
			public CodeCompressionAnalysis Analyze(
				string fullPath,
				string relativePath,
				string content,
				CancellationToken cancellationToken)
			{
				var call = Interlocked.Increment(ref owner._analysisCount);
				if (call == 1)
				{
					owner.FirstAnalysisStarted.TrySetResult();
					cancellationToken.WaitHandle.WaitOne();
					cancellationToken.ThrowIfCancellationRequested();
				}

				return new CodeCompressionAnalysis(
					CodeCompressionPlan.Unchanged(
						relativePath,
						"test",
						CodeCompressionOutcome.UnchangedNoBenefit,
						content.Length,
						owner.TransformIdentity),
					null);
			}

			public void Dispose() { }
		}
	}

	private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
	{
		public void Report(T value) => report(value);
	}

	private static async ValueTask<BudgetedContentReadResult> ReadTestFactWithBudgetAsync(
		Func<ValueTask<ContentReadFact>> readFact,
		long retainedBytes,
		WeightedByteBudget byteBudget,
		SemaphoreSlim decodeScratchGate,
		CancellationToken cancellationToken)
	{
		var lease = await byteBudget.AcquireAsync(retainedBytes, cancellationToken);
		await decodeScratchGate.WaitAsync(cancellationToken);
		try
		{
			var fact = await readFact();
			return new BudgetedContentReadResult(fact, null, lease);
		}
		catch
		{
			lease.Dispose();
			throw;
		}
		finally
		{
			decodeScratchGate.Release();
		}
	}

	private sealed class RetainedBytesTrackingAnalyzer : IFileContentAnalyzer
	{
		private readonly int _contentCharacters;
		private readonly ContentFingerprint _fingerprint;
		private long _currentRetainedBytes;
		private long _peakRetainedBytes;

		public RetainedBytesTrackingAnalyzer(int contentCharacters)
		{
			_contentCharacters = contentCharacters;
			_fingerprint = ContentFingerprint.Compute(new string('x', contentCharacters));
		}

		public long CurrentRetainedBytes => Interlocked.Read(ref _currentRetainedBytes);
		public long PeakRetainedBytes => Interlocked.Read(ref _peakRetainedBytes);

		public FileContentClassification? ClassifyWithoutReading(string path) => null;

		public ValueTask<ContentReadFact> ReadFactAsync(
			string path,
			long maxSizeForFullRead,
			CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var content = new string('x', _contentCharacters);
			var retainedBytes = 128L + content.Length * sizeof(char);
			var current = Interlocked.Add(ref _currentRetainedBytes, retainedBytes);
			UpdateMaximum(ref _peakRetainedBytes, current);
			return ValueTask.FromResult(new ContentReadFact(
				content,
				FileContentClassification.Text,
				new TextFileMetrics(
					SizeBytes: content.Length,
					LineCount: 1,
					CharCount: content.Length,
					IsEmpty: false,
					IsWhitespaceOnly: false),
				_fingerprint));
		}

		public void Release(string content)
		{
			var retainedBytes = 128L + content.Length * sizeof(char);
			Interlocked.Add(ref _currentRetainedBytes, -retainedBytes);
		}

		public ValueTask<bool> IsTextFileAsync(
			string path,
			CancellationToken cancellationToken = default) =>
			throw new NotSupportedException();

		public ValueTask<TextFileMetrics?> GetTextFileMetricsAsync(
			string path,
			CancellationToken cancellationToken = default) =>
			throw new NotSupportedException();

		public ValueTask<TextFileContent?> TryReadAsTextAsync(
			string path,
			CancellationToken cancellationToken = default) =>
			throw new NotSupportedException();

		public ValueTask<TextFileContent?> TryReadAsTextAsync(
			string path,
			long maxSizeForFullRead,
			CancellationToken cancellationToken = default) =>
			throw new NotSupportedException();

		private static void UpdateMaximum(ref long target, long candidate)
		{
			var current = Interlocked.Read(ref target);
			while (candidate > current)
			{
				var observed = Interlocked.CompareExchange(ref target, candidate, current);
				if (observed == current)
					return;
				current = observed;
			}
		}
	}

	private sealed class RetainedBytesReleasingCompressor(
		RetainedBytesTrackingAnalyzer analyzer) : ICodeCompressor, IDisposable
	{
		public string TransformIdentity => "retained-budget-test:v1";
		public bool IsSupported(string relativePath) => true;
		public ICodeCompressionScope CreateScope(string projectRoot) => new Scope(analyzer, TransformIdentity);
		public void Dispose()
		{
		}

		private sealed class Scope(
			RetainedBytesTrackingAnalyzer analyzer,
			string transformIdentity) : ICodeCompressionScope
		{
			public CodeCompressionAnalysis Analyze(
				string fullPath,
				string relativePath,
				string content,
				CancellationToken cancellationToken)
			{
				try
				{
					cancellationToken.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(50));
					cancellationToken.ThrowIfCancellationRequested();
					return new CodeCompressionAnalysis(
						CodeCompressionPlan.Unchanged(
							relativePath,
							"test",
							CodeCompressionOutcome.UnchangedNoBenefit,
							content.Length,
							transformIdentity),
						null);
				}
				finally
				{
					analyzer.Release(content);
				}
			}

			public void Dispose()
			{
			}
		}
	}

	private sealed class TrackingFileContentAnalyzer : IFileContentAnalyzer, IPrewarmFileContentAnalyzer
	{
		private readonly FileContentAnalyzer inner = new();
		private int _classifiedMetricsCalls;
		private int _readFactCalls;

		public int ClassifiedMetricsCalls => Volatile.Read(ref _classifiedMetricsCalls);
		public int ReadFactCalls => Volatile.Read(ref _readFactCalls);

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

		public ValueTask<FileContentMetricsResult> GetClassifiedMetricsAsync(
			string path,
			CancellationToken cancellationToken = default)
		{
			Interlocked.Increment(ref _classifiedMetricsCalls);
			return inner.GetClassifiedMetricsAsync(path, cancellationToken);
		}

		public ValueTask<IdentifiedFileContentMetricsResult> GetClassifiedMetricsWithIdentityAsync(
			string path,
			CancellationToken cancellationToken = default)
		{
			Interlocked.Increment(ref _classifiedMetricsCalls);
			return ((IPrewarmFileContentAnalyzer)inner).GetClassifiedMetricsWithIdentityAsync(
				path,
				cancellationToken);
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

		public ValueTask<ContentReadFact> ReadFactAsync(
			string path,
			long maxSizeForFullRead,
			CancellationToken cancellationToken = default)
		{
			Interlocked.Increment(ref _readFactCalls);
			return inner.ReadFactAsync(path, maxSizeForFullRead, cancellationToken);
		}

		public ValueTask<BudgetedContentReadResult> ReadFactWithBudgetAsync(
			string path,
			long maximumReadBytes,
			WeightedByteBudget byteBudget,
			SemaphoreSlim decodeScratchGate,
			CancellationToken cancellationToken = default)
		{
			Interlocked.Increment(ref _readFactCalls);
			return ((IPrewarmFileContentAnalyzer)inner).ReadFactWithBudgetAsync(
				path,
				maximumReadBytes,
				byteBudget,
				decodeScratchGate,
				cancellationToken);
		}
	}

	private sealed class SynchronousMetricsConcurrencyAnalyzer(int requiredConcurrency) :
		IFileContentAnalyzer,
		IDisposable
	{
		private readonly ManualResetEventSlim _release = new(initialState: false);
		private int _activeReads;
		private int _peakConcurrentReads;
		private int _remainingBeforeRelease = requiredConcurrency;

		public int PeakConcurrentReads => Volatile.Read(ref _peakConcurrentReads);

		public FileContentClassification? ClassifyWithoutReading(string path) => null;

		public ValueTask<bool> IsTextFileAsync(
			string path,
			CancellationToken cancellationToken = default) =>
			throw new NotSupportedException();

		public ValueTask<TextFileMetrics?> GetTextFileMetricsAsync(
			string path,
			CancellationToken cancellationToken = default) =>
			throw new NotSupportedException();

		public ValueTask<FileContentMetricsResult> GetClassifiedMetricsAsync(
			string path,
			CancellationToken cancellationToken = default)
		{
			var activeReads = Interlocked.Increment(ref _activeReads);
			RecordPeak(activeReads);
			try
			{
				if (Interlocked.Decrement(ref _remainingBeforeRelease) == 0)
					_release.Set();
				if (!_release.Wait(TimeSpan.FromSeconds(5), cancellationToken))
					throw new TimeoutException("Synchronous metric reads were not scheduled concurrently.");
				return ValueTask.FromResult(new FileContentMetricsResult(
					FileContentClassification.Text,
					new TextFileMetrics(16, 1, 16, false, false)));
			}
			finally
			{
				Interlocked.Decrement(ref _activeReads);
			}
		}

		public ValueTask<TextFileContent?> TryReadAsTextAsync(
			string path,
			CancellationToken cancellationToken = default) =>
			throw new NotSupportedException();

		public ValueTask<TextFileContent?> TryReadAsTextAsync(
			string path,
			long maxSizeForFullRead,
			CancellationToken cancellationToken = default) =>
			throw new NotSupportedException();

		public void Dispose() => _release.Dispose();

		private void RecordPeak(int value)
		{
			var current = Volatile.Read(ref _peakConcurrentReads);
			while (value > current)
			{
				var observed = Interlocked.CompareExchange(ref _peakConcurrentReads, value, current);
				if (observed == current)
					return;
				current = observed;
			}
		}
	}

	private sealed class CancelingMetricsFileContentAnalyzer(
		CancellationTokenSource cancellation) : IFileContentAnalyzer
	{
		private int _classifiedMetricsCalls;

		public int ClassifiedMetricsCalls => Volatile.Read(ref _classifiedMetricsCalls);

		public FileContentClassification? ClassifyWithoutReading(string path) => null;

		public ValueTask<FileContentMetricsResult> GetClassifiedMetricsAsync(
			string path,
			CancellationToken cancellationToken = default)
		{
			Interlocked.Increment(ref _classifiedMetricsCalls);
			cancellation.Cancel();
			return ValueTask.FromResult(new FileContentMetricsResult(
				FileContentClassification.Text,
				new TextFileMetrics(
					SizeBytes: 21,
					LineCount: 1,
					CharCount: 21,
					IsEmpty: false,
					IsWhitespaceOnly: false)));
		}

		public ValueTask<bool> IsTextFileAsync(
			string path,
			CancellationToken cancellationToken = default) =>
			throw new NotSupportedException();

		public ValueTask<TextFileMetrics?> GetTextFileMetricsAsync(
			string path,
			CancellationToken cancellationToken = default) =>
			throw new NotSupportedException();

		public ValueTask<TextFileContent?> TryReadAsTextAsync(
			string path,
			CancellationToken cancellationToken = default) =>
			throw new NotSupportedException();

		public ValueTask<TextFileContent?> TryReadAsTextAsync(
			string path,
			long maxSizeForFullRead,
			CancellationToken cancellationToken = default) =>
			throw new NotSupportedException();
	}

	private sealed class CoordinatedFailureFileContentAnalyzer(
		Task analysisStarted,
		string primaryMessage) : IFileContentAnalyzer, IPrewarmFileContentAnalyzer
	{
		public TaskCompletionSource FailureObserved { get; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);

		public FileContentClassification? ClassifyWithoutReading(string path) =>
			FileContentClassification.Text;

		public ValueTask<bool> IsTextFileAsync(
			string path,
			CancellationToken cancellationToken = default) =>
			ValueTask.FromResult(true);

		public ValueTask<TextFileMetrics?> GetTextFileMetricsAsync(
			string path,
			CancellationToken cancellationToken = default) =>
			throw new NotSupportedException();

		public ValueTask<TextFileContent?> TryReadAsTextAsync(
			string path,
			CancellationToken cancellationToken = default) =>
			throw new NotSupportedException();

		public ValueTask<TextFileContent?> TryReadAsTextAsync(
			string path,
			long maxSizeForFullRead,
			CancellationToken cancellationToken = default) =>
			throw new NotSupportedException();

		public async ValueTask<ContentReadFact> ReadFactAsync(
			string path,
			long maxSizeForFullRead,
			CancellationToken cancellationToken = default)
		{
			if (path.EndsWith("failure.cs", StringComparison.Ordinal))
			{
				await analysisStarted.WaitAsync(cancellationToken);
				FailureObserved.TrySetResult();
				throw new InvalidOperationException(primaryMessage);
			}

			const string content = "same-content";
			return new ContentReadFact(
				content,
				FileContentClassification.Text,
				new TextFileMetrics(content.Length, 1, content.Length, false, false),
				ContentFingerprint.Compute(content));
		}

		public ValueTask<BudgetedContentReadResult> ReadFactWithBudgetAsync(
			string path,
			long maximumReadBytes,
			WeightedByteBudget byteBudget,
			SemaphoreSlim decodeScratchGate,
			CancellationToken cancellationToken = default) =>
			ReadTestFactWithBudgetAsync(
				() => ReadFactAsync(path, maximumReadBytes, cancellationToken),
				128 + "same-content".Length * sizeof(char),
				byteBudget,
				decodeScratchGate,
				cancellationToken);

		public ValueTask<IdentifiedFileContentMetricsResult> GetClassifiedMetricsWithIdentityAsync(
			string path,
			CancellationToken cancellationToken = default) =>
			throw new NotSupportedException();
	}

	private sealed class DisposalTrackingBlockingCompressor : ICodeCompressor, IDisposable
	{
		private readonly ManualResetEventSlim _release = new(false);
		private int _activeAnalyses;
		private int _disposedWhileAnalyzing;

		public string TransformIdentity => "disposal-tracking:v1";
		public TaskCompletionSource Started { get; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		public bool DisposedWhileAnalyzing => Volatile.Read(ref _disposedWhileAnalyzing) != 0;
		public bool IsSupported(string relativePath) => true;
		public ICodeCompressionScope CreateScope(string projectRoot) => new Scope(this);
		public void Release() => _release.Set();
		public void Dispose() => _release.Dispose();

		private sealed class Scope(DisposalTrackingBlockingCompressor owner) : ICodeCompressionScope
		{
			public CodeCompressionAnalysis Analyze(
				string fullPath,
				string relativePath,
				string content,
				CancellationToken cancellationToken)
			{
				Interlocked.Increment(ref owner._activeAnalyses);
				owner.Started.TrySetResult();
				try
				{
					owner._release.Wait(cancellationToken);
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
					Interlocked.Decrement(ref owner._activeAnalyses);
				}
			}

			public void Dispose()
			{
				if (Volatile.Read(ref owner._activeAnalyses) != 0)
					Interlocked.Exchange(ref owner._disposedWhileAnalyzing, 1);
			}
		}
	}

	private sealed class PipelineFillFileContentAnalyzer(int requiredReads) :
		IFileContentAnalyzer,
		IPrewarmFileContentAnalyzer
	{
		private int _readFactCalls;

		public TaskCompletionSource RequiredReadsReached { get; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		public int ReadFactCalls => Volatile.Read(ref _readFactCalls);

		public FileContentClassification? ClassifyWithoutReading(string path) =>
			FileContentClassification.Text;

		public ValueTask<bool> IsTextFileAsync(
			string path,
			CancellationToken cancellationToken = default) =>
			ValueTask.FromResult(true);

		public ValueTask<TextFileMetrics?> GetTextFileMetricsAsync(
			string path,
			CancellationToken cancellationToken = default) =>
			throw new NotSupportedException();

		public ValueTask<TextFileContent?> TryReadAsTextAsync(
			string path,
			CancellationToken cancellationToken = default) =>
			throw new NotSupportedException();

		public ValueTask<TextFileContent?> TryReadAsTextAsync(
			string path,
			long maxSizeForFullRead,
			CancellationToken cancellationToken = default) =>
			throw new NotSupportedException();

		public ValueTask<ContentReadFact> ReadFactAsync(
			string path,
			long maxSizeForFullRead,
			CancellationToken cancellationToken = default)
		{
			var calls = Interlocked.Increment(ref _readFactCalls);
			if (calls >= requiredReads)
				RequiredReadsReached.TrySetResult();

			const string content = "pipeline-content";
			return ValueTask.FromResult(new ContentReadFact(
				content,
				FileContentClassification.Text,
				new TextFileMetrics(content.Length, 1, content.Length, false, false),
				ContentFingerprint.Compute(content)));
		}

		public ValueTask<BudgetedContentReadResult> ReadFactWithBudgetAsync(
			string path,
			long maximumReadBytes,
			WeightedByteBudget byteBudget,
			SemaphoreSlim decodeScratchGate,
			CancellationToken cancellationToken = default) =>
			ReadTestFactWithBudgetAsync(
				() => ReadFactAsync(path, maximumReadBytes, cancellationToken),
				128 + "pipeline-content".Length * sizeof(char),
				byteBudget,
				decodeScratchGate,
				cancellationToken);

		public ValueTask<IdentifiedFileContentMetricsResult> GetClassifiedMetricsWithIdentityAsync(
			string path,
			CancellationToken cancellationToken = default) =>
			throw new NotSupportedException();
	}

	private sealed class PipelineFillFailureCompressor(
		Task requiredReadsReached,
		string primaryMessage) : ICodeCompressor, IDisposable
	{
		private readonly Task _requiredReadsReached = requiredReadsReached;
		private readonly string _primaryMessage = primaryMessage;
		private int _activeAnalyses;

		public string TransformIdentity => "pipeline-fill-failure:v1";
		public int ActiveAnalyses => Volatile.Read(ref _activeAnalyses);
		public bool IsSupported(string relativePath) => true;
		public ICodeCompressionScope CreateScope(string projectRoot) => new Scope(this);
		public void Dispose()
		{
		}

		private sealed class Scope(PipelineFillFailureCompressor owner) : ICodeCompressionScope
		{
			public CodeCompressionAnalysis Analyze(
				string fullPath,
				string relativePath,
				string content,
				CancellationToken cancellationToken)
			{
				Interlocked.Increment(ref owner._activeAnalyses);
				try
				{
					owner._requiredReadsReached.Wait(cancellationToken);
					throw new InvalidOperationException(owner._primaryMessage);
				}
				finally
				{
					Interlocked.Decrement(ref owner._activeAnalyses);
				}
			}

			public void Dispose()
			{
			}
		}
	}

	private sealed class ConstantFileContentAnalyzer : IFileContentAnalyzer
	{
		private static readonly TextFileContent Content = new(
			"content",
			SizeBytes: 7,
			LineCount: 1,
			CharCount: 7,
			IsEmpty: false,
			IsWhitespaceOnly: false);

		public ValueTask<bool> IsTextFileAsync(
			string path,
			CancellationToken cancellationToken = default) =>
			ValueTask.FromResult(true);

		public ValueTask<TextFileMetrics?> GetTextFileMetricsAsync(
			string path,
			CancellationToken cancellationToken = default) =>
			throw new NotSupportedException();

		public ValueTask<TextFileContent?> TryReadAsTextAsync(
			string path,
			CancellationToken cancellationToken = default) =>
			ValueTask.FromResult<TextFileContent?>(Content);

		public ValueTask<TextFileContent?> TryReadAsTextAsync(
			string path,
			long maxSizeForFullRead,
			CancellationToken cancellationToken = default) =>
			ValueTask.FromResult<TextFileContent?>(Content);
	}
}
