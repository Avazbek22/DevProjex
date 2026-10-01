using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using DevProjex.Avalonia.Services;

namespace DevProjex.Tests.Unit.Avalonia;

[Collection("AvaloniaUI")]
public sealed class MetricsPipelineFreshnessConcurrencyTests
{
	[AvaloniaFact]
	public async Task IndependentFreshnessProbesOverlapWithinTheExistingRecoveryBound()
	{
		using var fixture = await Fixture.CreateAsync(64);
		using var gate = new ProbeGate();
		var bound = MetricsCalculationPolicy.GetSelectionRecoveryParallelism(Environment.ProcessorCount);
		var expectedConcurrency = Math.Min(2, bound);
		fixture.Versions.OnCapture = (_, version) =>
		{
			gate.Enter(expectedConcurrency);
			return version;
		};
		var sweep = StartSweep(fixture, fixture.Paths, TestContext.Current.CancellationToken);
		try
		{
			await gate.ExpectedConcurrencyReached.Task.WaitAsync(
				TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
			Assert.InRange(gate.MaximumActive, expectedConcurrency, bound);
		}
		finally
		{
			gate.Release();
			await sweep.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
		}

		Assert.Empty(await sweep);
		Assert.Equal(fixture.Paths.Length, gate.Captures);
		Assert.InRange(gate.MaximumActive, expectedConcurrency, bound);
	}

	[AvaloniaFact]
	public async Task CancellationStopsQueuedProbesWithoutEvictingPartiallyObservedEntries()
	{
		using var fixture = await Fixture.CreateAsync(64);
		using var gate = new ProbeGate();
		using var cancellation = new CancellationTokenSource();
		fixture.Versions.Change(fixture.Paths[0]);
		fixture.Versions.OnCapture = (_, version) =>
		{
			gate.Enter(1);
			return version;
		};
		var sweep = StartSweep(fixture, fixture.Paths, cancellation.Token);
		try
		{
			await gate.ExpectedConcurrencyReached.Task.WaitAsync(
				TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
			cancellation.Cancel();
		}
		finally
		{
			cancellation.Cancel();
			gate.Release();
			try
			{
				await sweep.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
			}
			catch (OperationCanceledException)
			{
			}
		}

		var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
			await sweep.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
		Assert.Equal(cancellation.Token, exception.CancellationToken);
		Assert.InRange(gate.Captures, 1,
			MetricsCalculationPolicy.GetSelectionRecoveryParallelism(Environment.ProcessorCount));
		fixture.Versions.OnCapture = null;
		fixture.Versions.ResetCaptures();

		Assert.Equal([fixture.Paths[0]], await StartSweep(
			fixture, fixture.Paths, TestContext.Current.CancellationToken));
		Assert.Equal(fixture.Paths.Length, fixture.Versions.CapturedPaths.Count);
	}

	[AvaloniaFact]
	public async Task PreCanceledSweepDoesNotCaptureAnySourceVersion()
	{
		using var fixture = await Fixture.CreateAsync(8);
		using var cancellation = new CancellationTokenSource();
		cancellation.Cancel();
		fixture.Versions.ResetCaptures();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
			await StartSweep(fixture, fixture.Paths, cancellation.Token));

		Assert.Empty(fixture.Versions.CapturedPaths);
	}

	[AvaloniaFact]
	public async Task ChangedFilesAreEvictedSelectivelyAndMissingPathsKeepRequestedOrder()
	{
		using var fixture = await Fixture.CreateAsync(12);
		fixture.Versions.Change(fixture.Paths[1]);
		fixture.Versions.Change(fixture.Paths[4]);
		fixture.Versions.Change(fixture.Paths[10]);
		var uncached = Path.Combine(fixture.RootPath, "Uncached.cs");
		var requested = new[]
		{
			fixture.Paths[10], fixture.Paths[11], uncached, fixture.Paths[4],
			fixture.Paths[7], fixture.Paths[1], fixture.Paths[0]
		};
		var expectedMissing = new[] { fixture.Paths[10], uncached, fixture.Paths[4], fixture.Paths[1] };

		Assert.Equal(expectedMissing, await StartSweep(
			fixture, requested, TestContext.Current.CancellationToken));
		fixture.Versions.ResetCaptures();
		Assert.Equal(expectedMissing, await StartSweep(
			fixture, requested, TestContext.Current.CancellationToken));

		Assert.Equal(new[] { fixture.Paths[0], fixture.Paths[7], fixture.Paths[11] }.Order(StringComparer.Ordinal),
			fixture.Versions.CapturedPaths.Order(StringComparer.Ordinal));
		Assert.Equal(fixture.Paths.Length, fixture.Analyzer.MetricsCalls);
	}

	[AvaloniaFact]
	public async Task SlowOlderProbeDoesNotBlockNewerMergeOrEvictItsReplacement()
	{
		using var fixture = await Fixture.CreateAsync(16);
		using var gate = new ProbeGate();
		var changed = fixture.Paths[0];
		fixture.Versions.Change(changed);
		var blockNextCapture = 1;
		fixture.Versions.OnCapture = (path, version) =>
		{
			if (path == changed && Interlocked.Exchange(ref blockNextCapture, 0) == 1)
				gate.Enter(1);
			return version;
		};
		var oldSweep = StartSweep(fixture, [changed], TestContext.Current.CancellationToken);
		try
		{
			await gate.ExpectedConcurrencyReached.Task.WaitAsync(
				TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
			fixture.Pipeline.Recalculate(MemoryCleanupReason.FilterApplied);
			await WaitUntilAsync(() => fixture.CompletedRecalculations == 1);
			Assert.False(oldSweep.IsCompleted);
			Assert.Equal(fixture.Paths.Length + 1, fixture.Analyzer.MetricsCalls);
		}
		finally
		{
			gate.Release();
			await oldSweep.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
		}

		Assert.Empty(await oldSweep);
		Assert.Empty(await StartSweep(fixture, [changed], TestContext.Current.CancellationToken));
	}

	private static Task<IReadOnlyList<string>> StartSweep(
		Fixture fixture,
		IReadOnlyList<string> paths,
		CancellationToken cancellationToken) =>
		Task.Factory.StartNew(() =>
		{
			var method = typeof(MetricsPipeline).GetMethod(
				"CollectMissingMetricsFilePaths", BindingFlags.Instance | BindingFlags.NonPublic)!;
			try
			{
				return (IReadOnlyList<string>)method.Invoke(fixture.Pipeline, [paths, cancellationToken])!;
			}
			catch (TargetInvocationException exception) when (exception.InnerException is not null)
			{
				ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
				throw;
			}
		}, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

	private static async Task WaitUntilAsync(Func<bool> condition)
	{
		var timer = Stopwatch.StartNew();
		while (!condition())
		{
			if (timer.Elapsed > TimeSpan.FromSeconds(5))
				throw new TimeoutException("Metrics publication did not complete.");
			await Task.Delay(10, TestContext.Current.CancellationToken);
		}
	}

	private sealed class Fixture : IDisposable
	{
		private int _completedRecalculations;
		public string RootPath { get; } = Path.Combine(Path.GetTempPath(), "DevProjex-Metrics-Freshness");
		public string[] Paths { get; }
		public VersionProvider Versions { get; }
		public MetricsAnalyzer Analyzer { get; }
		public MetricsPipeline Pipeline { get; }
		public BuildTreeResult Tree { get; }
		public int CompletedRecalculations => Volatile.Read(ref _completedRecalculations);

		private Fixture(int count)
		{
			Paths = Enumerable.Range(0, count)
				.Select(index => Path.Combine(RootPath, $"File-{index:D3}.cs")).ToArray();
			var root = new TreeNodeDescriptor("root", RootPath, true, false, "folder",
				Paths.Select(path => new TreeNodeDescriptor(Path.GetFileName(path), path,
					false, false, "csharp", [])).ToArray());
			Tree = new BuildTreeResult(root, false, false, Paths);
			var localization = new LocalizationService(new StubLocalizationCatalog(
				new Dictionary<AppLanguage, IReadOnlyDictionary<string, string>>
				{
					[AppLanguage.En] = new Dictionary<string, string>
					{
						["Status.Operation.CalculatingData"] = "Calculating data",
						["Status.Metric.Lines"] = "{0} lines",
						["Status.Metric.Chars"] = "{0} chars",
						["Status.Metric.Tokens"] = "{0} tokens"
					}
				}), AppLanguage.En);
			var viewModel = new MainWindowViewModel(localization, new HelpContentProvider()) { IsProjectLoaded = true };
			viewModel.TreeNodes.Add(new TreeNodeViewModel(root, parent: null, icon: null));
			Versions = new VersionProvider(Paths);
			Analyzer = new MetricsAnalyzer(Versions);
			Pipeline = new MetricsPipeline(viewModel, localization, Analyzer, new TreeExportService(),
				new StatusOperationCoordinator(viewModel, () => false, () => viewModel.StatusOperationCalculatingData),
				() => Tree, () => RootPath, () => new HashSet<string>([RootPath], PathComparer.Default),
				() => TreeTextFormat.Ascii, () => null, () => 1400,
				scheduleMemoryCleanup: _ => Interlocked.Increment(ref _completedRecalculations),
				fileSourceVersionProvider: Versions);
		}

		public static async Task<Fixture> CreateAsync(int count)
		{
			var fixture = new Fixture(count);
			try
			{
				await fixture.Pipeline.InitializeFileMetricsCacheSoonAfterFirstPaintAsync(
					fixture.Tree, TestContext.Current.CancellationToken);
				await WaitUntilAsync(() => fixture.Pipeline.HasCompleteBaseline && fixture.Pipeline.HasStatusMetricsSnapshot);
				fixture.Versions.ResetCaptures();
				return fixture;
			}
			catch
			{
				fixture.Dispose();
				throw;
			}
		}

		public void Dispose() => Pipeline.Dispose();
	}

	private sealed class ProbeGate : IDisposable
	{
		private readonly ManualResetEventSlim _release = new();
		private int _active;
		private int _maximumActive;
		private int _captures;
		public TaskCompletionSource ExpectedConcurrencyReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public int MaximumActive => Volatile.Read(ref _maximumActive);
		public int Captures => Volatile.Read(ref _captures);

		public void Enter(int expectedConcurrency)
		{
			Interlocked.Increment(ref _captures);
			var active = Interlocked.Increment(ref _active);
			var previous = Volatile.Read(ref _maximumActive);
			while (active > previous)
			{
				var observed = Interlocked.CompareExchange(ref _maximumActive, active, previous);
				if (observed == previous)
					break;
				previous = observed;
			}
			if (active >= expectedConcurrency)
				ExpectedConcurrencyReached.TrySetResult();
			try
			{
				if (!_release.Wait(TimeSpan.FromSeconds(20)))
					throw new TimeoutException("The test did not release its freshness probe.");
			}
			finally
			{
				Interlocked.Decrement(ref _active);
			}
		}

		public void Release() => _release.Set();
		public void Dispose() => _release.Dispose();
	}

	private sealed class VersionProvider(IEnumerable<string> paths) : IMetricsFileSourceVersionProvider
	{
		private readonly ConcurrentDictionary<string, MetricsFileSourceVersion> _versions = new(
			paths.Select(path => new KeyValuePair<string, MetricsFileSourceVersion>(path, new(1, 1, false))),
			PathComparer.Default);
		private readonly ConcurrentQueue<string> _capturedPaths = new();
		public Func<string, MetricsFileSourceVersion, MetricsFileSourceVersion?>? OnCapture { get; set; }
		public IReadOnlyList<string> CapturedPaths => _capturedPaths.ToArray();

		public MetricsFileSourceVersion? Capture(string path)
		{
			_capturedPaths.Enqueue(path);
			var version = _versions[path];
			return OnCapture is { } callback ? callback(path, version) : version;
		}

		public void Change(string path) => _versions.AddOrUpdate(path,
			static _ => throw new InvalidOperationException("Unknown synthetic source."),
			static (_, version) => version with { Length = version.Length + 1, LastWriteTimeUtcTicks = version.LastWriteTimeUtcTicks + 1 });
		public long Length(string path) => _versions[path].Length;
		public void ResetCaptures() => _capturedPaths.Clear();
	}

	private sealed class MetricsAnalyzer(VersionProvider versions) : IFileContentAnalyzer
	{
		private int _metricsCalls;
		public int MetricsCalls => Volatile.Read(ref _metricsCalls);
		public ValueTask<bool> IsTextFileAsync(string path, CancellationToken cancellationToken = default) =>
			ValueTask.FromResult(true);
		public ValueTask<TextFileMetrics?> GetTextFileMetricsAsync(string path, CancellationToken cancellationToken = default)
		{
			Interlocked.Increment(ref _metricsCalls);
			var length = versions.Length(path);
			return ValueTask.FromResult<TextFileMetrics?>(new TextFileMetrics(length, 1, checked((int)length), false, false));
		}
		public ValueTask<TextFileContent?> TryReadAsTextAsync(string path, CancellationToken cancellationToken = default) =>
			ValueTask.FromResult<TextFileContent?>(null);
		public ValueTask<TextFileContent?> TryReadAsTextAsync(string path, long maxSizeForFullRead, CancellationToken cancellationToken = default) =>
			ValueTask.FromResult<TextFileContent?>(null);
	}
}
