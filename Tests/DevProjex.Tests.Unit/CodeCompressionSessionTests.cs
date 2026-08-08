using DevProjex.Application.Compression;

namespace DevProjex.Tests.Unit;

public sealed class CodeCompressionSessionTests
{
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
		using var compressor = new RecordingCompressor(delayMilliseconds: 40);
		using var session = new CodeCompressionSession(compressor);
		var paths = Enumerable.Range(0, 16).Select(index => $"{index}.cs").ToArray();
		using var scope = session.BeginOutput("project", paths);

		await Task.WhenAll(paths.Select((path, index) => Task.Run(() =>
			scope.Transform(path, path, $"content-{index}", CancellationToken.None))));
		_ = scope.Complete();

		Assert.True(compressor.MaximumConcurrency > 1);
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
	public async Task Prewarm_LargerThanPlanCache_LeavesOverflowForDemandProcessing()
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

		Assert.Equal(CodeCompressionSession.PlanCacheCapacity, result.CandidateFiles);
		Assert.Equal(CodeCompressionSession.PlanCacheCapacity, compressor.AnalysisCount);
	}

	private sealed class RecordingCompressor(
		int delayMilliseconds = 0,
		bool isSupported = true) : ICodeCompressor, IDisposable
	{
		private readonly int _delayMilliseconds = delayMilliseconds;
		private int _analysisCount;
		private int _active;
		private int _maximumConcurrency;

		public string TransformIdentity => "recording:v1";
		public int AnalysisCount => Volatile.Read(ref _analysisCount);
		public int MaximumConcurrency => Volatile.Read(ref _maximumConcurrency);
		public bool IsSupported(string relativePath) => isSupported;
		public ICodeCompressionScope CreateScope(string projectRoot) => new Scope(this);
		public void Dispose() { }

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
