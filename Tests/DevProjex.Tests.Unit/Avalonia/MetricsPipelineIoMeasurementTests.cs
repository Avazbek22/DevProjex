using System.Collections.Concurrent;
using System.Diagnostics;
using DevProjex.Avalonia.Services;

namespace DevProjex.Tests.Unit.Avalonia;

[Collection("AvaloniaUI")]
public sealed class MetricsPipelineIoMeasurementTests(ITestOutputHelper output)
{
	[AvaloniaFact]
	public async Task ConcurrentMergeDuringFreshnessProbe_PreservesLatestFileMetrics()
	{
		var rootPath = Path.Combine(Path.GetTempPath(), "DevProjex-Metrics-Concurrent");
		var filePath = Path.Combine(rootPath, "Changed.cs");
		var root = new TreeNodeDescriptor(
			"root",
			rootPath,
			true,
			false,
			"folder",
			[new TreeNodeDescriptor("Changed.cs", filePath, false, false, "csharp", [])]);
		var currentTree = new BuildTreeResult(root, false, false, [filePath]);
		var viewModel = CreateViewModel();
		viewModel.IsProjectLoaded = true;
		viewModel.TreeNodes.Add(new TreeNodeViewModel(root, parent: null, icon: null));
		var versions = new ControlledMetricsFileSourceVersionProvider([filePath]);
		var analyzer = new SyntheticMetricsAnalyzer(versions);
		var completedRecalculations = 0;
		using var pipeline = new MetricsPipeline(
			viewModel,
			CreateLocalization(),
			analyzer,
			new TreeExportService(),
			new StatusOperationCoordinator(
				viewModel,
				isBackgroundMetricsActive: () => false,
				metricsOperationTextProvider: () => viewModel.StatusOperationCalculatingData),
			currentTreeProvider: () => currentTree,
			currentPathProvider: () => rootPath,
			selectedPathsProvider: () => new HashSet<string>([rootPath], PathComparer.Default),
			treeFormatProvider: () => TreeTextFormat.Ascii,
			exportPathPresentationProvider: () => null,
			boundsWidthProvider: () => 1400,
			scheduleMemoryCleanup: _ => Interlocked.Increment(ref completedRecalculations),
			fileSourceVersionProvider: versions);

		await pipeline.InitializeFileMetricsCacheSoonAfterFirstPaintAsync(
			currentTree,
			TestContext.Current.CancellationToken);
		await WaitUntilAsync(
			() => pipeline.HasCompleteBaseline && pipeline.HasStatusMetricsSnapshot,
			TimeSpan.FromSeconds(5));
		var publishedContentCharsBefore = ReadPublishedContentChars(pipeline);

		versions.Change(filePath);
		versions.ArmOneSlowOpen();
		var staleSweep = Task.Run(
			() => InvokeMissingPathSweep(pipeline, [filePath]),
			TestContext.Current.CancellationToken);
		await versions.WaitForSlowOpenAsync(TestContext.Current.CancellationToken);

		pipeline.Recalculate(MemoryCleanupReason.FilterApplied);
		var latestPublication = WaitUntilAsync(
			() => Volatile.Read(ref completedRecalculations) == 1,
			TimeSpan.FromSeconds(5));
		var publishedBeforeRelease = ReferenceEquals(
			await Task.WhenAny(
				latestPublication,
				Task.Delay(100, TestContext.Current.CancellationToken)),
			latestPublication);
		versions.ReleaseSlowOpen();
		await latestPublication;
		var missingAfterConcurrentMerge = await staleSweep;

		Assert.True(publishedBeforeRelease);
		Assert.Empty(missingAfterConcurrentMerge);
		Assert.Equal(2, analyzer.MetricsCallCount);
		Assert.Equal(1, ReadPublishedContentChars(pipeline) - publishedContentCharsBefore);
	}

	[AvaloniaTheory(Timeout = 60_000)]
	[InlineData(100, false)]
	[InlineData(100, true)]
	[InlineData(20_000, false)]
	[InlineData(20_000, true)]
	public async Task FilterRefresh_MeasuresVersionIoLockWaitAndPublicationLatency(
		int fileCount,
		bool changeOneFile)
	{
		var measurement = await MeasureFilterRefreshAsync(fileCount, changeOneFile);

		output.WriteLine(
			"files={0}; changed={1}; opens={2}; content-rereads={3}; lock-wait-ms={4:F3}; publication-ms={5:F3}",
			fileCount,
			changeOneFile ? 1 : 0,
			measurement.FileVersionOpens,
			measurement.RepeatedContentReads,
			measurement.MetricsLockWait.TotalMilliseconds,
			measurement.PublicationLatency.TotalMilliseconds);
		Assert.True(measurement.FileVersionOpens >= fileCount);
		Assert.Equal(changeOneFile ? 1 : 0, measurement.RepeatedContentReads);
		Assert.Equal(changeOneFile ? 1 : 0, measurement.PublishedContentCharsDelta);
		Assert.True(measurement.MetricsLockWait >= TimeSpan.Zero);
		Assert.True(measurement.PublicationLatency > TimeSpan.Zero);
		Assert.True(
			measurement.PublishedBeforeSlowOpenReleased,
			"A newer filter refresh was blocked by an older freshness probe.");
	}

	private static async Task<FilterRefreshMeasurement> MeasureFilterRefreshAsync(
		int fileCount,
		bool changeOneFile)
	{
		var rootPath = Path.Combine(Path.GetTempPath(), "DevProjex-Metrics-Synthetic");
		var filePaths = Enumerable.Range(0, fileCount)
			.Select(index => Path.Combine(rootPath, $"File-{index:D5}.cs"))
			.ToArray();
		var root = new TreeNodeDescriptor(
			"root",
			rootPath,
			true,
			false,
			"folder",
			filePaths.Select(path => new TreeNodeDescriptor(
				Path.GetFileName(path),
				path,
				false,
				false,
				"csharp",
				[])).ToArray());
		var currentTree = new BuildTreeResult(root, false, false, filePaths);
		var viewModel = CreateViewModel();
		viewModel.IsProjectLoaded = true;
		viewModel.TreeNodes.Add(new TreeNodeViewModel(root, parent: null, icon: null));
		var versions = new ControlledMetricsFileSourceVersionProvider(filePaths);
		var analyzer = new SyntheticMetricsAnalyzer(versions);
		var io = new MetricsPipelineIoTestPoint();
		var completedRecalculations = 0;
		using var pipeline = new MetricsPipeline(
			viewModel,
			CreateLocalization(),
			analyzer,
			new TreeExportService(),
			new StatusOperationCoordinator(
				viewModel,
				isBackgroundMetricsActive: () => false,
				metricsOperationTextProvider: () => viewModel.StatusOperationCalculatingData),
			currentTreeProvider: () => currentTree,
			currentPathProvider: () => rootPath,
			selectedPathsProvider: () => new HashSet<string>([rootPath], PathComparer.Default),
			treeFormatProvider: () => TreeTextFormat.Ascii,
			exportPathPresentationProvider: () => null,
			boundsWidthProvider: () => 1400,
			scheduleMemoryCleanup: _ => Interlocked.Increment(ref completedRecalculations),
			fileSourceVersionProvider: versions,
			ioTestPoint: io);

		await pipeline.InitializeFileMetricsCacheSoonAfterFirstPaintAsync(
			currentTree,
			TestContext.Current.CancellationToken);
		await WaitUntilAsync(
			() => pipeline.HasCompleteBaseline && pipeline.HasStatusMetricsSnapshot,
			TimeSpan.FromSeconds(10));
		var before = io.Snapshot;
		var contentReadsBefore = analyzer.MetricsCallCount;
		var publishedContentCharsBefore = ReadPublishedContentChars(pipeline);
		if (changeOneFile)
			versions.Change(filePaths[0]);

		versions.ArmOneSlowOpen();
		var stopwatch = Stopwatch.StartNew();
		var publishedBeforeSlowOpenReleased = false;
		try
		{
			pipeline.InvalidateSelectionProjection();
			pipeline.Recalculate(MemoryCleanupReason.FilterApplied);
			await versions.WaitForSlowOpenAsync(TestContext.Current.CancellationToken);

			pipeline.InvalidateSelectionProjection();
			pipeline.Recalculate(MemoryCleanupReason.FilterApplied);
			await WaitUntilAsync(
				() => io.Snapshot.MetricsLockAttemptCount >= before.MetricsLockAttemptCount + 2,
				TimeSpan.FromSeconds(5));

			var publication = WaitUntilAsync(
				() => Volatile.Read(ref completedRecalculations) >= 1,
				TimeSpan.FromSeconds(10));
			var controlledDelay = Task.Delay(
				TimeSpan.FromMilliseconds(100),
				TestContext.Current.CancellationToken);
			publishedBeforeSlowOpenReleased =
				ReferenceEquals(await Task.WhenAny(publication, controlledDelay), publication);
			versions.ReleaseSlowOpen();
			await publication;
		}
		finally
		{
			versions.ReleaseSlowOpen();
		}
		stopwatch.Stop();

		var after = io.Snapshot;
		return new FilterRefreshMeasurement(
			after.FileVersionOpenCount - before.FileVersionOpenCount,
			analyzer.MetricsCallCount - contentReadsBefore,
			after.MetricsLockWait - before.MetricsLockWait,
			stopwatch.Elapsed,
			publishedBeforeSlowOpenReleased,
			ReadPublishedContentChars(pipeline) - publishedContentCharsBefore);
	}

	private static long ReadPublishedContentChars(MetricsPipeline pipeline) =>
		(long)(typeof(MetricsPipeline).GetField(
			"_lastStatusContentChars",
			BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(pipeline) ?? -1L);

	private static IReadOnlyList<string> InvokeMissingPathSweep(
		MetricsPipeline pipeline,
		IReadOnlyList<string> paths)
	{
		var method = typeof(MetricsPipeline).GetMethod(
			"CollectMissingMetricsFilePaths",
			BindingFlags.Instance | BindingFlags.NonPublic)
			?? throw new MissingMethodException(nameof(MetricsPipeline), "CollectMissingMetricsFilePaths");
		return (IReadOnlyList<string>)(method.Invoke(
			pipeline,
			[paths, CancellationToken.None])
			?? throw new InvalidOperationException("The freshness sweep returned no result."));
	}

	private static MainWindowViewModel CreateViewModel() =>
		new(CreateLocalization(), new HelpContentProvider());

	private static LocalizationService CreateLocalization()
	{
		IReadOnlyDictionary<string, string> english = new Dictionary<string, string>
		{
			["Status.Operation.CalculatingData"] = "Calculating data",
			["Status.Metric.Lines"] = "{0} lines",
			["Status.Metric.Chars"] = "{0} chars",
			["Status.Metric.Tokens"] = "{0} tokens"
		};
		return new LocalizationService(
			new StubLocalizationCatalog(
				new Dictionary<AppLanguage, IReadOnlyDictionary<string, string>>
				{
					[AppLanguage.En] = english
				}),
			AppLanguage.En);
	}

	private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
	{
		var stopwatch = Stopwatch.StartNew();
		while (!condition())
		{
			if (stopwatch.Elapsed >= timeout)
				throw new TimeoutException("Metrics pipeline did not reach the measured state in time.");
			await Task.Delay(10, TestContext.Current.CancellationToken);
		}
	}

	private readonly record struct FilterRefreshMeasurement(
		long FileVersionOpens,
		int RepeatedContentReads,
		TimeSpan MetricsLockWait,
		TimeSpan PublicationLatency,
		bool PublishedBeforeSlowOpenReleased,
		long PublishedContentCharsDelta);

	private sealed class ControlledMetricsFileSourceVersionProvider : IMetricsFileSourceVersionProvider
	{
		private readonly ConcurrentDictionary<string, MetricsFileSourceVersion> _versions;
		private TaskCompletionSource _slowOpenEntered = NewSignal();
		private TaskCompletionSource _slowOpenRelease = NewSignal();
		private int _slowOpenArmed;

		public ControlledMetricsFileSourceVersionProvider(IEnumerable<string> paths)
		{
			_versions = new ConcurrentDictionary<string, MetricsFileSourceVersion>(
				paths.Select((path, index) => new KeyValuePair<string, MetricsFileSourceVersion>(
					path,
					new MetricsFileSourceVersion(Length: 1, index + 1, IsMissing: false))),
				PathComparer.Default);
		}

		public MetricsFileSourceVersion? Capture(string path)
		{
			if (Interlocked.Exchange(ref _slowOpenArmed, 0) == 1)
			{
				_slowOpenEntered.TrySetResult();
				_slowOpenRelease.Task
					.WaitAsync(TimeSpan.FromSeconds(5))
					.GetAwaiter()
					.GetResult();
			}
			return _versions[path];
		}

		public void Change(string path) =>
			_versions.AddOrUpdate(
				path,
				static _ => throw new InvalidOperationException("The synthetic path was not initialized."),
				static (_, current) => current with
				{
					Length = current.Length + 1,
					LastWriteTimeUtcTicks = current.LastWriteTimeUtcTicks + 1
				});

		public long ReadLength(string path) => _versions[path].Length;

		public void ArmOneSlowOpen()
		{
			_slowOpenEntered = NewSignal();
			_slowOpenRelease = NewSignal();
			Volatile.Write(ref _slowOpenArmed, 1);
		}

		public Task WaitForSlowOpenAsync(CancellationToken cancellationToken) =>
			_slowOpenEntered.Task.WaitAsync(cancellationToken);

		public void ReleaseSlowOpen() => _slowOpenRelease.TrySetResult();

		private static TaskCompletionSource NewSignal() =>
			new(TaskCreationOptions.RunContinuationsAsynchronously);
	}

	private sealed class SyntheticMetricsAnalyzer(
		ControlledMetricsFileSourceVersionProvider versions) : IFileContentAnalyzer
	{
		private int _metricsCallCount;

		public int MetricsCallCount => Volatile.Read(ref _metricsCallCount);

		public ValueTask<bool> IsTextFileAsync(
			string path,
			CancellationToken cancellationToken = default) =>
			ValueTask.FromResult(true);

		public ValueTask<TextFileMetrics?> GetTextFileMetricsAsync(
			string path,
			CancellationToken cancellationToken = default)
		{
			Interlocked.Increment(ref _metricsCallCount);
			var length = versions.ReadLength(path);
			return ValueTask.FromResult<TextFileMetrics?>(new TextFileMetrics(
				SizeBytes: length,
				LineCount: 1,
				CharCount: checked((int)length),
				IsEmpty: false,
				IsWhitespaceOnly: false));
		}

		public ValueTask<TextFileContent?> TryReadAsTextAsync(
			string path,
			CancellationToken cancellationToken = default) =>
			ValueTask.FromResult<TextFileContent?>(null);

		public ValueTask<TextFileContent?> TryReadAsTextAsync(
			string path,
			long maxSizeForFullRead,
			CancellationToken cancellationToken = default) =>
			ValueTask.FromResult<TextFileContent?>(null);
	}
}
