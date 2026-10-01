using System.Diagnostics;
using DevProjex.Application.Diagnostics;
using DevProjex.Avalonia.Services;

namespace DevProjex.Tests.Unit.Avalonia;

[Collection("AvaloniaUI")]
public sealed class GuiBackendLoadMeasurementTests(ITestOutputHelper output)
{
	[AvaloniaFact(Timeout = 180_000)]
	[Trait("Category", "LocalPerformance")]
	public async Task RealProject_RecordsInitialMetricsAndPublicationWithoutVisualDelays()
	{
		var projectRoot = Environment.GetEnvironmentVariable("DEVPROJEX_GUI_BENCHMARK_ROOT");
		Assert.SkipWhen(string.IsNullOrWhiteSpace(projectRoot), "Set DEVPROJEX_GUI_BENCHMARK_ROOT for read-only profiling.");
		projectRoot = Path.GetFullPath(projectRoot!);
		Assert.True(Directory.Exists(projectRoot));
		using var appData = new TemporaryDirectory();
		var compositionStarted = Stopwatch.GetTimestamp();
		var services = AvaloniaCompositionRoot.CreateDefault(DesktopStartupOptions.Default, () => appData.Path);
		output.WriteLine($"composition_ms={Stopwatch.GetElapsedTime(compositionStarted).TotalMilliseconds:F3}");
		try
		{
			for (var iteration = 0; iteration < 6; iteration++)
			{
				// Normal folder loads refresh discovery while retaining validated compiled ignore matchers.
				services.IgnoreRulesService.RefreshDiscoveryCaches(projectRoot);
				using var ignoreMeasurement = IgnorePipelineDiagnostics.BeginMeasurement();
				using var contentMeasurement = ContentPipelineDiagnostics.BeginMeasurement();
				var loadStarted = Stopwatch.GetTimestamp();
				var loaded = await Task.Run(() => services.ProjectAnalysisService.Load(
					new ProjectAnalysisRequest(projectRoot), TestContext.Current.CancellationToken));
				var loadMilliseconds = Stopwatch.GetElapsedTime(loadStarted).TotalMilliseconds;
				var tree = loaded.Tree;
				var viewModel = new MainWindowViewModel(services.Localization, services.HelpContentProvider)
				{
					IsProjectLoaded = true
				};
				viewModel.TreeNodes.Add(new TreeNodeViewModel(tree.Root, parent: null, icon: null));
				var selected = new HashSet<string>(ProjectTreePathIdentity.CanonicalComparer);
				var io = new MetricsPipelineIoTestPoint();
				using var status = new StatusOperationCoordinator(viewModel,
					isBackgroundMetricsActive: () => false,
					metricsOperationTextProvider: () => viewModel.StatusOperationCalculatingData);
				using var background = new BackgroundTaskRegistry(reportFailure: (name, failure) =>
					throw new InvalidOperationException(name, failure));
				using var pipeline = new MetricsPipeline(viewModel, services.Localization,
					services.FileContentAnalyzer, services.TreeExportService, status,
					() => tree, () => projectRoot, () => selected, () => TreeTextFormat.Ascii,
					() => null, () => 1400, backgroundTasks: background, ioTestPoint: io);
				var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
				var metricsStarted = Stopwatch.GetTimestamp();
				await pipeline.InitializeFileMetricsCacheSoonAfterFirstPaintAsync(
					tree, TestContext.Current.CancellationToken);
				var scanMilliseconds = Stopwatch.GetElapsedTime(metricsStarted).TotalMilliseconds;
				while (background.TrackedTaskCount != 0)
				{
					TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
					Assert.True(Stopwatch.GetElapsedTime(metricsStarted) < TimeSpan.FromSeconds(30));
					await Task.Delay(1, TestContext.Current.CancellationToken);
				}
				var publicationMilliseconds = Stopwatch.GetElapsedTime(metricsStarted).TotalMilliseconds;
				var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
				Assert.True(pipeline.HasCompleteBaseline);
				Assert.True(pipeline.HasStatusMetricsSnapshot);
				var content = contentMeasurement.Capture();
				output.WriteLine(JsonSerializer.Serialize(new
				{
					iteration,
					phase = iteration == 0 ? "first" : "warm-filesystem-fresh-metrics",
					files = tree.OrderedFilePaths?.Count,
					sharedLoadMs = loadMilliseconds,
					metricsScanMs = scanMilliseconds,
					metricsPublishedMs = publicationMilliseconds,
					metricsAllocatedBytes = allocated,
					io = io.Snapshot,
					content.FullFileReads,
					content.FullFileReadBytes,
					content.ContentFingerprintComputations,
					ignore = ignoreMeasurement.Capture()
				}));
			}
		}
		finally
		{
			services.CodeCompressionSession.Dispose();
			services.SecretRedactionSession.Dispose();
		}
	}
}
