using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;
using DevProjex.Application.Diagnostics;
using DevProjex.Application.UseCases;
using DevProjex.Infrastructure.FileSystem;
using DevProjex.Infrastructure.Git;
using DevProjex.Infrastructure.ResourceStore;
using DevProjex.Kernel.Abstractions;

namespace DevProjex.Tests.Terminal;

public sealed class TerminalWorkspaceMetricsLoadTests
{
	[Fact(Timeout = 300_000)]
	[Trait("Category", "LocalPerformance")]
	public async Task RealProjectMeasuresDeferredTuiOpenAgainstFullMetricsPath()
	{
		var projectRoot = Environment.GetEnvironmentVariable("DEVPROJEX_GUI_BENCHMARK_ROOT");
		Assert.SkipWhen(string.IsNullOrWhiteSpace(projectRoot),
			"Set DEVPROJEX_GUI_BENCHMARK_ROOT for read-only profiling.");
		projectRoot = Path.GetFullPath(projectRoot!);
		Assert.True(Directory.Exists(projectRoot));
		using var appData = new TemporaryDirectory();
		using var services = new TerminalServiceFactory(() => appData.Path).Create(AppLanguage.En);
		var analyzer = new CountingMetricsAnalyzer(new FileContentAnalyzer());
		var (controller, measuredServices) = CreateMeasuredController(services, analyzer);
		var cancellationToken = TestContext.Current.CancellationToken;

		async Task<(ProjectContextPlan Plan, BackendMeasurement Measurement)> MeasureFullAsync()
		{
			services.IgnoreRulesService.RefreshDiscoveryCaches(projectRoot);
			var callsBefore = analyzer.MetricsCalls;
			using var pipeline = ContentPipelineDiagnostics.BeginMeasurement();
			var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
			var started = Stopwatch.GetTimestamp();
			var selection = await measuredServices.SelectionResolver.ResolveAsync(
				projectRoot,
				ProjectProfileReference.Standard,
				new ProjectSelectionSpec(),
				cancellationToken);
			var plan = await measuredServices.ContextFactory.BuildAsync(
				projectRoot,
				selection,
				cancellationToken: cancellationToken,
				captureIgnoreImpactCounts: true);
			return (plan, CaptureMeasurement(started, allocatedBefore, callsBefore, analyzer, pipeline.Capture()));
		}

		async Task<(TerminalWorkspaceState State, BackendMeasurement Measurement)> MeasureDeferredAsync()
		{
			services.IgnoreRulesService.RefreshDiscoveryCaches(projectRoot);
			var callsBefore = analyzer.MetricsCalls;
			using var pipeline = ContentPipelineDiagnostics.BeginMeasurement();
			var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
			var started = Stopwatch.GetTimestamp();
			var state = await controller.OpenAsync(
				projectRoot,
				ProjectProfileReference.Standard,
				cancellationToken);
			return (state, CaptureMeasurement(started, allocatedBefore, callsBefore, analyzer, pipeline.Capture()));
		}

		FileStamp[]? frozenSourceStamps = null;
		for (var iteration = -1; iteration < 5; iteration++)
		{
			ProjectContextPlan full;
			TerminalWorkspaceState state;
			BackendMeasurement fullMeasurement;
			BackendMeasurement deferredMeasurement;
			FileStamp[] sourceStamps;
			if (iteration % 2 == 0)
			{
				(full, fullMeasurement) = await MeasureFullAsync();
				sourceStamps = CaptureSourceStamps(full.IncludedFiles);
				(state, deferredMeasurement) = await MeasureDeferredAsync();
			}
			else
			{
				(state, deferredMeasurement) = await MeasureDeferredAsync();
				sourceStamps = CaptureSourceStamps(state.Plan.IncludedFiles);
				(full, fullMeasurement) = await MeasureFullAsync();
			}

			using (state)
			{
				Assert.Equal(sourceStamps, CaptureSourceStamps(full.IncludedFiles));
				Assert.Equal(sourceStamps, CaptureSourceStamps(state.Plan.IncludedFiles));
				frozenSourceStamps ??= sourceStamps;
				Assert.Equal(frozenSourceStamps, sourceStamps);
				Assert.Equal(full.IncludedFiles, state.Plan.IncludedFiles);
				Assert.Equal(full.IncludedBytes, state.Plan.IncludedBytes);
				Assert.True(full.HasIgnoreOptionCounts);
				Assert.True(state.Plan.HasIgnoreOptionCounts);
				Assert.Equal(full.IncludedFiles.Count, fullMeasurement.MetricCalls);
				Assert.Equal(0, deferredMeasurement.MetricCalls);

				ProjectContextPlan current;
				BackendMeasurement onDemandMeasurement;
				using (var pipeline = ContentPipelineDiagnostics.BeginMeasurement())
				{
					var callsBefore = analyzer.MetricsCalls;
					var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
					var started = Stopwatch.GetTimestamp();
					current = await controller.BuildCurrentPlanAsync(state, cancellationToken);
					onDemandMeasurement = CaptureMeasurement(
						started, allocatedBefore, callsBefore, analyzer, pipeline.Capture());
				}
				Assert.Equal(current.IncludedFiles.Count, onDemandMeasurement.MetricCalls);
				Assert.Equal(full.Analysis.Metrics, current.Analysis.Metrics);
				Assert.Equal(full.IncludedFiles, current.IncludedFiles);
				Assert.Equal(full.IncludedBytes, current.IncludedBytes);

				if (iteration == 0)
				{
					using var fullState = new TerminalWorkspaceState(
						full,
						measuredServices.ContextPlanner.GetSelectedRelativePathFrontier(full));
					var previousExplicit = await controller.BuildCurrentPlanAsync(fullState, cancellationToken);
					var previousOutput = await RenderStructuredTreeAsync(
						services.ContextDocumentService, previousExplicit, cancellationToken);
					var currentOutput = await RenderStructuredTreeAsync(
						services.ContextDocumentService, current, cancellationToken);
					Assert.True(previousOutput.AsSpan().SequenceEqual(currentOutput),
						$"Structured tree output differs: old={previousOutput.Length}, new={currentOutput.Length} bytes.");
				}

				TestContext.Current.TestOutputHelper?.WriteLine(JsonSerializer.Serialize(new
				{
					iteration,
					phase = iteration < 0 ? "warm-up" : "sample",
					order = iteration % 2 == 0 ? "full-then-deferred" : "deferred-then-full",
					includedFiles = full.IncludedFiles.Count,
					includedBytes = full.IncludedBytes,
					full = fullMeasurement,
					deferred = deferredMeasurement,
					onDemand = onDemandMeasurement
				}));
			}
		}
	}

	[Fact]
	public async Task StructuredPreviewAndExactDocumentKeepContentMetricsAfterDeferredOpen()
	{
		using var workspace = new TemporaryDirectory();
		using var appData = new TemporaryDirectory();
		workspace.WriteFile("src/First.cs", "class First {}\n");
		using var services = new TerminalServiceFactory(() => appData.Path).Create(AppLanguage.En);
		var controller = new TerminalWorkspaceController(services, new TestTerminalEnvironment());
		using var state = await controller.OpenAsync(
			workspace.Path,
			ProjectProfileReference.Standard,
			TestContext.Current.CancellationToken);
		var expected = await services.ContextFactory.BuildAsync(
			workspace.Path,
			state.BuildSelection(),
			cancellationToken: TestContext.Current.CancellationToken);

		await controller.RefreshPreviewAsync(
			state,
			ProjectContextView.Content,
			ProjectContextDocumentFormat.Json,
			TestContext.Current.CancellationToken);
		using var preview = JsonDocument.Parse(state.PreviewText);
		Assert.Equal(
			expected.Analysis.Metrics.Content.Chars,
			preview.RootElement.GetProperty("metrics").GetProperty("characters").GetInt64());

		using var exact = await controller.BuildExactExportDocumentAsync(
			state,
			ProjectContextView.Content,
			ProjectContextDocumentFormat.Xml,
			TestContext.Current.CancellationToken);
		var exactXml = XDocument.Parse(exact.GetFullText());
		Assert.Equal(
			expected.Analysis.Metrics.Content.Chars,
			long.Parse(exactXml.Root!.Element("metrics")!.Element("characters")!.Value,
				System.Globalization.CultureInfo.InvariantCulture));
	}

	[Fact]
	public async Task OpeningAndRefreshingWorkspaceDefersContentMetricsUntilRequested()
	{
		using var workspace = new TemporaryDirectory();
		using var appData = new TemporaryDirectory();
		workspace.WriteFile("src/First.cs", "class First {}\n");
		workspace.WriteFile("src/Second.cs", "class Second {}\n");
		using var services = new TerminalServiceFactory(() => appData.Path).Create(AppLanguage.En);
		var analyzer = new CountingMetricsAnalyzer(new FileContentAnalyzer());
		var (controller, _) = CreateMeasuredController(services, analyzer);

		using var state = await controller.OpenAsync(
			workspace.Path,
			ProjectProfileReference.Standard,
			TestContext.Current.CancellationToken);
		Assert.Equal(2, state.Plan.IncludedFiles.Count);
		Assert.True(state.Plan.HasIgnoreOptionCounts);
		Assert.Equal(0, analyzer.MetricsCalls);

		await controller.RefreshProjectAsync(state, TestContext.Current.CancellationToken);
		Assert.Equal(2, state.Plan.IncludedFiles.Count);
		Assert.True(state.Plan.HasIgnoreOptionCounts);
		Assert.Equal(0, analyzer.MetricsCalls);

		var current = await controller.BuildCurrentPlanAsync(
			state,
			TestContext.Current.CancellationToken);
		var expected = await services.ContextFactory.BuildAsync(
			workspace.Path,
			state.BuildSelection(),
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.Equal(2, analyzer.MetricsCalls);
		Assert.True(current.HasIgnoreOptionCounts);
		Assert.Equal(expected.Analysis.Metrics.Content, current.Analysis.Metrics.Content);
		Assert.Equal(expected.Analysis.Metrics.Tree, current.Analysis.Metrics.Tree);

		using var cancelled = new CancellationTokenSource();
		cancelled.Cancel();
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
			controller.BuildCurrentPlanAsync(state, cancelled.Token));
		Assert.Equal(2, analyzer.MetricsCalls);

		state.RestoreSelectedRelativePaths([]);
		var empty = await controller.BuildCurrentPlanAsync(
			state,
			TestContext.Current.CancellationToken);
		Assert.Empty(empty.IncludedFiles);
		Assert.Equal(ProjectOutputMetricsReport.Empty, empty.Analysis.Metrics.Content);
		Assert.Equal(2, analyzer.MetricsCalls);
	}

	private static (TerminalWorkspaceController Controller, TerminalServices MeasuredServices)
		CreateMeasuredController(TerminalServices services, CountingMetricsAnalyzer analyzer)
	{
		var analysis = new ProjectAnalysisService(
			new ScanOptionsUseCase(new FileSystemScanner()),
			new BuildTreeUseCase(
				new TreeBuilder(),
				new TreeNodePresentationService(services.Localization, new IconMapper())),
			new FilterOptionSelectionService(),
			services.IgnoreOptionsService,
			services.IgnoreRulesService,
			services.TreeExportService,
			analyzer);
		var planner = new ProjectContextPlanner(analysis);
		var factory = new TerminalProjectContextFactory(
			planner,
			services.SourceIdentityResolver,
			services.SecretRedactionSession,
			new GitScopePathProvider(),
			new GitRemoteDiffRangeResolver());
		var measuredServices = services with
		{
			AnalysisService = analysis,
			ContextPlanner = planner,
			ContextFactory = factory
		};
		return (new TerminalWorkspaceController(measuredServices, new TestTerminalEnvironment()), measuredServices);
	}

	private static BackendMeasurement CaptureMeasurement(
		long started,
		long allocatedBefore,
		int callsBefore,
		CountingMetricsAnalyzer analyzer,
		ContentPipelineDiagnosticSnapshot pipeline) =>
		new(
			Stopwatch.GetElapsedTime(started).TotalMilliseconds,
			GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore,
			analyzer.MetricsCalls - callsBefore,
			pipeline.FullFileReads,
			pipeline.FullFileReadBytes);

	private static FileStamp[] CaptureSourceStamps(IReadOnlyList<string> paths) =>
		paths.Select(static path =>
		{
			var source = new FileInfo(path);
			return source.Exists
				? new FileStamp(path, source.Length, source.LastWriteTimeUtc.Ticks)
				: new FileStamp(path, -1, -1);
		}).ToArray();

	private static async Task<byte[]> RenderStructuredTreeAsync(
		ProjectContextDocumentService service,
		ProjectContextPlan plan,
		CancellationToken cancellationToken)
	{
		using var destination = new MemoryStream();
		await service.WriteCompleteAsync(
			plan,
			ProjectContextView.Tree,
			ProjectContextDocumentFormat.Json,
			destination,
			cancellationToken);
		return destination.ToArray();
	}

	private sealed record BackendMeasurement(
		double ElapsedMilliseconds,
		long AllocatedBytes,
		int MetricCalls,
		long FileReads,
		long FileReadBytes);

	private readonly record struct FileStamp(string Path, long Length, long LastWriteUtcTicks);

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
