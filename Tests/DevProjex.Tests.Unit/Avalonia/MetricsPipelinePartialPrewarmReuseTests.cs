using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using DevProjex.Application.Compression;
using DevProjex.Application.Preview;
using DevProjex.Avalonia.Services;

namespace DevProjex.Tests.Unit.Avalonia;

[Collection("AvaloniaUI")]
public sealed class MetricsPipelinePartialPrewarmReuseTests
{
	[AvaloniaTheory]
	[InlineData(".cs")]
	[InlineData(".txt")]
	public async Task PartialPrewarm_ReusesSelectedFactsAndPopulatesUnselectedMetrics(string extension)
	{
		using var fixture = new Fixture(extension);
		await fixture.PrewarmAsync();
		Assert.Equal(1, fixture.Reads(fixture.SelectedFile));
		Assert.Equal(0, fixture.Reads(fixture.OtherFile));

		await fixture.InitializeAsync();

		await fixture.AssertOutputMetricsAsync();
		Assert.True(fixture.HasCachedMetrics(fixture.OtherFile));
		Assert.Equal(1, fixture.Reads(fixture.OtherFile));
		Assert.Equal(0, fixture.Pipeline.RetainedReadFactBytes);
		Assert.Equal(1, fixture.Reads(fixture.SelectedFile));
	}

	[AvaloniaTheory]
	[InlineData(Invalidation.ChangedSource)]
	[InlineData(Invalidation.ChangedTransform)]
	[InlineData(Invalidation.ChangedRootWithSameFiles)]
	public async Task PartialPrewarm_RejectsFactsWhoseSourceOrContextChanged(Invalidation invalidation)
	{
		using var fixture = new Fixture(".cs");
		await fixture.PrewarmAsync();
		switch (invalidation)
		{
			case Invalidation.ChangedSource:
				await File.AppendAllTextAsync(fixture.SelectedFile, "\n// changed source", TestContext.Current.CancellationToken);
				File.SetLastWriteTimeUtc(fixture.SelectedFile, DateTime.UtcNow.AddMinutes(1));
				break;
			case Invalidation.ChangedTransform:
				fixture.Kinds = CodeTransformKinds.Comments;
				break;
			case Invalidation.ChangedRootWithSameFiles:
				fixture.CurrentRoot = Path.GetDirectoryName(fixture.CurrentRoot)!;
				break;
		}

		await fixture.InitializeAsync();

		Assert.Equal(2, fixture.Reads(fixture.SelectedFile));
		Assert.Equal(1, fixture.Reads(fixture.OtherFile));
		Assert.Equal(0, fixture.Pipeline.RetainedReadFactBytes);
		await fixture.AssertOutputMetricsAsync();
	}

	[AvaloniaFact]
	public async Task PartialPrewarm_CancellationReleasesFactsAndNextPassReadsAgain()
	{
		using var fixture = new Fixture(".cs");
		await fixture.PrewarmAsync();

		fixture.Pipeline.CancelByUser();

		Assert.Equal(0, fixture.Pipeline.RetainedReadFactBytes);
		await fixture.InitializeAsync();
		Assert.Equal(2, fixture.Reads(fixture.SelectedFile));
		await fixture.AssertOutputMetricsAsync();
	}

	[AvaloniaFact]
	public async Task PartialPrewarm_DoesNotImportFactsForFilesRemovedFromCurrentTree()
	{
		using var fixture = new Fixture(".cs");
		await fixture.PrewarmAsync();
		fixture.SelectOnlyOtherFileAndRemoveOriginalFromTree();

		await fixture.InitializeAsync();

		Assert.Equal(1, fixture.Reads(fixture.SelectedFile));
		Assert.Equal(1, fixture.Reads(fixture.OtherFile));
		Assert.False(fixture.HasCachedMetrics(fixture.SelectedFile));
		Assert.True(fixture.HasCachedMetrics(fixture.OtherFile));
		Assert.Equal(0, fixture.Pipeline.RetainedReadFactBytes);
		await fixture.AssertOutputMetricsAsync();
	}

	public enum Invalidation { ChangedSource, ChangedTransform, ChangedRootWithSameFiles }

	private sealed class Fixture : IDisposable
	{
		private readonly TemporaryDirectory _workspace = new();
		private readonly BackgroundTaskRegistry _background = new();
		private readonly ConcurrentDictionary<string, int> _reads = new(PathComparer.Default);
		private readonly CodeCompressionSession _compression = new(new FixtureCompressor());
		private readonly StatusOperationCoordinator _status;
		private readonly MainWindowViewModel _viewModel;
		private BuildTreeResult _tree;
		private HashSet<string> _selectedPaths;

		public Fixture(string extension)
		{
			CurrentRoot = _workspace.CreateFolder("project");
			SelectedFile = _workspace.CreateFile("project/Selected" + extension,
				"internal class Selected {\n" + new string(' ', 262144) + " void Run() { }\n}\n");
			OtherFile = _workspace.CreateFile("project/Other.cs", "internal class Other { }\n");
			_selectedPaths = new HashSet<string>([SelectedFile], PathComparer.Default);
			_tree = CreateTree([OtherFile, SelectedFile]);
			var localization = new LocalizationService(new JsonLocalizationCatalog(), AppLanguage.En);
			_viewModel = new MainWindowViewModel(localization, new HelpContentProvider())
			{
				IsProjectLoaded = true,
				SelectedPreviewContentMode = PreviewContentMode.Content
			};
			_viewModel.TreeNodes.Add(new TreeNodeViewModel(_tree.Root, parent: null, icon: null));
			_status = new StatusOperationCoordinator(_viewModel, () => false, () => _viewModel.StatusOperationCalculatingData);
			var analyzer = new FileContentAnalyzer((path, bufferSize, share, asynchronous) =>
			{
				_reads.AddOrUpdate(path, 1, static (_, count) => count + 1);
				return new FileStream(path, FileMode.Open, FileAccess.Read, share, bufferSize, asynchronous);
			});
			Pipeline = new MetricsPipeline(_viewModel, localization, analyzer, new TreeExportService(), _status,
				() => _tree, () => CurrentRoot, () => _selectedPaths,
				() => TreeTextFormat.Ascii, () => null, () => 1400,
				transformationContextProvider: () => Transformation,
				backgroundTasks: _background);
		}

		public string CurrentRoot { get; set; }
		public string SelectedFile { get; }
		public string OtherFile { get; }
		public CodeTransformKinds Kinds { get; set; } = CodeTransformKinds.Bodies;
		public MetricsPipeline Pipeline { get; }
		private ContentTransformationContext Transformation => ContentTransformationContext.For(
			new CodeCompressionContext(CurrentRoot, _compression, Kinds), redaction: null)!;
		public int Reads(string path) => _reads.GetValueOrDefault(path);

		public async Task PrewarmAsync()
		{
			await Pipeline.PrewarmCompressionAsync(_tree, TestContext.Current.CancellationToken,
				retainReadFactsForNextMetricsPass: true);
			Assert.True(Pipeline.RetainedReadFactBytes > 0);
		}

		public async Task InitializeAsync()
		{
			await Pipeline.InitializeFileMetricsCacheSoonAfterFirstPaintAsync(_tree, TestContext.Current.CancellationToken);
			var started = Stopwatch.StartNew();
			while (_background.TrackedTaskCount != 0)
			{
				Assert.True(started.Elapsed < TimeSpan.FromSeconds(5), "Metrics publication did not complete.");
				await Task.Delay(1, TestContext.Current.CancellationToken);
			}
			Assert.True(Pipeline.HasCompleteBaseline);
			Assert.True(Pipeline.HasStatusMetricsSnapshot);
		}

		public async Task AssertOutputMetricsAsync()
		{
			var rendered = await new SelectedContentExportService(new FileContentAnalyzer()).BuildAsync(
				_selectedPaths, TestContext.Current.CancellationToken,
				TreeAndContentExportService.CreateRelativeContentHeaderPathMapper(CurrentRoot),
				transformationContext: Transformation, displayRootPath: CurrentRoot);
			using var document = new InMemoryPreviewTextDocument("x");
			Assert.True(Pipeline.TryGetCachedPreviewSelectionMetrics(PreviewContentMode.Content, document,
				new PreviewSelectionRange(1, 0, 1, 1), out var actual));
			Assert.Equal(ExportOutputMetricsCalculator.FromText(rendered), actual);
		}

		public bool HasCachedMetrics(string path)
		{
			var cache = (IDictionary)typeof(MetricsPipeline).GetField("_fileMetricsCache",
				BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Pipeline)!;
			return cache.Contains(path);
		}

		public void SelectOnlyOtherFileAndRemoveOriginalFromTree()
		{
			_tree = CreateTree([OtherFile]);
			_selectedPaths = new HashSet<string>([OtherFile], PathComparer.Default);
			_viewModel.TreeNodes.Clear();
			_viewModel.TreeNodes.Add(new TreeNodeViewModel(_tree.Root, parent: null, icon: null));
		}

		private BuildTreeResult CreateTree(IReadOnlyList<string> files) => new(
			new TreeNodeDescriptor("project", CurrentRoot, true, false, "folder",
				files.Select(path => new TreeNodeDescriptor(Path.GetFileName(path), path, false, false, "text", [])).ToArray()),
			false, false, files);

		public void Dispose()
		{
			Pipeline.Dispose();
			_status.Dispose();
			_background.Dispose();
			_compression.Dispose();
			_workspace.Dispose();
		}
	}

	private sealed class FixtureCompressor : ICodeCompressor
	{
		public string TransformIdentity => "partial-prewarm:v1";
		public bool IsSupported(string relativePath) => Path.GetExtension(relativePath) == ".cs";
		public ICodeCompressionScope CreateScope(string projectRoot) => new Scope(CodeTransformKinds.Bodies);
		public ICodeCompressionScope CreateScope(string projectRoot, CodeTransformKinds kinds) => new Scope(kinds);

		private sealed class Scope(CodeTransformKinds kinds) : ICodeCompressionScope
		{
			public CodeCompressionAnalysis Analyze(string fullPath, string relativePath, string content,
				CancellationToken cancellationToken)
			{
				cancellationToken.ThrowIfCancellationRequested();
				var start = content.IndexOf('{');
				var edits = start >= 0
					? new[] { new CodeCompressionEdit(start, content.Length - start, "{...}\n") { Kinds = kinds } }
					: [];
				return new CodeCompressionAnalysis(CodeCompressionPlan.Create(relativePath, "csharp", edits,
					content.Length, CodeTransformIdentity.Create("partial-prewarm:v1", kinds)), null);
			}
			public void Dispose() { }
		}
	}
}
