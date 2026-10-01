using System.Diagnostics;
using DevProjex.Application.Compression;
using DevProjex.Application.Diagnostics;
using DevProjex.Avalonia.Services;
using DevProjex.Infrastructure.Compression;
using DevProjex.Infrastructure.FileSystem;

namespace DevProjex.Tests.Unit.Avalonia;

[Collection("AvaloniaUI")]
public sealed class GuiTransformedLoadMeasurementTests(ITestOutputHelper output)
{
	[AvaloniaFact(Timeout = 180_000)]
	[Trait("Category", "LocalPerformance")]
	public Task MeasureCompressionPrewarmAndRetainedMetricsOnActualProject() => MeasureAsync(partialSelection: false);

	[AvaloniaFact(Timeout = 180_000)]
	[Trait("Category", "LocalPerformance")]
	public Task MeasurePartialSelectionRetainedAndDiscardedFactsOnActualProject() => MeasureAsync(partialSelection: true);

	private async Task MeasureAsync(bool partialSelection)
	{
		var requestedRoot = Environment.GetEnvironmentVariable("DEVPROJEX_GUI_BENCHMARK_ROOT");
		Assert.SkipWhen(string.IsNullOrWhiteSpace(requestedRoot),
			"Set DEVPROJEX_GUI_BENCHMARK_ROOT for read-only profiling.");
		var projectRoot = Path.GetFullPath(requestedRoot!);
		Assert.True(Directory.Exists(projectRoot));
		var cancellationToken = TestContext.Current.CancellationToken;
		var localization = new LocalizationService(new JsonLocalizationCatalog(), AppLanguage.En);
		var ignoreRules = ProjectLoadWorkflowRuntime.CreateIgnoreRulesService();
		var scanOptions = new ScanOptionsUseCase(new FileSystemScanner());
		var buildTree = new BuildTreeUseCase(new TreeBuilder(),
			new TreeNodePresentationService(localization, new IconMapper()));
		using var compression = CodeCompressionFactory.CreateSession();
		var transformation = ContentTransformationContext.For(
			new CodeCompressionContext(projectRoot, compression, CodeTransformKinds.Bodies), redaction: null);
		PublishedMetrics? expectedMetrics = null;
		int? expectedFileCount = null;

		// A fresh metrics pipeline models reload while the window-lifetime compression cache survives.
		// The first pass is session-cold, not filesystem-cold; no extra corpus warm-up is performed.
		// Partial selection alternates three warm control pairs after one cold priming pass.
		bool[] discardReadFacts = partialSelection
			? [false, true, false, false, true, true, false]
			: [false, false, false, false];
		for (var run = 0; run < discardReadFacts.Length; run++)
		{
			var viewModel = new MainWindowViewModel(localization, new HelpContentProvider())
			{
				IsProjectLoaded = true
			};
			using var coordinator = new SelectionSyncCoordinator(
				viewModel, scanOptions, new FilterOptionSelectionService(), new IgnoreOptionsService(localization),
				(path, selected, roots) => ignoreRules.Build(path, selected, roots),
				(path, roots) => ignoreRules.GetIgnoreOptionsAvailability(path, roots) with { ShowAdvancedCounts = true },
				_ => false, () => projectRoot,
				buildIgnoreRulesWithCancellation: ignoreRules.BuildWithCancellation,
				getIgnoreOptionsAvailabilityWithCancellation: (path, roots, token) =>
					ignoreRules.GetIgnoreOptionsAvailabilityWithCancellation(path, roots, token) with { ShowAdvancedCounts = true },
				gitScopePathProvider: new GitScopePathProvider());
			coordinator.ResetProjectProfileSelections(projectRoot);
			ignoreRules.RefreshDiscoveryCaches(projectRoot);
			coordinator.InvalidateFileSystemCaches();
			using var ignoreMeasurement = IgnorePipelineDiagnostics.BeginMeasurement();
			var totalAllocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
			var totalStarted = Stopwatch.GetTimestamp();
			var snapshot = await coordinator.BuildProjectSelectionSnapshotAsync(projectRoot, cancellationToken);
			var selectionMilliseconds = Stopwatch.GetElapsedTime(totalStarted).TotalMilliseconds;
			Assert.NotNull(snapshot);
			Assert.NotNull(snapshot.TreeInventory);
			Assert.False(snapshot.HadScanFailure);
			var projectionStarted = Stopwatch.GetTimestamp();
			var selectedRoots = snapshot.RootOptions!
				.Where(static option => option.IsChecked)
				.Select(static option => option.Name)
				.ToHashSet(ProjectTreePathIdentity.CanonicalComparer);
			var options = new TreeFilterOptions(
				snapshot.EffectiveExtensionOptions.Where(static option => option.IsChecked)
					.Select(static option => option.Name).ToHashSet(StringComparer.OrdinalIgnoreCase),
				selectedRoots,
				ProjectLoadIgnoreRulesResolver.Resolve(snapshot, selected =>
					ignoreRules.BuildWithCancellation(projectRoot, selected, selectedRoots, cancellationToken)));
			var projected = await Task.Run(() => buildTree.ExecuteWithInventory(
				new BuildTreeRequest(projectRoot, options), snapshot.TreeInventory, cancellationToken), cancellationToken);
			var projectionMilliseconds = Stopwatch.GetElapsedTime(projectionStarted).TotalMilliseconds;
			Assert.Same(snapshot.TreeInventory, projected.Inventory);
			Assert.True(coordinator.ApplyProjectSelectionSnapshot(projectRoot, snapshot));
			var tree = projected.Tree;
			var fileCount = tree.OrderedFilePaths!.Count;
			expectedFileCount ??= fileCount;
			Assert.Equal(expectedFileCount.Value, fileCount);
			viewModel.TreeNodes.Add(new TreeNodeViewModel(tree.Root, parent: null, icon: null));
			var selectedPaths = partialSelection
				? tree.OrderedFilePaths.Where(static (_, index) => index % 2 == 0)
					.ToHashSet(ProjectTreePathIdentity.CanonicalComparer)
				: new HashSet<string>(ProjectTreePathIdentity.CanonicalComparer);
			var selectedContentPaths = partialSelection
				? selectedPaths
				: tree.OrderedFilePaths.ToHashSet(ProjectTreePathIdentity.CanonicalComparer);
			var supportedPaths = tree.OrderedFilePaths
				.Where(path => compression.IsSupported(Path.GetRelativePath(projectRoot, path), CodeTransformKinds.Bodies))
				.ToHashSet(ProjectTreePathIdentity.CanonicalComparer);
			var contentOpens = new ContentOpenCounters(selectedContentPaths, supportedPaths);
			var analyzer = new FileContentAnalyzer((path, bufferSize, fileShare, asynchronous) =>
			{
				contentOpens.Record(path);
				return UnixFileTypeInspector.OpenRegularFileForSequentialRead(path, bufferSize, fileShare, asynchronous);
			});
			var metricsIo = new MetricsPipelineIoTestPoint();
			using var status = new StatusOperationCoordinator(viewModel, () => false,
				() => viewModel.StatusOperationCalculatingData);
			using var background = new BackgroundTaskRegistry();
			using var pipeline = new MetricsPipeline(viewModel, localization, analyzer, new TreeExportService(), status,
				() => tree, () => projectRoot, () => selectedPaths, () => TreeTextFormat.Ascii,
				() => null, () => 1400, transformationContextProvider: () => transformation,
				backgroundTasks: background, ioTestPoint: metricsIo);
			var compressionBefore = compression.Diagnostics;
			ContentPipelineDiagnosticSnapshot prewarmContent;
			double prewarmMilliseconds;
			long prewarmAllocatedBytes;
			using (var measurement = ContentPipelineDiagnostics.BeginMeasurement())
			{
				var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
				var started = Stopwatch.GetTimestamp();
				await pipeline.PrewarmCompressionAsync(tree, cancellationToken, retainReadFactsForNextMetricsPass: true);
				prewarmMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
				prewarmAllocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
				prewarmContent = measurement.Capture();
			}
			cancellationToken.ThrowIfCancellationRequested();
			var compressionAfterPrewarm = compression.Diagnostics;
			var compressionSnapshot = compression.Snapshot;
			Assert.False(compressionSnapshot.Availability.IsUnavailable);
			Assert.True(compressionSnapshot.BodyTransformedFiles > 0, "The workload must exercise native body compression.");
			var retainedBytesBeforeMetrics = pipeline.RetainedReadFactBytes;
			var retainedCountBeforeMetrics = ReadRetainedFacts(pipeline)?.Count ?? 0;
			Assert.True(retainedCountBeforeMetrics > 0);
			var prewarmContentOpens = contentOpens.Capture();
			contentOpens = new ContentOpenCounters(selectedContentPaths, supportedPaths);
			if (discardReadFacts[run])
				pipeline.ReleasePostLoadReadFacts();
			ContentPipelineDiagnosticSnapshot metricsContent;
			double metricsScanMilliseconds;
			double metricsPublicationMilliseconds;
			long metricsAllocatedBytes;
			using (var measurement = ContentPipelineDiagnostics.BeginMeasurement())
			{
				var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
				var started = Stopwatch.GetTimestamp();
				await pipeline.InitializeFileMetricsCacheSoonAfterFirstPaintAsync(tree, cancellationToken);
				metricsScanMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
				while (background.TrackedTaskCount != 0)
				{
					Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(30), "Metrics publication did not complete.");
					await Task.Delay(1, cancellationToken);
				}
				metricsPublicationMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
				metricsAllocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
				metricsContent = measurement.Capture();
			}
			var totalMilliseconds = Stopwatch.GetElapsedTime(totalStarted).TotalMilliseconds;
			var totalAllocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - totalAllocatedBefore;
			Assert.True(pipeline.HasCompleteBaseline);
			Assert.True(pipeline.HasStatusMetricsSnapshot);
			Assert.Equal(0, pipeline.RetainedReadFactBytes);
			var cachedFileCount = ((System.Collections.IDictionary)typeof(MetricsPipeline)
				.GetField("_fileMetricsCache", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pipeline)!).Count;
			Assert.Equal(fileCount, cachedFileCount);
			var compressionAfterMetrics = compression.Diagnostics;
			Assert.InRange(compressionAfterMetrics.CacheEntries, 0, compressionAfterMetrics.MaximumCacheEntries);
			Assert.InRange(compressionAfterMetrics.RetainedCacheBytes, 0, compressionAfterMetrics.MaximumRetainedCacheBytes);
			Assert.Equal(0, compressionAfterMetrics.Runtime.LeasedWorkers);
			var publishedMetrics = ReadPublishedMetrics(pipeline);
			expectedMetrics ??= publishedMetrics;
			Assert.Equal(expectedMetrics, publishedMetrics);
			output.WriteLine(JsonSerializer.Serialize(new
			{
				Scenario = partialSelection
					? "gui-partial-selection-retained-vs-discarded-facts-control"
					: "gui-body-compression-prewarm-and-retained-metrics",
				Run = run,
				Phase = run == 0 ? "cold-compression-session" : "warm-compression-cache-fresh-metrics",
				DiscardReadFactsBeforeMetrics = discardReadFacts[run],
				Files = fileCount,
				SelectedFiles = selectedContentPaths.Count,
				SupportedFiles = supportedPaths.Count,
				CachedFileCount = cachedFileCount,
				InventoryEntries = snapshot.TreeInventory.Entries.Count,
				GitMode = options.IgnoreRules.GitFilteringMode.ToString(),
				SelectionMilliseconds = selectionMilliseconds,
				ProjectionMilliseconds = projectionMilliseconds,
				PrewarmMilliseconds = prewarmMilliseconds,
				PrewarmAllocatedBytes = prewarmAllocatedBytes,
				MetricsScanMilliseconds = metricsScanMilliseconds,
				MetricsPublicationMilliseconds = metricsPublicationMilliseconds,
				MetricsAllocatedBytes = metricsAllocatedBytes,
				TotalBackendMilliseconds = totalMilliseconds,
				TotalBackendAllocatedBytes = totalAllocatedBytes,
				RetainedFactsBeforeMetrics = retainedCountBeforeMetrics,
				RetainedFactBytesBeforeMetrics = retainedBytesBeforeMetrics,
				RetainedFactBytesAfterMetrics = pipeline.RetainedReadFactBytes,
				PrewarmContent = prewarmContent,
				MetricsContent = metricsContent,
				PrewarmContentOpens = prewarmContentOpens,
				MetricsContentOpens = contentOpens.Capture(),
				PrewarmCompression = CompressionDelta(compressionBefore, compressionAfterPrewarm),
				MetricsCompression = CompressionDelta(compressionAfterPrewarm, compressionAfterMetrics),
				CompressionCache = new
				{
					compressionAfterMetrics.CacheEntries,
					compressionAfterMetrics.RetainedCacheBytes,
					compressionAfterMetrics.MaximumCacheEntries,
					compressionAfterMetrics.MaximumRetainedCacheBytes,
					compressionAfterMetrics.Runtime
				},
				CompressionOutcome = new
				{
					compressionSnapshot.CompressedFiles,
					compressionSnapshot.UnchangedFiles,
					compressionSnapshot.SourceCharacters,
					compressionSnapshot.TransformedCharacters,
					compressionSnapshot.BodyTransformedFiles
				},
				MetricsIo = metricsIo.Snapshot,
				PublishedMetrics = publishedMetrics,
				Ignore = ignoreMeasurement.Capture()
			}));
		}
	}

	private static object CompressionDelta(CodeCompressionDiagnosticsSnapshot before, CodeCompressionDiagnosticsSnapshot after) => new
	{
		HashComputations = after.HashComputations - before.HashComputations,
		CacheHits = after.CacheHits - before.CacheHits,
		CacheMisses = after.CacheMisses - before.CacheMisses,
		AnalysisExecutions = after.AnalysisExecutions - before.AnalysisExecutions,
		PrewarmRequests = after.PrewarmRequests - before.PrewarmRequests,
		PrewarmCacheHits = after.PrewarmCacheHits - before.PrewarmCacheHits,
		PrewarmAnalyses = after.PrewarmAnalyses - before.PrewarmAnalyses,
		PrewarmReuses = after.PrewarmReuses - before.PrewarmReuses,
		UnsupportedFastPaths = after.UnsupportedFastPaths - before.UnsupportedFastPaths
	};

	private static ContentReadFactSnapshot? ReadRetainedFacts(MetricsPipeline pipeline) =>
		(ContentReadFactSnapshot?)typeof(MetricsPipeline)
			.GetField("_postLoadReadFacts", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pipeline);

	private static PublishedMetrics ReadPublishedMetrics(MetricsPipeline pipeline) => new(
		ReadPublishedMetric(pipeline, "_lastStatusTreeLines"),
		ReadPublishedMetric(pipeline, "_lastStatusTreeChars"),
		ReadPublishedMetric(pipeline, "_lastStatusTreeTokens"),
		ReadPublishedMetric(pipeline, "_lastStatusContentLines"),
		ReadPublishedMetric(pipeline, "_lastStatusContentChars"),
		ReadPublishedMetric(pipeline, "_lastStatusContentTokens"));

	private static long ReadPublishedMetric(MetricsPipeline pipeline, string field) =>
		(long)typeof(MetricsPipeline).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pipeline)!;

	private sealed record PublishedMetrics(long TreeLines, long TreeChars, long TreeTokens,
		long ContentLines, long ContentChars, long ContentTokens);

	// Counts content stream opens, including prefix-only classification, not metadata handles or full reads.
	private sealed class ContentOpenCounters(IReadOnlySet<string> selected, IReadOnlySet<string> supported)
	{
		private long _selectedSupported;
		private long _selectedUnsupported;
		private long _unselectedSupported;
		private long _unselectedUnsupported;

		public void Record(string path)
		{
			if (selected.Contains(path))
			{
				if (supported.Contains(path))
					Interlocked.Increment(ref _selectedSupported);
				else
					Interlocked.Increment(ref _selectedUnsupported);
			}
			else if (supported.Contains(path))
				Interlocked.Increment(ref _unselectedSupported);
			else
				Interlocked.Increment(ref _unselectedUnsupported);
		}

		public object Capture() => new
		{
			SelectedSupported = Interlocked.Read(ref _selectedSupported),
			SelectedUnsupported = Interlocked.Read(ref _selectedUnsupported),
			UnselectedSupported = Interlocked.Read(ref _unselectedSupported),
			UnselectedUnsupported = Interlocked.Read(ref _unselectedUnsupported)
		};
	}
}
