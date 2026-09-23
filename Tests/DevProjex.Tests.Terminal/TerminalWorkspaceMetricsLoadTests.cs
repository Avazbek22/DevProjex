using System.Text.Json;
using System.Xml.Linq;
using DevProjex.Application.UseCases;
using DevProjex.Infrastructure.FileSystem;
using DevProjex.Infrastructure.Git;
using DevProjex.Infrastructure.ResourceStore;
using DevProjex.Kernel.Abstractions;

namespace DevProjex.Tests.Terminal;

public sealed class TerminalWorkspaceMetricsLoadTests
{
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
		var controller = new TerminalWorkspaceController(
			services with
			{
				AnalysisService = analysis,
				ContextPlanner = planner,
				ContextFactory = factory
			},
			new TestTerminalEnvironment());

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
