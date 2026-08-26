using System.Collections.Concurrent;

namespace DevProjex.Tests.Unit;

public sealed class ProjectContentMetricsCalculatorTests
{
	[Fact]
	public async Task FileMetricsObserverUsesTheAggregateReadPass()
	{
		using var project = new TemporaryDirectory();
		var first = project.CreateFile("first.txt", "alpha\nbeta");
		var second = project.CreateFile("second.txt", "gamma");
		var analyzer = new CountingMetricsAnalyzer(new FileContentAnalyzer());
		var observed = new List<ContentFileMetrics>();

		var metrics = await ProjectContentMetricsCalculator.CalculateAsync(
			analyzer,
			[first, second],
			observed.Add,
			progress: null,
			TestContext.Current.CancellationToken);

		Assert.Equal(2, analyzer.MetricsCalls);
		Assert.Equal([first, second], observed.Select(static item => item.Path));
		Assert.Equal(metrics, ExportOutputMetricsCalculator.FromOrderedContentFiles(observed));
	}

	[Fact]
	public async Task ConcurrentReadsPublishProgressInMonotonicOrder()
	{
		var analyzer = new CoordinatedMetricsAnalyzer();
		var progress = new DelayedFirstProgress();

		await ProjectContentMetricsCalculator.CalculateAsync(
			analyzer,
			["first", "second"],
			progress,
			TestContext.Current.CancellationToken);

		Assert.Equal([1, 2], progress.ProcessedFiles);
	}

	private sealed class CountingMetricsAnalyzer(IFileContentAnalyzer inner) : IFileContentAnalyzer
	{
		private int _metricsCalls;

		public int MetricsCalls => Volatile.Read(ref _metricsCalls);

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
			Interlocked.Increment(ref _metricsCalls);
			return inner.GetClassifiedMetricsAsync(path, cancellationToken);
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

	private sealed class CoordinatedMetricsAnalyzer : IFileContentAnalyzer
	{
		private readonly TaskCompletionSource _secondReadStarted = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		private int _readCount;

		public async ValueTask<FileContentMetricsResult> GetClassifiedMetricsAsync(
			string path,
			CancellationToken cancellationToken = default)
		{
			if (Interlocked.Increment(ref _readCount) == 1)
				await _secondReadStarted.Task.WaitAsync(cancellationToken);
			else
				_secondReadStarted.TrySetResult();

			return new FileContentMetricsResult(
				FileContentClassification.Text,
				new TextFileMetrics(1, 1, 1, false, false));
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

	private sealed class DelayedFirstProgress : IProgress<ProjectCopyExportProgress>
	{
		private readonly ConcurrentQueue<int> _processedFiles = new();
		private int _reports;

		public IReadOnlyList<int> ProcessedFiles => _processedFiles.ToArray();

		public void Report(ProjectCopyExportProgress value)
		{
			if (Interlocked.Increment(ref _reports) == 1)
				Thread.Sleep(100);
			_processedFiles.Enqueue(value.ProcessedEntryCount);
		}
	}
}
