using System.Diagnostics;
using DevProjex.Application.Diagnostics;
using DevProjex.Avalonia.Services;
using DevProjex.Infrastructure.FileSystem;

namespace DevProjex.Tests.Unit.Avalonia;

[Collection("AvaloniaUI")]
public sealed class GuiSelectionLoadMeasurementTests(ITestOutputHelper output)
{
	[Fact(Timeout = 180_000)]
	[Trait("Category", "LocalPerformance")]
	public void MeasureRootGitIgnoreMatcherConstruction()
	{
		var requestedRoot = Environment.GetEnvironmentVariable("DEVPROJEX_GUI_BENCHMARK_ROOT");
		Assert.SkipWhen(string.IsNullOrWhiteSpace(requestedRoot), "Set DEVPROJEX_GUI_BENCHMARK_ROOT for read-only profiling.");
		var projectRoot = Path.GetFullPath(requestedRoot!);
		var lines = File.ReadAllLines(Path.Combine(projectRoot, ".gitignore"));
		var comparisonSemantics = GitConfigPathComparisonSemanticsResolver.Instance.Resolve(projectRoot);
		Assert.True(comparisonSemantics.IsAuthoritative);
		for (var run = 0; run < 6; run++)
		{
			var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
			var timer = Stopwatch.StartNew();
			var matcher = GitIgnoreMatcher.Build(projectRoot, lines, comparisonSemantics);
			var milliseconds = timer.Elapsed.TotalMilliseconds;
			var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
			output.WriteLine(JsonSerializer.Serialize(new
			{
				Scenario = "root-gitignore-matcher-construction",
				Run = run,
				Milliseconds = milliseconds,
				AllocatedBytes = allocatedBytes,
				SourceLines = lines.Length,
				comparisonSemantics.IgnoreCase,
				comparisonSemantics.NormalizeUnicode
			}));
			GC.KeepAlive(matcher);
		}
	}

	[AvaloniaFact(Timeout = 180_000)]
	[Trait("Category", "LocalPerformance")]
	public async Task MeasureDefaultSelectionLoadAndInventoryProjection()
	{
		var requestedRoot = Environment.GetEnvironmentVariable("DEVPROJEX_GUI_BENCHMARK_ROOT");
		Assert.SkipWhen(string.IsNullOrWhiteSpace(requestedRoot), "Set DEVPROJEX_GUI_BENCHMARK_ROOT for read-only profiling.");
		var projectRoot = Path.GetFullPath(requestedRoot!);
		Assert.True(Directory.Exists(projectRoot));
		var localization = new LocalizationService(new JsonLocalizationCatalog(), AppLanguage.En);
		var ignoreRules = ProjectLoadWorkflowRuntime.CreateIgnoreRulesService();
		var scanOptions = new ScanOptionsUseCase(new FileSystemScanner());
		var buildTree = new BuildTreeUseCase(
			new TreeBuilder(),
			new TreeNodePresentationService(localization, new IconMapper()));
		var iterations = int.TryParse(Environment.GetEnvironmentVariable("DEVPROJEX_GUI_BENCHMARK_ITERATIONS"),
			out var requestedIterations) ? Math.Clamp(requestedIterations, 2, 64) : 6;

		for (var run = 0; run < iterations; run++)
		{
			var callbackMeasurements = new List<CallbackMeasurement>();
			T MeasureCallback<T>(string name, Func<T> callback)
			{
				var allocated = GC.GetTotalAllocatedBytes(precise: false);
				var timer = Stopwatch.StartNew();
				try
				{
					return callback();
				}
				finally
				{
					callbackMeasurements.Add(new CallbackMeasurement(
						name, timer.Elapsed.TotalMilliseconds, GC.GetTotalAllocatedBytes(precise: false) - allocated));
				}
			}

			var viewModel = new MainWindowViewModel(localization, new HelpContentProvider())
			{
				IsProjectLoaded = true
			};
			using var coordinator = new SelectionSyncCoordinator(
				viewModel,
				scanOptions,
				new FilterOptionSelectionService(),
				new IgnoreOptionsService(localization),
				(path, selected, roots) => MeasureCallback("build-rules", () => ignoreRules.Build(path, selected, roots)),
				(path, roots) => MeasureCallback("availability", () =>
					ignoreRules.GetIgnoreOptionsAvailability(path, roots) with { ShowAdvancedCounts = true }),
				_ => false,
				() => projectRoot,
				buildIgnoreRulesWithCancellation: (path, selected, roots, token) => MeasureCallback("build-rules", () =>
					ignoreRules.BuildWithCancellation(path, selected, roots, token)),
				getIgnoreOptionsAvailabilityWithCancellation: (path, roots, token) =>
					MeasureCallback("availability", () =>
						ignoreRules.GetIgnoreOptionsAvailabilityWithCancellation(path, roots, token) with { ShowAdvancedCounts = true }),
				gitScopePathProvider: new GitScopePathProvider());
			coordinator.ResetProjectProfileSelections(projectRoot);
			// Normal folder load refreshes discovery; only unchanged F5 uses revalidation.
			ignoreRules.RefreshDiscoveryCaches(projectRoot);
			coordinator.InvalidateFileSystemCaches();
			using var ignoreMeasurement = IgnorePipelineDiagnostics.BeginMeasurement();
			using var contentMeasurement = ContentPipelineDiagnostics.BeginMeasurement();
			var allocatedBefore = GC.GetTotalAllocatedBytes(precise: false);
			var phase = Stopwatch.StartNew();
			var snapshot = await coordinator.BuildProjectSelectionSnapshotAsync(
				projectRoot,
				TestContext.Current.CancellationToken);
			var selectionMilliseconds = phase.Elapsed.TotalMilliseconds;
			var selectionAllocatedBytes = GC.GetTotalAllocatedBytes(precise: false) - allocatedBefore;
			Assert.NotNull(snapshot);
			Assert.NotNull(snapshot.TreeInventory);
			Assert.False(snapshot.HadScanFailure);
			var selectionDiagnostics = ignoreMeasurement.Capture();

			phase.Restart();
			var selectedRoots = snapshot.RootOptions!
				.Where(static option => option.IsChecked)
				.Select(static option => option.Name)
				.ToHashSet(ProjectTreePathIdentity.CanonicalComparer);
			var options = new TreeFilterOptions(
				snapshot.EffectiveExtensionOptions
					.Where(static option => option.IsChecked)
					.Select(static option => option.Name)
					.ToHashSet(StringComparer.OrdinalIgnoreCase),
				selectedRoots,
				ProjectLoadIgnoreRulesResolver.Resolve(snapshot, selected =>
					ignoreRules.BuildWithCancellation(
						projectRoot, selected, selectedRoots, TestContext.Current.CancellationToken)));
			var scope = ProjectTreeInventoryReuseScope.Create(projectRoot, options, supportsHiddenDotFolderVariants: true);
			var tree = await Task.Run(
				() => buildTree.ExecuteWithInventory(
					new BuildTreeRequest(projectRoot, options),
					snapshot.TreeInventory,
					TestContext.Current.CancellationToken),
				TestContext.Current.CancellationToken);
			var projectionMilliseconds = phase.Elapsed.TotalMilliseconds;
			var projectionAllocatedBytes = GC.GetTotalAllocatedBytes(precise: false) - allocatedBefore - selectionAllocatedBytes;
			var projectionDiagnostics = ignoreMeasurement.Capture();
			Assert.Same(snapshot.TreeInventory, tree.Inventory);
			Assert.True(scope.CanProject(projectRoot, options));

			phase.Restart();
			Assert.True(coordinator.ApplyProjectSelectionSnapshot(projectRoot, snapshot));
			var selectionPublicationMilliseconds = phase.Elapsed.TotalMilliseconds;
			var allocatedBytes = GC.GetTotalAllocatedBytes(precise: false) - allocatedBefore;
			viewModel.TreeNodes.Add(new TreeNodeViewModel(tree.Tree.Root, parent: null, icon: null));
			var metricsIo = new MetricsPipelineIoTestPoint();
			using var metricsStatus = new StatusOperationCoordinator(
				viewModel, () => false, () => viewModel.StatusOperationCalculatingData);
			using var metricsBackground = new BackgroundTaskRegistry();
			using var metricsPipeline = new MetricsPipeline(
				viewModel, localization, new FileContentAnalyzer(), new TreeExportService(), metricsStatus,
				() => tree.Tree, () => projectRoot,
				() => new HashSet<string>(ProjectTreePathIdentity.CanonicalComparer),
				() => TreeTextFormat.Ascii, () => null, () => 1400,
				backgroundTasks: metricsBackground, ioTestPoint: metricsIo);
			var metricsAllocatedBefore = GC.GetTotalAllocatedBytes(precise: false);
			phase.Restart();
			await metricsPipeline.InitializeFileMetricsCacheSoonAfterFirstPaintAsync(
				tree.Tree, TestContext.Current.CancellationToken);
			var metricsScanMilliseconds = phase.Elapsed.TotalMilliseconds;
			while (metricsBackground.TrackedTaskCount != 0)
			{
				Assert.True(phase.Elapsed < TimeSpan.FromSeconds(30), "Metrics publication did not complete.");
				await Task.Delay(1, TestContext.Current.CancellationToken);
			}
			var metricsPublicationMilliseconds = phase.Elapsed.TotalMilliseconds;
			var metricsAllocatedBytes = GC.GetTotalAllocatedBytes(precise: false) - metricsAllocatedBefore;
			Assert.True(metricsPipeline.HasCompleteBaseline);
			Assert.True(metricsPipeline.HasStatusMetricsSnapshot);
			var publishedMetrics = new
			{
				TreeLines = ReadPublishedMetric(metricsPipeline, "_lastStatusTreeLines"),
				TreeChars = ReadPublishedMetric(metricsPipeline, "_lastStatusTreeChars"),
				TreeTokens = ReadPublishedMetric(metricsPipeline, "_lastStatusTreeTokens"),
				ContentLines = ReadPublishedMetric(metricsPipeline, "_lastStatusContentLines"),
				ContentChars = ReadPublishedMetric(metricsPipeline, "_lastStatusContentChars"),
				ContentTokens = ReadPublishedMetric(metricsPipeline, "_lastStatusContentTokens")
			};
			var contentDiagnostics = contentMeasurement.Capture();
			output.WriteLine(JsonSerializer.Serialize(new
			{
				Scenario = "gui-default-selection-and-inventory",
				Run = run,
				SelectionMilliseconds = selectionMilliseconds,
				ProjectionMilliseconds = projectionMilliseconds,
				SelectionPublicationMilliseconds = selectionPublicationMilliseconds,
				MetricsScanMilliseconds = metricsScanMilliseconds,
				MetricsPublicationMilliseconds = metricsPublicationMilliseconds,
				TotalBackendMilliseconds = selectionMilliseconds + projectionMilliseconds +
					selectionPublicationMilliseconds + metricsPublicationMilliseconds,
				AllocatedBytes = allocatedBytes,
				MetricsAllocatedBytes = metricsAllocatedBytes,
				TotalBackendAllocatedBytes = allocatedBytes + metricsAllocatedBytes,
				SelectionAllocatedBytes = selectionAllocatedBytes,
				ProjectionAllocatedBytes = projectionAllocatedBytes,
				CallbackMeasurements = callbackMeasurements,
				Files = tree.Tree.OrderedFilePaths?.Count ?? 0,
				InventoryEntries = snapshot.TreeInventory.Entries.Count,
				GitMode = options.IgnoreRules.GitFilteringMode.ToString(),
				IgnoreOptions = snapshot.EffectiveIgnoreOptions.Select(static option => option.ToString()).ToArray(),
				SelectionDiagnostics = selectionDiagnostics,
				ProjectionDiagnostics = projectionDiagnostics,
				MetricsIo = metricsIo.Snapshot,
				PublishedMetrics = publishedMetrics,
				contentDiagnostics.FullFileReads,
				contentDiagnostics.FullFileReadBytes
			}));
		}
	}

	private static long ReadPublishedMetric(MetricsPipeline pipeline, string fieldName) =>
		(long)typeof(MetricsPipeline).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
			.GetValue(pipeline)!;

	private sealed record CallbackMeasurement(string Name, double Milliseconds, long AllocatedBytes);
}
