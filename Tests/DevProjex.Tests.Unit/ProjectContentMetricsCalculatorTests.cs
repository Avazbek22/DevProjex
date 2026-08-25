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
}
