using System.Collections.Concurrent;
using DevProjex.Application.Compression;

namespace DevProjex.Tests.Unit;

public sealed class CodeCompressionSessionTests
{
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

		var warmup = Task.Run(() =>
			warmScope.Warm("sample.cs", "sample.cs", "same-content", cancellationToken),
			cancellationToken);
		await compressor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
		var output = Task.Run(() =>
			outputScope.Transform("sample.cs", "sample.cs", "same-content", cancellationToken),
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
	public async Task PrewarmDuringTransform_SharesTheInFlightAnalysis()
	{
		using var compressor = new BlockingCompressor();
		using var session = new CodeCompressionSession(compressor);
		using var outputScope = session.BeginOutput("project", ["sample.cs"]);
		using var warmScope = session.BeginOutput("project", ["sample.cs"]);
		var cancellationToken = TestContext.Current.CancellationToken;

		var output = Task.Run(() =>
			outputScope.Transform("sample.cs", "sample.cs", "same-content", cancellationToken),
			cancellationToken);
		await compressor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
		var warmup = Task.Run(() =>
			warmScope.Warm("sample.cs", "sample.cs", "same-content", cancellationToken),
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

		var output = Task.Run(
			() => outputScope.Transform(
				"sample.cs",
				"sample.cs",
				"same-content",
				outputCancellation.Token),
			cancellationToken);
		await compressor.FirstAnalysisStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
		var warmup = Task.Run(
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
