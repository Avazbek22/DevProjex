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

	private sealed class RecordingCompressor(int delayMilliseconds = 0) : ICodeCompressor, IDisposable
	{
		private readonly int _delayMilliseconds = delayMilliseconds;
		private int _analysisCount;
		private int _active;
		private int _maximumConcurrency;

		public string TransformIdentity => "recording:v1";
		public int AnalysisCount => Volatile.Read(ref _analysisCount);
		public int MaximumConcurrency => Volatile.Read(ref _maximumConcurrency);
		public bool IsSupported(string relativePath) => true;
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
}
