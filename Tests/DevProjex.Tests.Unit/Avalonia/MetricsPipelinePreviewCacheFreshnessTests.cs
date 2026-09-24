using System.Diagnostics;
using DevProjex.Application.Preview;
using DevProjex.Avalonia.Services;

namespace DevProjex.Tests.Unit.Avalonia;

[Collection("AvaloniaUI")]
public sealed class MetricsPipelinePreviewCacheFreshnessTests
{
	[AvaloniaFact]
	public void ScheduledSelectionChangeDoesNotReusePreviousPreviewSelectionMetrics()
	{
		using var directory = new TemporaryDirectory();
		var firstFile = directory.CreateFile("First.cs", "first");
		var secondFile = directory.CreateFile("Second.cs", "second");
		var tree = CreateTree(directory.Path, [firstFile, secondFile]);
		var localization = new LocalizationService(new JsonLocalizationCatalog(), AppLanguage.En);
		var viewModel = CreateViewModel(localization, tree.Root);
		using var document = new InMemoryPreviewTextDocument("selection");
		viewModel.PreviewDocument = document;
		using var status = CreateStatus(viewModel);
		IReadOnlySet<string> selectedPaths = new HashSet<string>([firstFile], PathComparer.Default);
		using var pipeline = CreatePipeline(
			viewModel, localization, status, tree, new FileContentAnalyzer(),
			() => selectedPaths);
		var fullSelection = new PreviewSelectionRange(1, 0, 1, "selection".Length);

		pipeline.UpdateStatusBarMetrics(1, 10, 3, 2, 20, 5);
		Assert.True(pipeline.TryGetCachedPreviewSelectionMetrics(
			PreviewContentMode.Content, document, fullSelection, out _));
		var visibleStatus = viewModel.StatusContentStatsText;

		selectedPaths = new HashSet<string>([secondFile], PathComparer.Default);
		pipeline.ScheduleRecalculate();

		Assert.Equal(visibleStatus, viewModel.StatusContentStatsText);
		Assert.False(pipeline.TryGetCachedPreviewSelectionMetrics(
			PreviewContentMode.Content, document, fullSelection, out _));
	}

	[AvaloniaFact]
	public void FullTreeRefreshDiscardsPreviewCacheWithoutClearingVisibleStatus()
	{
		using var directory = new TemporaryDirectory();
		var filePath = directory.CreateFile("Program.cs", "initial contents");
		var tree = CreateTree(directory.Path, [filePath]);
		var localization = new LocalizationService(new JsonLocalizationCatalog(), AppLanguage.En);
		var viewModel = CreateViewModel(localization, tree.Root);
		using var document = new InMemoryPreviewTextDocument("selection");
		viewModel.PreviewDocument = document;
		using var status = CreateStatus(viewModel);
		using var pipeline = CreatePipeline(
			viewModel, localization, status, tree, new FileContentAnalyzer(),
			() => new HashSet<string>([directory.Path], PathComparer.Default));
		var fullSelection = new PreviewSelectionRange(1, 0, 1, "selection".Length);

		pipeline.UpdateStatusBarMetrics(1, 10, 3, 2, 20, 5);
		Assert.True(pipeline.TryGetCachedPreviewSelectionMetrics(
			PreviewContentMode.Content, document, fullSelection, out _));
		var visibleStatus = viewModel.StatusContentStatsText;

		pipeline.CancelAndDiscardBackgroundCalculation();

		Assert.True(pipeline.HasStatusMetricsSnapshot);
		Assert.Equal(visibleStatus, viewModel.StatusContentStatsText);
		Assert.False(pipeline.TryGetCachedPreviewSelectionMetrics(
			PreviewContentMode.Content, document, fullSelection, out _));
	}

	[AvaloniaFact]
	public async Task TreeOnlyPublicationDoesNotReuseIncompleteCombinedPreviewMetrics()
	{
		using var directory = new TemporaryDirectory();
		var filePath = directory.CreateFile("Program.cs", "initial contents");
		var tree = CreateTree(directory.Path, [filePath]);
		var localization = new LocalizationService(new JsonLocalizationCatalog(), AppLanguage.En);
		var viewModel = CreateViewModel(localization, tree.Root);
		using var document = new InMemoryPreviewTextDocument("selection");
		viewModel.PreviewDocument = document;
		using var status = CreateStatus(viewModel);
		var analyzer = new GatedMetricsAnalyzer(new FileContentAnalyzer());
		var publication = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		using var pipeline = CreatePipeline(
			viewModel, localization, status, tree, analyzer,
			() => new HashSet<string>([directory.Path], PathComparer.Default),
			reason =>
			{
				if (reason == MemoryCleanupReason.FilterApplied)
					publication.TrySetResult();
			});
		var fullSelection = new PreviewSelectionRange(1, 0, 1, "selection".Length);

		await pipeline.InitializeFileMetricsCacheSoonAfterFirstPaintAsync(
			tree, TestContext.Current.CancellationToken);
		await WaitUntilAsync(() => pipeline.HasCompleteBaseline && pipeline.HasStatusMetricsSnapshot);
		Assert.True(pipeline.TryGetCachedPreviewSelectionMetrics(
			PreviewContentMode.TreeAndContent, document, fullSelection, out _));

		analyzer.BlockNextRead();
		await File.WriteAllTextAsync(
			filePath, "updated content with a different length", TestContext.Current.CancellationToken);
		File.SetLastWriteTimeUtc(filePath, DateTime.UtcNow.AddMinutes(1));
		pipeline.Recalculate(MemoryCleanupReason.FilterApplied);
		try
		{
			await analyzer.BlockedReadStarted.WaitAsync(
				TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
			Assert.True(pipeline.HasStatusMetricsSnapshot);
			Assert.False(pipeline.TryGetCachedPreviewSelectionMetrics(
				PreviewContentMode.TreeAndContent, document, fullSelection, out _));
		}
		finally
		{
			analyzer.Release();
		}

		await publication.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
		Assert.True(pipeline.TryGetCachedPreviewSelectionMetrics(
			PreviewContentMode.TreeAndContent, document, fullSelection, out _));
	}

	private static BuildTreeResult CreateTree(string rootPath, IReadOnlyList<string> paths)
	{
		var root = new TreeNodeDescriptor(
			"root", rootPath, true, false, "folder",
			paths.Select(path => new TreeNodeDescriptor(
				Path.GetFileName(path), path, false, false, "csharp", [])).ToArray());
		return new BuildTreeResult(root, false, false, paths);
	}

	private static MainWindowViewModel CreateViewModel(
		LocalizationService localization,
		TreeNodeDescriptor root)
	{
		var viewModel = new MainWindowViewModel(localization, new HelpContentProvider())
		{
			IsProjectLoaded = true
		};
		viewModel.TreeNodes.Add(new TreeNodeViewModel(root, parent: null, icon: null));
		return viewModel;
	}

	private static StatusOperationCoordinator CreateStatus(MainWindowViewModel viewModel) =>
		new(viewModel, () => false, () => viewModel.StatusOperationCalculatingData);

	private static MetricsPipeline CreatePipeline(
		MainWindowViewModel viewModel,
		LocalizationService localization,
		StatusOperationCoordinator status,
		BuildTreeResult tree,
		IFileContentAnalyzer analyzer,
		Func<IReadOnlySet<string>> selectedPaths,
		Action<MemoryCleanupReason>? onPublication = null) =>
		new(viewModel, localization, analyzer, new TreeExportService(), status,
			() => tree, () => tree.Root.FullPath, selectedPaths,
			() => TreeTextFormat.Ascii, () => null, () => 1400,
			scheduleMemoryCleanup: onPublication);

	private static async Task WaitUntilAsync(Func<bool> condition)
	{
		var stopwatch = Stopwatch.StartNew();
		while (!condition())
		{
			if (stopwatch.Elapsed >= TimeSpan.FromSeconds(5))
				throw new TimeoutException("Initial metrics did not publish.");
			await Task.Delay(10, TestContext.Current.CancellationToken);
		}
	}

	private sealed class GatedMetricsAnalyzer(IFileContentAnalyzer inner) : IFileContentAnalyzer
	{
		private readonly TaskCompletionSource _blockedReadStarted = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		private readonly TaskCompletionSource _release = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		private int _blockNextRead;

		public Task BlockedReadStarted => _blockedReadStarted.Task;

		public void BlockNextRead() => Volatile.Write(ref _blockNextRead, 1);
		public void Release() => _release.TrySetResult();

		public FileContentClassification? ClassifyWithoutReading(string path) =>
			inner.ClassifyWithoutReading(path);

		public async ValueTask<FileContentMetricsResult> GetClassifiedMetricsAsync(
			string path,
			CancellationToken cancellationToken = default)
		{
			if (Interlocked.Exchange(ref _blockNextRead, 0) == 1)
			{
				_blockedReadStarted.TrySetResult();
				await _release.Task.WaitAsync(cancellationToken);
			}
			return await inner.GetClassifiedMetricsAsync(path, cancellationToken);
		}

		public ValueTask<bool> IsTextFileAsync(
			string path,
			CancellationToken cancellationToken = default) =>
			inner.IsTextFileAsync(path, cancellationToken);

		public ValueTask<TextFileMetrics?> GetTextFileMetricsAsync(
			string path,
			CancellationToken cancellationToken = default) =>
			inner.GetTextFileMetricsAsync(path, cancellationToken);

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
