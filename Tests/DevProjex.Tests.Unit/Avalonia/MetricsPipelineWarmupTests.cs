using System.Diagnostics;
using Avalonia.Threading;
using DevProjex.Application.Compression;
using DevProjex.Application.Preview;
using DevProjex.Avalonia.Services;

namespace DevProjex.Tests.Unit.Avalonia;

[Collection("AvaloniaUI")]
public sealed class MetricsPipelineWarmupTests
{
    [AvaloniaFact]
    public async Task InitializeFileMetricsCacheAsync_PendingVisualGate_DefersAllFileIoUntilReveal()
    {
        using var temp = new TemporaryDirectory();
        var textFile = temp.CreateFile("Program.cs", "Console.WriteLine(\"ready\");");
        var treeRoot = new TreeNodeDescriptor(
            "root",
            temp.Path,
            true,
            false,
            "folder",
            [new TreeNodeDescriptor("Program.cs", textFile, false, false, "csharp", [])]);
        var currentTree = new BuildTreeResult(
            treeRoot,
            RootAccessDenied: false,
            HadAccessDenied: false,
            [textFile]);
        var viewModel = CreateViewModel();
        viewModel.IsProjectLoaded = true;
        viewModel.TreeNodes.Add(new TreeNodeViewModel(treeRoot, parent: null, icon: null));
        var observedProgressValues = new List<double>();
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainWindowViewModel.StatusProgressValue))
                observedProgressValues.Add(viewModel.StatusProgressValue);
        };
        var analyzer = new CountingFileContentAnalyzer(new FileContentAnalyzer());
        var status = new StatusOperationCoordinator(
            viewModel,
            isBackgroundMetricsActive: () => false,
            metricsOperationTextProvider: () => viewModel.StatusOperationCalculatingData);
        using var pipeline = new MetricsPipeline(
            viewModel,
            CreateLocalization(),
            analyzer,
            new TreeExportService(),
            status,
            currentTreeProvider: () => currentTree,
            currentPathProvider: () => temp.Path,
            selectedPathsProvider: () => new HashSet<string>(PathComparer.Default),
            treeFormatProvider: () => TreeTextFormat.Ascii,
            exportPathPresentationProvider: () => null,
            boundsWidthProvider: () => 1400);
        var visualReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var warmupTask = pipeline.InitializeFileMetricsCacheSoonAfterFirstPaintAsync(
            currentTree,
            visualReady.Task,
            TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.Equal(0, analyzer.GetMetricsCallCount(textFile));
        Assert.False(pipeline.IsBackgroundActive);
        Assert.False(pipeline.HasCompleteBaseline);
        Assert.Equal(0, viewModel.StatusProgressValue);
        Assert.DoesNotContain(observedProgressValues, static value => value > 0);

        visualReady.SetResult();
        await warmupTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(1, analyzer.GetMetricsCallCount(textFile));
        Assert.True(pipeline.HasCompleteBaseline);
        Assert.Contains(100, observedProgressValues);
    }

	[AvaloniaFact]
	public async Task CompressionPrewarm_DoesNotWaitForTheMetricsVisualGate()
	{
		using var temp = new TemporaryDirectory();
		const string source = "internal class Program { void Run() { } }";
		var textFile = temp.CreateFile("Program.cs", source);
		var treeRoot = new TreeNodeDescriptor(
			"root",
			temp.Path,
			true,
			false,
			"folder",
			[new TreeNodeDescriptor("Program.cs", textFile, false, false, "csharp", [])]);
		var currentTree = new BuildTreeResult(treeRoot, false, false, [textFile]);
		var viewModel = CreateViewModel();
		viewModel.IsProjectLoaded = true;
		viewModel.TreeNodes.Add(new TreeNodeViewModel(treeRoot, parent: null, icon: null));
		var observedProgressValues = new List<double>();
		viewModel.PropertyChanged += (_, args) =>
		{
			if (args.PropertyName == nameof(MainWindowViewModel.StatusProgressValue))
				observedProgressValues.Add(viewModel.StatusProgressValue);
		};
		var status = new StatusOperationCoordinator(
			viewModel,
			isBackgroundMetricsActive: () => false,
			metricsOperationTextProvider: () => viewModel.StatusOperationCalculatingData);
		using var compressor = new CountingCodeCompressor();
		using var compressionSession = new CodeCompressionSession(compressor);
		var transformationContext = ContentTransformationContext.For(
			new CodeCompressionContext(temp.Path, compressionSession),
			redaction: null);
		using var pipeline = new MetricsPipeline(
			viewModel,
			CreateLocalization(),
			new FileContentAnalyzer(),
			new TreeExportService(),
			status,
			currentTreeProvider: () => currentTree,
			currentPathProvider: () => temp.Path,
			selectedPathsProvider: () => new HashSet<string>(PathComparer.Default),
			treeFormatProvider: () => TreeTextFormat.Ascii,
			exportPathPresentationProvider: () => null,
			boundsWidthProvider: () => 1400,
			transformationContextProvider: () => transformationContext);
		var visualReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

		var delayedMetrics = pipeline.InitializeFileMetricsCacheSoonAfterFirstPaintAsync(
			currentTree,
			visualReady.Task,
			TestContext.Current.CancellationToken);
		await pipeline.PrewarmCompressionAsync(currentTree, TestContext.Current.CancellationToken);

		Assert.Equal(1, compressor.AnalysisCount);
		Assert.Contains(observedProgressValues, static value => value == 100);
		var tokens = CodeCompressionSnapshot.EstimateTokens(source.Length);
		Assert.Equal(
			$"Compressed 0 of 1 files.{Environment.NewLine}≈Tokens: {tokens} → {tokens}.",
			viewModel.SettingsCompressionNotice);
		Assert.False(viewModel.StatusBusy);
		Assert.False(delayedMetrics.IsCompleted);
		Assert.False(pipeline.HasCompleteBaseline);

		visualReady.SetResult();
		await delayedMetrics.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

		Assert.Equal(1, compressor.AnalysisCount);
		Assert.True(compressionSession.Diagnostics.CacheHits > 0);
		Assert.True(pipeline.HasCompleteBaseline);
	}

	[AvaloniaFact]
	public async Task CompressionPrewarm_LongOperationPublishesMeasuredProgressAndCompletion()
	{
		using var temp = new TemporaryDirectory();
		const string firstSource = "internal class First { void Run() { } }";
		const string secondSource = "internal class Second { void Run() { } }";
		var firstFile = temp.CreateFile("First.cs", firstSource);
		var secondFile = temp.CreateFile("Second.cs", secondSource);
		var treeRoot = new TreeNodeDescriptor(
			"root",
			temp.Path,
			true,
			false,
			"folder",
			[
				new TreeNodeDescriptor("First.cs", firstFile, false, false, "csharp", []),
				new TreeNodeDescriptor("Second.cs", secondFile, false, false, "csharp", [])
			]);
		var currentTree = new BuildTreeResult(treeRoot, false, false, [firstFile, secondFile]);
		var viewModel = CreateViewModel();
		var status = new StatusOperationCoordinator(
			viewModel,
			isBackgroundMetricsActive: () => false,
			metricsOperationTextProvider: () => viewModel.StatusOperationCalculatingData,
			extendedDelayedPresentationThreshold: TimeSpan.Zero);
		using var compressor = new GateSecondAnalysisCompressor();
		using var compressionSession = new CodeCompressionSession(compressor);
		using var pipeline = new MetricsPipeline(
			viewModel,
			CreateLocalization(),
			new FileContentAnalyzer(),
			new TreeExportService(),
			status,
			currentTreeProvider: () => currentTree,
			currentPathProvider: () => temp.Path,
			selectedPathsProvider: () => new HashSet<string>(PathComparer.Default),
			treeFormatProvider: () => TreeTextFormat.Ascii,
			exportPathPresentationProvider: () => null,
			boundsWidthProvider: () => 1400,
			transformationContextProvider: () => ContentTransformationContext.For(
				new CodeCompressionContext(temp.Path, compressionSession),
				redaction: null));

		var warmupTask = pipeline.PrewarmCompressionAsync(
			currentTree,
			TestContext.Current.CancellationToken);
		await compressor.SecondAnalysisStarted.Task.WaitAsync(
			TimeSpan.FromSeconds(5),
			TestContext.Current.CancellationToken);
		await WaitUntilAsync(
			() => viewModel.StatusProgressValue == 50 &&
			      viewModel.StatusOperationVisible,
			TimeSpan.FromSeconds(5));

		Assert.True(viewModel.StatusOperationVisible);
		Assert.Equal("Compressing code…", viewModel.StatusOperationText);
		Assert.False(viewModel.StatusProgressIsIndeterminate);
		Assert.Equal(50, viewModel.StatusProgressValue);
		Assert.Equal("Compressing code…", viewModel.SettingsCompressionNotice);

		compressor.ReleaseSecondAnalysis();
		await warmupTask.WaitAsync(
			TimeSpan.FromSeconds(5),
			TestContext.Current.CancellationToken);

		Assert.False(viewModel.StatusBusy);
		var tokens = CodeCompressionSnapshot.EstimateTokens(
			firstSource.Length + secondSource.Length);
		Assert.Equal(
			$"Compressed 0 of 2 files.{Environment.NewLine}≈Tokens: {tokens} → {tokens}.",
			viewModel.SettingsCompressionNotice);
	}

	[AvaloniaFact]
	public async Task PostLoadMetrics_ReusesThePrewarmReadFactWithoutReopeningTheFile()
	{
		using var temp = new TemporaryDirectory();
		var textFile = temp.CreateFile(
			"Program.cs",
			"internal class Program { void Run() { Console.WriteLine(1); } }");
		var treeRoot = new TreeNodeDescriptor(
			"root",
			temp.Path,
			true,
			false,
			"folder",
			[new TreeNodeDescriptor("Program.cs", textFile, false, false, "csharp", [])]);
		var currentTree = new BuildTreeResult(treeRoot, false, false, [textFile]);
		var viewModel = CreateViewModel();
		viewModel.IsProjectLoaded = true;
		viewModel.TreeNodes.Add(new TreeNodeViewModel(treeRoot, parent: null, icon: null));
		var analyzer = new CountingFileContentAnalyzer(new FileContentAnalyzer());
		var status = new StatusOperationCoordinator(
			viewModel,
			isBackgroundMetricsActive: () => false,
			metricsOperationTextProvider: () => viewModel.StatusOperationCalculatingData);
		using var compressor = new CountingCodeCompressor();
		using var compressionSession = new CodeCompressionSession(compressor);
		var transformation = ContentTransformationContext.For(
			new CodeCompressionContext(temp.Path, compressionSession),
			redaction: null);
		using var pipeline = new MetricsPipeline(
			viewModel,
			CreateLocalization(),
			analyzer,
			new TreeExportService(),
			status,
			currentTreeProvider: () => currentTree,
			currentPathProvider: () => temp.Path,
			selectedPathsProvider: () => new HashSet<string>(PathComparer.Default),
			treeFormatProvider: () => TreeTextFormat.Ascii,
			exportPathPresentationProvider: () => null,
			boundsWidthProvider: () => 1400,
			transformationContextProvider: () => transformation);

		await pipeline.PrewarmCompressionAsync(currentTree, TestContext.Current.CancellationToken);
		Assert.Equal(1, analyzer.GetClassifiedReadCallCount(textFile));

		await pipeline.InitializeFileMetricsCacheSoonAfterFirstPaintAsync(
			currentTree,
			TestContext.Current.CancellationToken);

		Assert.Equal(1, analyzer.GetClassifiedReadCallCount(textFile));
		Assert.Equal(0, analyzer.GetMetricsCallCount(textFile));
		Assert.True(pipeline.HasCompleteBaseline);
	}

	[AvaloniaFact]
	public async Task CompressionPrewarm_AnalyzesOnlyTheEffectiveSelection()
	{
		using var temp = new TemporaryDirectory();
		var selectedFile = temp.CreateFile("Selected.cs", "internal class Selected { }");
		var otherFile = temp.CreateFile("Other.cs", "internal class Other { }");
		var treeRoot = new TreeNodeDescriptor(
			"root",
			temp.Path,
			true,
			false,
			"folder",
			[
				new TreeNodeDescriptor("Other.cs", otherFile, false, false, "csharp", []),
				new TreeNodeDescriptor("Selected.cs", selectedFile, false, false, "csharp", [])
			]);
		var currentTree = new BuildTreeResult(treeRoot, false, false, [otherFile, selectedFile]);
		var viewModel = CreateViewModel();
		var status = new StatusOperationCoordinator(
			viewModel,
			isBackgroundMetricsActive: () => false,
			metricsOperationTextProvider: () => viewModel.StatusOperationCalculatingData);
		using var compressor = new CountingCodeCompressor();
		using var compressionSession = new CodeCompressionSession(compressor);
		using var pipeline = new MetricsPipeline(
			viewModel,
			CreateLocalization(),
			new FileContentAnalyzer(),
			new TreeExportService(),
			status,
			currentTreeProvider: () => currentTree,
			currentPathProvider: () => temp.Path,
			selectedPathsProvider: () => new HashSet<string>([selectedFile], PathComparer.Default),
			treeFormatProvider: () => TreeTextFormat.Ascii,
			exportPathPresentationProvider: () => null,
			boundsWidthProvider: () => 1400,
			transformationContextProvider: () => ContentTransformationContext.For(
				new CodeCompressionContext(temp.Path, compressionSession),
				redaction: null));

		await pipeline.PrewarmCompressionAsync(
			currentTree,
			TestContext.Current.CancellationToken);

		Assert.Equal(1, compressor.AnalysisCount);
		Assert.Equal(1, compressionSession.Diagnostics.PrewarmRequests);
	}

	[AvaloniaFact]
	public async Task CompressionMetrics_ReadOnceAndReuseRawAndTransformedVariantsAcrossToggles()
	{
		using var temp = new TemporaryDirectory();
		var textFile = temp.CreateFile(
			"Program.cs",
			"internal class Program { void Run() { Console.WriteLine(1); } }");
		var treeRoot = new TreeNodeDescriptor(
			"root",
			temp.Path,
			true,
			false,
			"folder",
			[new TreeNodeDescriptor("Program.cs", textFile, false, false, "csharp", [])]);
		var currentTree = new BuildTreeResult(treeRoot, false, false, [textFile]);
		var viewModel = CreateViewModel();
		viewModel.IsProjectLoaded = true;
		viewModel.TreeNodes.Add(new TreeNodeViewModel(treeRoot, parent: null, icon: null));
		var analyzer = new CountingFileContentAnalyzer(new FileContentAnalyzer());
		var status = new StatusOperationCoordinator(
			viewModel,
			isBackgroundMetricsActive: () => false,
			metricsOperationTextProvider: () => viewModel.StatusOperationCalculatingData);
		using var compressor = new CountingCodeCompressor();
		using var compressionSession = new CodeCompressionSession(compressor);
		var compression = ContentTransformationContext.For(
			new CodeCompressionContext(temp.Path, compressionSession),
			redaction: null);
		ContentTransformationContext? currentTransformation = null;
		var completedRecalculations = 0;
		using var pipeline = new MetricsPipeline(
			viewModel,
			CreateLocalization(),
			analyzer,
			new TreeExportService(),
			status,
			currentTreeProvider: () => currentTree,
			currentPathProvider: () => temp.Path,
			selectedPathsProvider: () => new HashSet<string>(PathComparer.Default),
			treeFormatProvider: () => TreeTextFormat.Ascii,
			exportPathPresentationProvider: () => null,
			boundsWidthProvider: () => 1400,
			scheduleMemoryCleanup: _ => Interlocked.Increment(ref completedRecalculations),
			transformationContextProvider: () => currentTransformation);

		await pipeline.InitializeFileMetricsCacheSoonAfterFirstPaintAsync(
			currentTree,
			TestContext.Current.CancellationToken);
		Assert.Equal(1, analyzer.GetMetricsCallCount(textFile));
		Assert.Equal(0, analyzer.GetClassifiedReadCallCount(textFile));

		currentTransformation = compression;
		pipeline.HasCompleteBaseline = false;
		pipeline.Recalculate(MemoryCleanupReason.FilterApplied);
		await WaitUntilAsync(
			() => Volatile.Read(ref completedRecalculations) == 1,
			TimeSpan.FromSeconds(5));
		Assert.Equal(1, analyzer.GetClassifiedReadCallCount(textFile));
		Assert.Equal(1, analyzer.GetMetricsCallCount(textFile));

		currentTransformation = null;
		pipeline.HasCompleteBaseline = false;
		pipeline.Recalculate(MemoryCleanupReason.FilterApplied);
		await WaitUntilAsync(
			() => Volatile.Read(ref completedRecalculations) == 2,
			TimeSpan.FromSeconds(5));

		currentTransformation = compression;
		pipeline.HasCompleteBaseline = false;
		pipeline.Recalculate(MemoryCleanupReason.FilterApplied);
		await WaitUntilAsync(
			() => Volatile.Read(ref completedRecalculations) == 3,
			TimeSpan.FromSeconds(5));

		Assert.Equal(1, analyzer.GetMetricsCallCount(textFile));
		Assert.Equal(1, analyzer.GetClassifiedReadCallCount(textFile));
		Assert.Equal(1, compressor.AnalysisCount);
	}

    [AvaloniaFact]
    public async Task InitializeFileMetricsCacheAsync_InvalidatedWhileWaitingNeverReadsObsoleteTree()
    {
        using var temp = new TemporaryDirectory();
        var textFile = temp.CreateFile(
            "Program.cs",
            "Console.WriteLine(\"obsolete\");");
        var treeRoot = new TreeNodeDescriptor(
            "root",
            temp.Path,
            true,
            false,
            "folder",
            [
                new TreeNodeDescriptor(
                    "Program.cs",
                    textFile,
                    false,
                    false,
                    "csharp",
                    [])
            ]);
        var currentTree = new BuildTreeResult(
            treeRoot,
            RootAccessDenied: false,
            HadAccessDenied: false,
            [textFile]);
        var viewModel = CreateViewModel();
        viewModel.IsProjectLoaded = true;
        viewModel.TreeNodes.Add(
            new TreeNodeViewModel(treeRoot, parent: null, icon: null));
        var analyzer = new CountingFileContentAnalyzer(
            new FileContentAnalyzer());
        var status = new StatusOperationCoordinator(
            viewModel,
            isBackgroundMetricsActive: () => false,
            metricsOperationTextProvider:
                () => viewModel.StatusOperationCalculatingData);
        using var pipeline = new MetricsPipeline(
            viewModel,
            CreateLocalization(),
            analyzer,
            new TreeExportService(),
            status,
            currentTreeProvider: () => currentTree,
            currentPathProvider: () => temp.Path,
            selectedPathsProvider:
                () => new HashSet<string>(PathComparer.Default),
            treeFormatProvider: () => TreeTextFormat.Ascii,
            exportPathPresentationProvider: () => null,
            boundsWidthProvider: () => 1400);
        var visualReady =
            new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);

        var warmup = pipeline.InitializeFileMetricsCacheSoonAfterFirstPaintAsync(
            currentTree,
            visualReady.Task,
            TestContext.Current.CancellationToken);
        await Task.Delay(
            100,
            TestContext.Current.CancellationToken);

        pipeline.CancelAndDiscardBackgroundCalculation();
        visualReady.SetResult();
        await warmup.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, analyzer.GetMetricsCallCount(textFile));
        Assert.False(pipeline.IsBackgroundActive);
        Assert.False(pipeline.HasCompleteBaseline);
    }

    [AvaloniaFact]
    public async Task InitializeFileMetricsCacheAsync_MixedFiles_InspectsEachTextFileOnceAndPublishesRenderedMetrics()
    {
        using var temp = new TemporaryDirectory();
        var textFile = temp.CreateFile("src/Program.cs", "line 1\r\nline 2");
        var emptyFile = temp.CreateFile("src/empty.txt", string.Empty);
        var binaryFile = temp.CreateBinaryFile("assets/image.png", [0x89, 0x50, 0x4E, 0x47, 0x00]);
        var deletedFile = Path.Combine(temp.Path, "src", "deleted.cs");
        var orderedPaths = new[] { binaryFile, deletedFile, emptyFile, textFile }
            .OrderBy(static path => path, PathComparer.Default)
            .ToArray();
        var treeRoot = CreateTree(temp.Path, textFile, emptyFile, binaryFile, deletedFile);
        var currentTree = new BuildTreeResult(
            treeRoot,
            RootAccessDenied: false,
            HadAccessDenied: false,
            orderedPaths);

        var viewModel = CreateViewModel();
        viewModel.IsProjectLoaded = true;
        viewModel.SelectedPreviewContentMode = PreviewContentMode.Content;
        viewModel.TreeNodes.Add(new TreeNodeViewModel(treeRoot, parent: null, icon: null));

        var analyzer = new CountingFileContentAnalyzer(new FileContentAnalyzer());
        var status = new StatusOperationCoordinator(
            viewModel,
            isBackgroundMetricsActive: () => false,
            metricsOperationTextProvider: () => viewModel.StatusOperationCalculatingData);
        var pipeline = new MetricsPipeline(
            viewModel,
            CreateLocalization(),
            analyzer,
            new TreeExportService(),
            status,
            currentTreeProvider: () => currentTree,
            currentPathProvider: () => temp.Path,
            selectedPathsProvider: () => new HashSet<string>(PathComparer.Default),
            treeFormatProvider: () => TreeTextFormat.Ascii,
            exportPathPresentationProvider: () => null,
            boundsWidthProvider: () => 1400);

        try
        {
            await pipeline.InitializeFileMetricsCacheSoonAfterFirstPaintAsync(
                currentTree,
                TestContext.Current.CancellationToken);
            await WaitUntilAsync(
                () => pipeline.HasStatusMetricsSnapshot,
                TimeSpan.FromSeconds(5));

            var renderedContent = await new SelectedContentExportService(new FileContentAnalyzer())
                .BuildAsync(orderedPaths, TestContext.Current.CancellationToken);
            var expectedMetrics = ExportOutputMetricsCalculator.FromText(renderedContent);
            using var document = new InMemoryPreviewTextDocument("x");

            Assert.True(pipeline.HasCompleteBaseline);
            Assert.Equal(1, analyzer.GetMetricsCallCount(textFile));
            Assert.Equal(1, analyzer.GetMetricsCallCount(emptyFile));
            Assert.Equal(1, analyzer.GetMetricsCallCount(deletedFile));
            Assert.Equal(0, analyzer.GetMetricsCallCount(binaryFile));
            Assert.True(pipeline.TryGetCachedPreviewSelectionMetrics(
                PreviewContentMode.Content,
                document,
                new PreviewSelectionRange(1, 0, 1, 1),
                out var actualMetrics));
            Assert.Equal(expectedMetrics, actualMetrics);
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(pipeline.Dispose);
        }
    }

    [AvaloniaFact]
    public async Task Recalculate_FilterCleanupIsScheduledAfterCurrentMetricsFinish()
    {
        using var temp = new TemporaryDirectory();
        var textFile = temp.CreateFile(
            "src/Program.cs",
            "Console.WriteLine(\"ready\");");
        var treeRoot = new TreeNodeDescriptor(
            "root",
            temp.Path,
            true,
            false,
            "folder",
            [
                new TreeNodeDescriptor(
                    "Program.cs",
                    textFile,
                    false,
                    false,
                    "csharp",
                    [])
            ]);
        var currentTree = new BuildTreeResult(
            treeRoot,
            RootAccessDenied: false,
            HadAccessDenied: false,
            [textFile]);
        var viewModel = CreateViewModel();
        viewModel.IsProjectLoaded = true;
        viewModel.TreeNodes.Add(
            new TreeNodeViewModel(treeRoot, parent: null, icon: null));
        var status = new StatusOperationCoordinator(
            viewModel,
            isBackgroundMetricsActive: () => false,
            metricsOperationTextProvider:
                () => viewModel.StatusOperationCalculatingData);
        var analyzer = new BlockingMetricsFileContentAnalyzer(
            new FileContentAnalyzer());
        var cleanupScheduled =
            new TaskCompletionSource<MemoryCleanupReason>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        using var pipeline = new MetricsPipeline(
            viewModel,
            CreateLocalization(),
            analyzer,
            new TreeExportService(),
            status,
            currentTreeProvider: () => currentTree,
            currentPathProvider: () => temp.Path,
            selectedPathsProvider:
                () => new HashSet<string>(PathComparer.Default),
            treeFormatProvider: () => TreeTextFormat.Ascii,
            exportPathPresentationProvider: () => null,
            boundsWidthProvider: () => 1400,
            scheduleMemoryCleanup:
                reason => cleanupScheduled.TrySetResult(reason));

        pipeline.Recalculate(MemoryCleanupReason.FilterApplied);
        await analyzer.Started.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        Assert.False(cleanupScheduled.Task.IsCompleted);

        analyzer.Release();
        var reason = await cleanupScheduled.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        Assert.Equal(MemoryCleanupReason.FilterApplied, reason);
        Assert.True(pipeline.HasStatusMetricsSnapshot);
    }

    [AvaloniaFact]
    public async Task CancelAndDiscard_ObsoleteWarmupCannotRepopulateMetricsCache()
    {
        using var temp = new TemporaryDirectory();
        var textFile = temp.CreateFile("Program.cs", "class Program { }");
        var treeRoot = new TreeNodeDescriptor(
            "root",
            temp.Path,
            true,
            false,
            "folder",
            [
                new TreeNodeDescriptor(
                    "Program.cs",
                    textFile,
                    false,
                    false,
                    "csharp",
                    [])
            ]);
        var currentTree = new BuildTreeResult(
            treeRoot,
            RootAccessDenied: false,
            HadAccessDenied: false,
            [textFile]);
        var viewModel = CreateViewModel();
        viewModel.IsProjectLoaded = true;
        viewModel.TreeNodes.Add(
            new TreeNodeViewModel(treeRoot, parent: null, icon: null));
        var status = new StatusOperationCoordinator(
            viewModel,
            isBackgroundMetricsActive: () => false,
            metricsOperationTextProvider:
                () => viewModel.StatusOperationCalculatingData);
        var analyzer = new FirstCallBlockingMetricsAnalyzer(
            new FileContentAnalyzer());
        using var pipeline = new MetricsPipeline(
            viewModel,
            CreateLocalization(),
            analyzer,
            new TreeExportService(),
            status,
            currentTreeProvider: () => currentTree,
            currentPathProvider: () => temp.Path,
            selectedPathsProvider:
                () => new HashSet<string>(PathComparer.Default),
            treeFormatProvider: () => TreeTextFormat.Ascii,
            exportPathPresentationProvider: () => null,
            boundsWidthProvider: () => 1400);

        var warmup = pipeline.InitializeFileMetricsCacheSoonAfterFirstPaintAsync(
            currentTree,
            TestContext.Current.CancellationToken);
        await analyzer.FirstCallStarted.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        pipeline.CancelAndDiscardBackgroundCalculation();
        analyzer.ReleaseFirstCall();
        await warmup.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, analyzer.MetricsCallCount);

        pipeline.Recalculate();
        await WaitUntilAsync(
            () => analyzer.MetricsCallCount == 2 &&
                  pipeline.HasStatusMetricsSnapshot,
            TimeSpan.FromSeconds(5));

        Assert.Equal(2, analyzer.MetricsCallCount);
    }

    private static TreeNodeDescriptor CreateTree(
        string rootPath,
        string textFile,
        string emptyFile,
        string binaryFile,
        string deletedFile)
    {
        return new TreeNodeDescriptor(
            "root",
            rootPath,
            true,
            false,
            "folder",
            [
                new TreeNodeDescriptor(
                    "assets",
                    Path.Combine(rootPath, "assets"),
                    true,
                    false,
                    "folder",
                    [
                        new TreeNodeDescriptor("image.png", binaryFile, false, false, "image", [])
                    ]),
                new TreeNodeDescriptor(
                    "src",
                    Path.Combine(rootPath, "src"),
                    true,
                    false,
                    "folder",
                    [
                        new TreeNodeDescriptor("deleted.cs", deletedFile, false, false, "csharp", []),
                        new TreeNodeDescriptor("empty.txt", emptyFile, false, false, "text", []),
                        new TreeNodeDescriptor("Program.cs", textFile, false, false, "csharp", [])
                    ])
            ]);
    }

    private static MainWindowViewModel CreateViewModel() =>
        new(CreateLocalization(), new HelpContentProvider());

    private static LocalizationService CreateLocalization()
    {
        IReadOnlyDictionary<string, string> english = new Dictionary<string, string>
        {
            ["Status.Operation.CalculatingData"] = "Calculating data",
            ["Settings.Compression.Status.Scanning"] = "Compressing code…",
            ["Settings.Compression.Status.Applied"] =
                "Compressed {0} of {1} files. ≈Tokens: {2} → {3}.",
            ["Settings.Compression.Status.NothingToCompress"] =
                "Nothing to compress in this selection.",
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
                throw new TimeoutException("Metrics pipeline did not publish its snapshot in time.");

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    private sealed class CountingFileContentAnalyzer(IFileContentAnalyzer inner) : IFileContentAnalyzer
    {
        private readonly Dictionary<string, int> _metricsCalls = new(PathComparer.Default);
        private readonly Dictionary<string, int> _classifiedReadCalls = new(PathComparer.Default);
        private readonly object _sync = new();

        public int GetMetricsCallCount(string path)
        {
            lock (_sync)
                return _metricsCalls.GetValueOrDefault(path);
        }

		public int GetClassifiedReadCallCount(string path)
		{
			lock (_sync)
				return _classifiedReadCalls.GetValueOrDefault(path);
		}

		public ValueTask<FileContentReadResult> ReadClassifiedAsync(
			string path,
			long maxSizeForFullRead,
			CancellationToken cancellationToken = default)
		{
			lock (_sync)
				_classifiedReadCalls[path] = _classifiedReadCalls.GetValueOrDefault(path) + 1;
			return inner.ReadClassifiedAsync(path, maxSizeForFullRead, cancellationToken);
		}

        public FileContentClassification? ClassifyWithoutReading(string path) =>
            inner.ClassifyWithoutReading(path);

        public ValueTask<bool> IsTextFileAsync(string path, CancellationToken cancellationToken = default) =>
            inner.IsTextFileAsync(path, cancellationToken);

        public ValueTask<TextFileMetrics?> GetTextFileMetricsAsync(
            string path,
            CancellationToken cancellationToken = default)
        {
            lock (_sync)
                _metricsCalls[path] = _metricsCalls.GetValueOrDefault(path) + 1;

            return inner.GetTextFileMetricsAsync(path, cancellationToken);
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

    private sealed class BlockingMetricsFileContentAnalyzer(
        IFileContentAnalyzer inner) : IFileContentAnalyzer
    {
        private readonly TaskCompletionSource _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;

        public void Release() => _release.TrySetResult();

        public FileContentClassification? ClassifyWithoutReading(string path) =>
            inner.ClassifyWithoutReading(path);

        public ValueTask<bool> IsTextFileAsync(
            string path,
            CancellationToken cancellationToken = default) =>
            inner.IsTextFileAsync(path, cancellationToken);

        public async ValueTask<TextFileMetrics?> GetTextFileMetricsAsync(
            string path,
            CancellationToken cancellationToken = default)
        {
            _started.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            return await inner.GetTextFileMetricsAsync(
                path,
                cancellationToken);
        }

        public ValueTask<TextFileContent?> TryReadAsTextAsync(
            string path,
            CancellationToken cancellationToken = default) =>
            inner.TryReadAsTextAsync(path, cancellationToken);

        public ValueTask<TextFileContent?> TryReadAsTextAsync(
            string path,
            long maxSizeForFullRead,
            CancellationToken cancellationToken = default) =>
            inner.TryReadAsTextAsync(
                path,
                maxSizeForFullRead,
                cancellationToken);
    }

    private sealed class FirstCallBlockingMetricsAnalyzer(
        IFileContentAnalyzer inner) : IFileContentAnalyzer
    {
        private readonly TaskCompletionSource _firstCallStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseFirstCall =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _metricsCallCount;

        public Task FirstCallStarted => _firstCallStarted.Task;

        public int MetricsCallCount =>
            Volatile.Read(ref _metricsCallCount);

        public void ReleaseFirstCall() =>
            _releaseFirstCall.TrySetResult();

        public FileContentClassification? ClassifyWithoutReading(string path) =>
            inner.ClassifyWithoutReading(path);

        public ValueTask<bool> IsTextFileAsync(
            string path,
            CancellationToken cancellationToken = default) =>
            inner.IsTextFileAsync(path, cancellationToken);

        public async ValueTask<TextFileMetrics?> GetTextFileMetricsAsync(
            string path,
            CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _metricsCallCount);
            if (call == 1)
            {
                _firstCallStarted.TrySetResult();
                await _releaseFirstCall.Task;
                return await inner.GetTextFileMetricsAsync(
                    path,
                    CancellationToken.None);
            }

            return await inner.GetTextFileMetricsAsync(
                path,
                cancellationToken);
        }

        public ValueTask<TextFileContent?> TryReadAsTextAsync(
            string path,
            CancellationToken cancellationToken = default) =>
            inner.TryReadAsTextAsync(path, cancellationToken);

        public ValueTask<TextFileContent?> TryReadAsTextAsync(
            string path,
            long maxSizeForFullRead,
            CancellationToken cancellationToken = default) =>
            inner.TryReadAsTextAsync(
                path,
                maxSizeForFullRead,
                cancellationToken);
    }

	private sealed class CountingCodeCompressor : ICodeCompressor, IDisposable
	{
		private int _analysisCount;

		public string TransformIdentity => "metrics-prewarm:v1";
		public int AnalysisCount => Volatile.Read(ref _analysisCount);
		public bool IsSupported(string relativePath) =>
			Path.GetExtension(relativePath).Equals(".cs", StringComparison.OrdinalIgnoreCase);
		public ICodeCompressionScope CreateScope(string projectRoot) => new Scope(this);
		public void Dispose()
		{
		}

		private sealed class Scope(CountingCodeCompressor owner) : ICodeCompressionScope
		{
			public CodeCompressionAnalysis Analyze(
				string fullPath,
				string relativePath,
				string content,
				CancellationToken cancellationToken)
			{
				Interlocked.Increment(ref owner._analysisCount);
				return new CodeCompressionAnalysis(
					CodeCompressionPlan.Unchanged(
						relativePath,
						"csharp",
						CodeCompressionOutcome.UnchangedNoBenefit,
						content.Length,
						owner.TransformIdentity),
					null);
			}

			public void Dispose()
			{
			}
		}
	}

	private sealed class GateSecondAnalysisCompressor : ICodeCompressor, IDisposable
	{
		private readonly ManualResetEventSlim _secondAnalysisRelease = new(false);
		private int _analysisCount;

		public string TransformIdentity => "metrics-prewarm-gated:v1";
		public TaskCompletionSource SecondAnalysisStarted { get; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);

		public bool IsSupported(string relativePath) =>
			Path.GetExtension(relativePath).Equals(".cs", StringComparison.OrdinalIgnoreCase);

		public ICodeCompressionScope CreateScope(string projectRoot) => new Scope(this);

		public void ReleaseSecondAnalysis() => _secondAnalysisRelease.Set();

		public void Dispose() => _secondAnalysisRelease.Dispose();

		private sealed class Scope(GateSecondAnalysisCompressor owner) : ICodeCompressionScope
		{
			public CodeCompressionAnalysis Analyze(
				string fullPath,
				string relativePath,
				string content,
				CancellationToken cancellationToken)
			{
				if (Interlocked.Increment(ref owner._analysisCount) == 2)
				{
					owner.SecondAnalysisStarted.TrySetResult();
					owner._secondAnalysisRelease.Wait(cancellationToken);
				}

				return new CodeCompressionAnalysis(
					CodeCompressionPlan.Unchanged(
						relativePath,
						"csharp",
						CodeCompressionOutcome.UnchangedNoBenefit,
						content.Length,
						owner.TransformIdentity),
					null);
			}

			public void Dispose()
			{
			}
		}
	}
}
