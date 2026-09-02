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
	public async Task EmptyContentSelectionMetricsMatchRootOnlyDocument()
	{
		using var temp = new TemporaryDirectory();
		var root = new TreeNodeDescriptor("root", temp.Path, true, false, "folder", []);
		var currentTree = new BuildTreeResult(root, false, false, []);
		var viewModel = CreateViewModel();
		viewModel.IsProjectLoaded = true;
		viewModel.SelectedPreviewContentMode = PreviewContentMode.Content;
		viewModel.TreeNodes.Add(new TreeNodeViewModel(root, parent: null, icon: null));
		using var pipeline = CreateMetricsPipeline(
			viewModel,
			currentTree,
			temp.Path,
			new FileContentAnalyzer());

		await pipeline.InitializeFileMetricsCacheSoonAfterFirstPaintAsync(
			currentTree,
			TestContext.Current.CancellationToken);
		await WaitUntilAsync(() => pipeline.HasStatusMetricsSnapshot, TimeSpan.FromSeconds(5));
		var rendered = ContextRootPresentation.FormatLine(temp.Path);
		using var document = new InMemoryPreviewTextDocument(rendered);

		Assert.True(pipeline.TryGetCachedPreviewSelectionMetrics(
			PreviewContentMode.Content,
			document,
			new PreviewSelectionRange(1, 0, 1, rendered.Length),
			out var metrics));
		Assert.Equal(ExportOutputMetricsCalculator.FromText(rendered), metrics);
	}

	[AvaloniaFact]
	public void ScheduleRecalculate_AfterDispose_DoesNotRecreateDebounceTimer()
	{
		using var temp = new TemporaryDirectory();
		var root = new TreeNodeDescriptor(
			"root",
			temp.Path,
			true,
			false,
			"folder",
			[]);
		var viewModel = CreateViewModel();
		var pipeline = CreateMetricsPipeline(
			viewModel,
			new BuildTreeResult(root, false, false, []),
			temp.Path,
			new FileContentAnalyzer());

		pipeline.Dispose();
		pipeline.ScheduleRecalculate();

		var timer = typeof(MetricsPipeline).GetField(
			"_metricsDebounceTimer",
			BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(pipeline);
		Assert.Null(timer);
	}

	[AvaloniaFact]
	public async Task Recalculate_ProjectsSelectedPathsOncePerGeneration()
	{
		using var temp = new TemporaryDirectory();
		var selectedFile = temp.CreateFile("Selected.cs", "internal class Selected { }");
		var otherFile = temp.CreateFile("Other.cs", "internal class Other { }");
		var root = CreateTree(temp.Path, [selectedFile, otherFile]);
		var currentTree = new BuildTreeResult(root, false, false, [selectedFile, otherFile]);
		var selectedPaths = new CountingReadOnlySet<string>(
			new HashSet<string>([selectedFile], PathComparer.Default));
		var viewModel = CreateViewModel();
		viewModel.IsProjectLoaded = true;
		viewModel.TreeNodes.Add(new TreeNodeViewModel(root, parent: null, icon: null));
		var completedRecalculations = 0;
		using var pipeline = new MetricsPipeline(
			viewModel,
			CreateLocalization(),
			new FileContentAnalyzer(),
			new TreeExportService(),
			new StatusOperationCoordinator(
				viewModel,
				isBackgroundMetricsActive: () => false,
				metricsOperationTextProvider: () => viewModel.StatusOperationCalculatingData),
			currentTreeProvider: () => currentTree,
			currentPathProvider: () => temp.Path,
			selectedPathsProvider: () => selectedPaths,
			treeFormatProvider: () => TreeTextFormat.Ascii,
			exportPathPresentationProvider: () => null,
			boundsWidthProvider: () => 1400,
			scheduleMemoryCleanup: _ => Interlocked.Increment(ref completedRecalculations));

		await pipeline.InitializeFileMetricsCacheSoonAfterFirstPaintAsync(
			currentTree,
			TestContext.Current.CancellationToken);
		await WaitUntilAsync(
			() => pipeline.HasStatusMetricsSnapshot,
			TimeSpan.FromSeconds(5));
		selectedPaths.ResetEnumerationCount();

		pipeline.Recalculate(MemoryCleanupReason.FilterApplied);
		await WaitUntilAsync(
			() => Volatile.Read(ref completedRecalculations) == 1,
			TimeSpan.FromSeconds(5));

		Assert.Equal(1, selectedPaths.EnumerationCount);
	}

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
		await pipeline.PrewarmCompressionAsync(
			currentTree,
			TestContext.Current.CancellationToken,
			retainReadFactsForNextMetricsPass: true);

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
	public async Task CompressionPrewarm_CompletedStandalonePassSchedulesApplyCleanupOnce()
	{
		using var temp = new TemporaryDirectory();
		var textFile = temp.CreateFile("Program.cs", "internal class Program { void Run() { } }");
		var treeRoot = new TreeNodeDescriptor(
			"root",
			temp.Path,
			true,
			false,
			"folder",
			[new TreeNodeDescriptor("Program.cs", textFile, false, false, "csharp", [])]);
		var currentTree = new BuildTreeResult(treeRoot, false, false, [textFile]);
		var viewModel = CreateViewModel();
		var status = new StatusOperationCoordinator(
			viewModel,
			isBackgroundMetricsActive: () => false,
			metricsOperationTextProvider: () => viewModel.StatusOperationCalculatingData);
		using var compressor = new CountingCodeCompressor();
		using var compressionSession = new CodeCompressionSession(compressor);
		var scheduled = new List<MemoryCleanupReason>();
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
			scheduleMemoryCleanup: scheduled.Add,
			transformationContextProvider: () => ContentTransformationContext.For(
				new CodeCompressionContext(temp.Path, compressionSession),
				redaction: null));

		await pipeline.PrewarmCompressionAsync(
			currentTree,
			TestContext.Current.CancellationToken,
			cleanupAfterCompletion: MemoryCleanupReason.ApplySettingsWorkCompleted);

		Assert.Equal(
			[MemoryCleanupReason.ApplySettingsWorkCompleted],
			scheduled);
		Assert.False(viewModel.StatusBusy);
		Assert.Equal(0, pipeline.RetainedReadFactBytes);
	}

	[AvaloniaFact]
	public async Task CompressionPrewarm_SupersededPassSchedulesOnlyForLatestCompletion()
	{
		using var temp = new TemporaryDirectory();
		var textFile = temp.CreateFile("Program.cs", "internal class Program { void Run() { } }");
		var treeRoot = new TreeNodeDescriptor(
			"root",
			temp.Path,
			true,
			false,
			"folder",
			[new TreeNodeDescriptor("Program.cs", textFile, false, false, "csharp", [])]);
		var currentTree = new BuildTreeResult(treeRoot, false, false, [textFile]);
		var viewModel = CreateViewModel();
		var status = new StatusOperationCoordinator(
			viewModel,
			isBackgroundMetricsActive: () => false,
			metricsOperationTextProvider: () => viewModel.StatusOperationCalculatingData);
		var analyzer = new FirstPrewarmReadCancellationAnalyzer(new FileContentAnalyzer());
		using var compressor = new CountingCodeCompressor();
		using var compressionSession = new CodeCompressionSession(compressor);
		var scheduled = new List<MemoryCleanupReason>();
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
			scheduleMemoryCleanup: scheduled.Add,
			transformationContextProvider: () => ContentTransformationContext.For(
				new CodeCompressionContext(temp.Path, compressionSession),
				redaction: null));

		var superseded = pipeline.PrewarmCompressionAsync(
			currentTree,
			TestContext.Current.CancellationToken,
			cleanupAfterCompletion: MemoryCleanupReason.ApplySettingsWorkCompleted);
		await analyzer.FirstReadStarted.WaitAsync(
			TimeSpan.FromSeconds(5),
			TestContext.Current.CancellationToken);
		Assert.Empty(scheduled);

		var latest = pipeline.PrewarmCompressionAsync(
			currentTree,
			TestContext.Current.CancellationToken,
			cleanupAfterCompletion: MemoryCleanupReason.ApplySettingsWorkCompleted);
		await Task.WhenAll(superseded, latest).WaitAsync(
			TimeSpan.FromSeconds(5),
			TestContext.Current.CancellationToken);

		Assert.True(analyzer.FirstReadWasCanceled);
		Assert.Equal(
			[MemoryCleanupReason.ApplySettingsWorkCompleted],
			scheduled);
		Assert.Equal(0, pipeline.RetainedReadFactBytes);
	}

	[AvaloniaFact]
	public async Task CompressionPrewarm_AllTransformationsDisabledSchedulesCleanupWithoutReading()
	{
		using var temp = new TemporaryDirectory();
		var textFile = temp.CreateFile("Program.cs", "internal class Program { }");
		var treeRoot = new TreeNodeDescriptor(
			"root",
			temp.Path,
			true,
			false,
			"folder",
			[new TreeNodeDescriptor("Program.cs", textFile, false, false, "csharp", [])]);
		var currentTree = new BuildTreeResult(treeRoot, false, false, [textFile]);
		var viewModel = CreateViewModel();
		var analyzer = new CountingFileContentAnalyzer(new FileContentAnalyzer());
		var scheduled = new List<MemoryCleanupReason>();
		using var compressor = new CountingCodeCompressor();
		using var compressionSession = new CodeCompressionSession(compressor);
		ContentTransformationContext? transformation = ContentTransformationContext.For(
			new CodeCompressionContext(temp.Path, compressionSession),
			redaction: null);
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
			boundsWidthProvider: () => 1400,
			scheduleMemoryCleanup: scheduled.Add,
			transformationContextProvider: () => transformation);

		await pipeline.PrewarmCompressionAsync(
			currentTree,
			TestContext.Current.CancellationToken,
			retainReadFactsForNextMetricsPass: true);
		Assert.True(pipeline.RetainedReadFactBytes > 0);
		var readsBeforeDisable = analyzer.GetClassifiedReadCallCount(textFile);
		transformation = null;

		await pipeline.PrewarmCompressionAsync(
			currentTree,
			TestContext.Current.CancellationToken,
			cleanupAfterCompletion: MemoryCleanupReason.ApplySettingsWorkCompleted);

		Assert.Equal(
			[MemoryCleanupReason.ApplySettingsWorkCompleted],
			scheduled);
		Assert.Equal(readsBeforeDisable, analyzer.GetClassifiedReadCallCount(textFile));
		Assert.Equal(0, analyzer.GetMetricsCallCount(textFile));
		Assert.Equal(0, pipeline.RetainedReadFactBytes);
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

		await pipeline.PrewarmCompressionAsync(
			currentTree,
			TestContext.Current.CancellationToken,
			retainReadFactsForNextMetricsPass: true);
		Assert.Equal(1, analyzer.GetClassifiedReadCallCount(textFile));
		Assert.True(pipeline.RetainedReadFactBytes > 0);

		await pipeline.InitializeFileMetricsCacheSoonAfterFirstPaintAsync(
			currentTree,
			TestContext.Current.CancellationToken);

		Assert.Equal(1, analyzer.GetClassifiedReadCallCount(textFile));
		Assert.Equal(0, analyzer.GetMetricsCallCount(textFile));
		Assert.True(pipeline.HasCompleteBaseline);
		Assert.Equal(0, pipeline.RetainedReadFactBytes);
	}

	[AvaloniaFact]
	public async Task PostLoadReadFacts_UserCancellationBeforeMetrics_ReleasesSnapshot()
	{
		using var temp = new TemporaryDirectory();
		var textFile = temp.CreateFile("Program.cs", "internal class Program { void Run() { } }");
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
			selectedPathsProvider: () => new HashSet<string>(PathComparer.Default),
			treeFormatProvider: () => TreeTextFormat.Ascii,
			exportPathPresentationProvider: () => null,
			boundsWidthProvider: () => 1400,
			transformationContextProvider: () => ContentTransformationContext.For(
				new CodeCompressionContext(temp.Path, compressionSession),
				redaction: null));

		await pipeline.PrewarmCompressionAsync(
			currentTree,
			TestContext.Current.CancellationToken,
			retainReadFactsForNextMetricsPass: true);
		Assert.True(pipeline.RetainedReadFactBytes > 0);

		pipeline.CancelByUser();

		Assert.Equal(0, pipeline.RetainedReadFactBytes);
	}

	[AvaloniaFact]
	public async Task PostLoadMetrics_ChangedFileRejectsThePrewarmReadFact()
	{
		using var temp = new TemporaryDirectory();
		var textFile = temp.CreateFile(
			"Program.cs",
			"internal class Program { void Run() { } }");
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
		viewModel.SelectedPreviewContentMode = PreviewContentMode.Content;
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

		await pipeline.PrewarmCompressionAsync(
			currentTree,
			TestContext.Current.CancellationToken,
			retainReadFactsForNextMetricsPass: true);
		Assert.Equal(1, analyzer.GetClassifiedReadCallCount(textFile));

		const string changedSource =
			"internal class Program { void Run() { System.Console.WriteLine(12345); } }";
		await File.WriteAllTextAsync(textFile, changedSource, TestContext.Current.CancellationToken);
		File.SetLastWriteTimeUtc(textFile, DateTime.UtcNow.AddMinutes(1));
		await pipeline.InitializeFileMetricsCacheSoonAfterFirstPaintAsync(
			currentTree,
			TestContext.Current.CancellationToken);
		await WaitUntilAsync(
			() => pipeline.HasStatusMetricsSnapshot,
			TimeSpan.FromSeconds(5));

		Assert.Equal(2, analyzer.GetClassifiedReadCallCount(textFile));
		Assert.Equal(0, analyzer.GetMetricsCallCount(textFile));
		var renderedContent = await new SelectedContentExportService(new FileContentAnalyzer())
			.BuildAsync(
				[textFile],
				TestContext.Current.CancellationToken,
				TreeAndContentExportService.CreateRelativeContentHeaderPathMapper(temp.Path),
				transformationContext: transformation,
				displayRootPath: temp.Path);
		var expectedMetrics = ExportOutputMetricsCalculator.FromText(renderedContent);
		using var document = new InMemoryPreviewTextDocument("x");
		Assert.True(pipeline.TryGetCachedPreviewSelectionMetrics(
			PreviewContentMode.Content,
			document,
			new PreviewSelectionRange(1, 0, 1, 1),
			out var actualMetrics));
		Assert.Equal(expectedMetrics, actualMetrics);
		Assert.Equal(0, pipeline.RetainedReadFactBytes);
	}

	[AvaloniaFact]
	public async Task PostLoadMetrics_DeletedFileRejectsThePrewarmReadFactWithoutThrowing()
	{
		using var temp = new TemporaryDirectory();
		var textFile = temp.CreateFile(
			"Program.cs",
			"internal class Program { void Run() { } }");
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

		await pipeline.PrewarmCompressionAsync(
			currentTree,
			TestContext.Current.CancellationToken,
			retainReadFactsForNextMetricsPass: true);
		Assert.Equal(1, analyzer.GetClassifiedReadCallCount(textFile));
		File.Delete(textFile);

		await pipeline.InitializeFileMetricsCacheSoonAfterFirstPaintAsync(
			currentTree,
			TestContext.Current.CancellationToken);

		Assert.Equal(2, analyzer.GetClassifiedReadCallCount(textFile));
		Assert.Equal(0, analyzer.GetMetricsCallCount(textFile));
		Assert.True(pipeline.HasCompleteBaseline);
		Assert.Equal(0, pipeline.RetainedReadFactBytes);
	}

	[AvaloniaFact]
	public async Task PostLoadMetrics_ReusesStreamedUnsupportedMetricsWithoutMaterializingTheFile()
	{
		using var temp = new TemporaryDirectory();
		var textFile = temp.CreateFile(
			"site.css",
			"/* documentation */\n.card { color: red; }\n");
		var treeRoot = new TreeNodeDescriptor(
			"root",
			temp.Path,
			true,
			false,
			"folder",
			[new TreeNodeDescriptor("site.css", textFile, false, false, "css", [])]);
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

		await pipeline.PrewarmCompressionAsync(
			currentTree,
			TestContext.Current.CancellationToken,
			retainReadFactsForNextMetricsPass: true);
		Assert.Equal(0, analyzer.GetClassifiedReadCallCount(textFile));
		Assert.Equal(1, analyzer.GetMetricsCallCount(textFile));
		Assert.Equal(
			CodeCompressionOutcome.UnchangedUnsupportedLanguage,
			Assert.Single(compressionSession.Snapshot.Unchanged).Outcome);

		await pipeline.InitializeFileMetricsCacheSoonAfterFirstPaintAsync(
			currentTree,
			TestContext.Current.CancellationToken);

		Assert.Equal(0, analyzer.GetClassifiedReadCallCount(textFile));
		Assert.Equal(1, analyzer.GetMetricsCallCount(textFile));
		Assert.True(pipeline.HasCompleteBaseline);
		Assert.Equal(0, pipeline.RetainedReadFactBytes);
	}

	[AvaloniaFact]
	public async Task PostLoadMetrics_RereadsMetricsOnlyFactWhenTheFileBecomesSupportedInAnotherMode()
	{
		using var temp = new TemporaryDirectory();
		var textFile = temp.CreateFile(
			"site.css",
			"/* documentation */\n.card { color: red; }\n");
		var treeRoot = new TreeNodeDescriptor(
			"root",
			temp.Path,
			true,
			false,
			"folder",
			[new TreeNodeDescriptor("site.css", textFile, false, false, "css", [])]);
		var currentTree = new BuildTreeResult(treeRoot, false, false, [textFile]);
		var viewModel = CreateViewModel();
		viewModel.IsProjectLoaded = true;
		viewModel.TreeNodes.Add(new TreeNodeViewModel(treeRoot, parent: null, icon: null));
		var analyzer = new CountingFileContentAnalyzer(new FileContentAnalyzer());
		var status = new StatusOperationCoordinator(
			viewModel,
			isBackgroundMetricsActive: () => false,
			metricsOperationTextProvider: () => viewModel.StatusOperationCalculatingData);
		var compressor = new CommentsOnlyCssCompressor();
		using var compressionSession = new CodeCompressionSession(compressor);
		var transformation = ContentTransformationContext.For(
			new CodeCompressionContext(
				temp.Path,
				compressionSession,
				CodeTransformKinds.Bodies),
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

		await pipeline.PrewarmCompressionAsync(
			currentTree,
			TestContext.Current.CancellationToken,
			retainReadFactsForNextMetricsPass: true);
		Assert.Equal(0, analyzer.GetClassifiedReadCallCount(textFile));
		Assert.Equal(1, analyzer.GetMetricsCallCount(textFile));

		transformation = ContentTransformationContext.For(
			new CodeCompressionContext(
				temp.Path,
				compressionSession,
				CodeTransformKinds.Comments),
			redaction: null);
		await pipeline.InitializeFileMetricsCacheSoonAfterFirstPaintAsync(
			currentTree,
			TestContext.Current.CancellationToken);

		Assert.Equal(1, analyzer.GetClassifiedReadCallCount(textFile));
		Assert.Equal(1, analyzer.GetMetricsCallCount(textFile));
		Assert.True(pipeline.HasCompleteBaseline);
		Assert.Equal(0, pipeline.RetainedReadFactBytes);
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
				.BuildAsync(
					orderedPaths,
					TestContext.Current.CancellationToken,
					TreeAndContentExportService.CreateRelativeContentHeaderPathMapper(temp.Path),
					displayRootPath: temp.Path);
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
    public async Task InitializeFileMetricsCacheAsync_TransientReadFailureRetriesOnlyMissingFileAndRecoversExactMetrics()
    {
        using var temp = new TemporaryDirectory();
        var healthyFile = temp.CreateFile("src/Healthy.cs", "line 1\nline 2");
        var transientFile = temp.CreateFile("src/Transient.cs", "line 3\nline 4\nline 5");
        var orderedPaths = new[] { healthyFile, transientFile }
            .OrderBy(static path => path, PathComparer.Default)
            .ToArray();
        var treeRoot = CreateTree(temp.Path, orderedPaths);
        var currentTree = new BuildTreeResult(treeRoot, false, false, orderedPaths);
        var viewModel = CreateViewModel();
        viewModel.IsProjectLoaded = true;
        viewModel.SelectedPreviewContentMode = PreviewContentMode.Content;
        viewModel.TreeNodes.Add(new TreeNodeViewModel(treeRoot, parent: null, icon: null));
        var analyzer = new FaultInjectingMetricsAnalyzer(
            new FileContentAnalyzer(),
            transientFile,
            static call => call == 1 ? new IOException("Temporary read failure.") : null);
        using var pipeline = CreateMetricsPipeline(
            viewModel,
            currentTree,
            temp.Path,
            analyzer);

        await pipeline.InitializeFileMetricsCacheSoonAfterFirstPaintAsync(
            currentTree,
            TestContext.Current.CancellationToken);

        Assert.False(pipeline.HasCompleteBaseline);
        Assert.False(pipeline.IsBackgroundActive);
        Assert.False(viewModel.StatusBusy);
        Assert.Equal(1, analyzer.GetMetricsCallCount(healthyFile));
        Assert.Equal(1, analyzer.GetMetricsCallCount(transientFile));

        pipeline.Recalculate();
        await WaitUntilAsync(
            () => analyzer.GetMetricsCallCount(transientFile) == 2 &&
                  pipeline.HasCompleteBaseline &&
                  !pipeline.IsBackgroundActive,
            TimeSpan.FromSeconds(5));

        using var document = new InMemoryPreviewTextDocument("x");
        var cleanViewModel = CreateViewModel();
        cleanViewModel.IsProjectLoaded = true;
        cleanViewModel.SelectedPreviewContentMode = PreviewContentMode.Content;
        cleanViewModel.TreeNodes.Add(new TreeNodeViewModel(treeRoot, parent: null, icon: null));
        using var cleanPipeline = CreateMetricsPipeline(
            cleanViewModel,
            currentTree,
            temp.Path,
            new FileContentAnalyzer());
        await cleanPipeline.InitializeFileMetricsCacheSoonAfterFirstPaintAsync(
            currentTree,
            TestContext.Current.CancellationToken);
        await WaitUntilAsync(
            () => cleanPipeline.HasStatusMetricsSnapshot,
            TimeSpan.FromSeconds(5));
        Assert.True(cleanPipeline.TryGetCachedPreviewSelectionMetrics(
            PreviewContentMode.Content,
            document,
            new PreviewSelectionRange(1, 0, 1, 1),
            out var expectedMetrics));

        ExportOutputMetrics actualMetrics = default;
        await WaitUntilAsync(
            () => pipeline.TryGetCachedPreviewSelectionMetrics(
                      PreviewContentMode.Content,
                      document,
                      new PreviewSelectionRange(1, 0, 1, 1),
                      out actualMetrics) &&
                  actualMetrics == expectedMetrics,
            TimeSpan.FromSeconds(5));

        Assert.Equal(1, analyzer.GetMetricsCallCount(healthyFile));
        Assert.Equal(2, analyzer.GetMetricsCallCount(transientFile));
        Assert.Equal(expectedMetrics, actualMetrics);
    }

    [AvaloniaFact]
    public async Task InitializeFileMetricsCacheAsync_BinaryFileIsCachedAsInspected()
    {
        using var temp = new TemporaryDirectory();
        var binaryFile = temp.CreateBinaryFile("assets/image.png", [0x89, 0x50, 0x4E, 0x47, 0x00]);
        var treeRoot = CreateTree(temp.Path, [binaryFile]);
        var currentTree = new BuildTreeResult(treeRoot, false, false, [binaryFile]);
        var viewModel = CreateViewModel();
        viewModel.IsProjectLoaded = true;
        viewModel.TreeNodes.Add(new TreeNodeViewModel(treeRoot, parent: null, icon: null));
        var analyzer = new FaultInjectingMetricsAnalyzer(new FileContentAnalyzer());
        using var pipeline = CreateMetricsPipeline(
            viewModel,
            currentTree,
            temp.Path,
            analyzer);

        await pipeline.InitializeFileMetricsCacheSoonAfterFirstPaintAsync(
            currentTree,
            TestContext.Current.CancellationToken);
        pipeline.Recalculate();
        await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.True(pipeline.HasCompleteBaseline);
        Assert.Equal(1, analyzer.GetClassificationCallCount(binaryFile));
        Assert.Equal(0, analyzer.GetMetricsCallCount(binaryFile));
    }

    [AvaloniaFact]
    public async Task InitializeFileMetricsCacheAsync_UnexpectedAnalyzerFailureEndsRunWithoutCompletingBaseline()
    {
        using var temp = new TemporaryDirectory();
        var textFile = temp.CreateFile("Program.cs", "class Program { }");
        var treeRoot = CreateTree(temp.Path, [textFile]);
        var currentTree = new BuildTreeResult(treeRoot, false, false, [textFile]);
        var viewModel = CreateViewModel();
        viewModel.IsProjectLoaded = true;
        viewModel.TreeNodes.Add(new TreeNodeViewModel(treeRoot, parent: null, icon: null));
        var analyzer = new FaultInjectingMetricsAnalyzer(
            new FileContentAnalyzer(),
            textFile,
            static _ => new InvalidOperationException("Unexpected analyzer failure."));
        using var pipeline = CreateMetricsPipeline(
            viewModel,
            currentTree,
            temp.Path,
            analyzer);

        await pipeline.InitializeFileMetricsCacheSoonAfterFirstPaintAsync(
            currentTree,
            TestContext.Current.CancellationToken);

        Assert.False(pipeline.HasCompleteBaseline);
        Assert.False(pipeline.IsBackgroundActive);
        Assert.False(viewModel.StatusBusy);
        Assert.Equal(1, analyzer.GetMetricsCallCount(textFile));
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
	public async Task Recalculate_WithoutTransformationChangeSchedulesNoMemoryCleanup()
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
		var currentTree = new BuildTreeResult(treeRoot, false, false, [textFile]);
		var viewModel = CreateViewModel();
		viewModel.IsProjectLoaded = true;
		viewModel.TreeNodes.Add(new TreeNodeViewModel(treeRoot, parent: null, icon: null));
		var scheduled = new List<MemoryCleanupReason>();
		var status = new StatusOperationCoordinator(
			viewModel,
			isBackgroundMetricsActive: () => false,
			metricsOperationTextProvider: () => viewModel.StatusOperationCalculatingData);
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
			scheduleMemoryCleanup: scheduled.Add);

		pipeline.Recalculate();
		await WaitUntilAsync(
			() => pipeline.HasStatusMetricsSnapshot,
			TimeSpan.FromSeconds(5));

		Assert.Empty(scheduled);
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

    private static TreeNodeDescriptor CreateTree(
        string rootPath,
        IReadOnlyList<string> files) =>
        new(
            "root",
            rootPath,
            true,
            false,
            "folder",
            files
                .Select(path => new TreeNodeDescriptor(
                    Path.GetFileName(path),
                    path,
                    false,
                    false,
                    "text",
                    []))
                .ToArray());

    private static MetricsPipeline CreateMetricsPipeline(
        MainWindowViewModel viewModel,
        BuildTreeResult currentTree,
        string currentPath,
        IFileContentAnalyzer analyzer)
    {
        var status = new StatusOperationCoordinator(
            viewModel,
            isBackgroundMetricsActive: () => false,
            metricsOperationTextProvider: () => viewModel.StatusOperationCalculatingData);
        return new MetricsPipeline(
            viewModel,
            CreateLocalization(),
            analyzer,
            new TreeExportService(),
            status,
            currentTreeProvider: () => currentTree,
            currentPathProvider: () => currentPath,
            selectedPathsProvider: () => new HashSet<string>(PathComparer.Default),
            treeFormatProvider: () => TreeTextFormat.Ascii,
            exportPathPresentationProvider: () => null,
            boundsWidthProvider: () => 1400);
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

    private sealed class CountingFileContentAnalyzer(IFileContentAnalyzer inner) :
		IFileContentAnalyzer,
		IPrewarmFileContentAnalyzer
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

		public ValueTask<BudgetedContentReadResult> ReadFactWithBudgetAsync(
			string path,
			long maximumReadBytes,
			WeightedByteBudget byteBudget,
			SemaphoreSlim decodeScratchGate,
			CancellationToken cancellationToken = default)
		{
			lock (_sync)
				_classifiedReadCalls[path] = _classifiedReadCalls.GetValueOrDefault(path) + 1;
			return ((IPrewarmFileContentAnalyzer)inner).ReadFactWithBudgetAsync(
				path,
				maximumReadBytes,
				byteBudget,
				decodeScratchGate,
				cancellationToken);
		}

		public ValueTask<IdentifiedFileContentMetricsResult> GetClassifiedMetricsWithIdentityAsync(
			string path,
			CancellationToken cancellationToken = default)
		{
			lock (_sync)
				_metricsCalls[path] = _metricsCalls.GetValueOrDefault(path) + 1;
			return ((IPrewarmFileContentAnalyzer)inner).GetClassifiedMetricsWithIdentityAsync(
				path,
				cancellationToken);
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

	private sealed class CountingReadOnlySet<T>(IReadOnlySet<T> inner) : IReadOnlySet<T>
	{
		private int _enumerationCount;

		public int Count => inner.Count;
		public int EnumerationCount => Volatile.Read(ref _enumerationCount);

		public bool Contains(T item) => inner.Contains(item);
		public bool IsProperSubsetOf(IEnumerable<T> other) => inner.IsProperSubsetOf(other);
		public bool IsProperSupersetOf(IEnumerable<T> other) => inner.IsProperSupersetOf(other);
		public bool IsSubsetOf(IEnumerable<T> other) => inner.IsSubsetOf(other);
		public bool IsSupersetOf(IEnumerable<T> other) => inner.IsSupersetOf(other);
		public bool Overlaps(IEnumerable<T> other) => inner.Overlaps(other);
		public bool SetEquals(IEnumerable<T> other) => inner.SetEquals(other);

		public IEnumerator<T> GetEnumerator()
		{
			Interlocked.Increment(ref _enumerationCount);
			return inner.GetEnumerator();
		}

		System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

		public void ResetEnumerationCount() => Volatile.Write(ref _enumerationCount, 0);
	}

    private sealed class FaultInjectingMetricsAnalyzer(
        IFileContentAnalyzer inner,
        string? faultPath = null,
        Func<int, Exception?>? faultFactory = null) : IFileContentAnalyzer
    {
        private readonly Dictionary<string, int> _classificationCalls = new(PathComparer.Default);
        private readonly Dictionary<string, int> _metricsCalls = new(PathComparer.Default);
        private readonly object _sync = new();

        public int GetClassificationCallCount(string path)
        {
            lock (_sync)
                return _classificationCalls.GetValueOrDefault(path);
        }

        public int GetMetricsCallCount(string path)
        {
            lock (_sync)
                return _metricsCalls.GetValueOrDefault(path);
        }

        public FileContentClassification? ClassifyWithoutReading(string path)
        {
            lock (_sync)
                _classificationCalls[path] = _classificationCalls.GetValueOrDefault(path) + 1;
            return inner.ClassifyWithoutReading(path);
        }

        public ValueTask<bool> IsTextFileAsync(
            string path,
            CancellationToken cancellationToken = default) =>
            inner.IsTextFileAsync(path, cancellationToken);

        public ValueTask<TextFileMetrics?> GetTextFileMetricsAsync(
            string path,
            CancellationToken cancellationToken = default)
        {
            int call;
            lock (_sync)
            {
                call = _metricsCalls.GetValueOrDefault(path) + 1;
                _metricsCalls[path] = call;
            }

            if (PathComparer.Default.Equals(path, faultPath) &&
                faultFactory?.Invoke(call) is { } exception)
            {
                return ValueTask.FromException<TextFileMetrics?>(exception);
            }

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

	private sealed class FirstPrewarmReadCancellationAnalyzer(
		IFileContentAnalyzer inner) : IFileContentAnalyzer, IPrewarmFileContentAnalyzer
	{
		private readonly TaskCompletionSource _firstReadStarted =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		private int _prewarmReadCount;
		private int _firstReadWasCanceled;

		public Task FirstReadStarted => _firstReadStarted.Task;
		public bool FirstReadWasCanceled => Volatile.Read(ref _firstReadWasCanceled) != 0;

		public async ValueTask<BudgetedContentReadResult> ReadFactWithBudgetAsync(
			string path,
			long maximumReadBytes,
			WeightedByteBudget byteBudget,
			SemaphoreSlim decodeScratchGate,
			CancellationToken cancellationToken = default)
		{
			if (Interlocked.Increment(ref _prewarmReadCount) == 1)
			{
				_firstReadStarted.TrySetResult();
				try
				{
					await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
				}
				catch (OperationCanceledException)
				{
					Interlocked.Exchange(ref _firstReadWasCanceled, 1);
					throw;
				}
			}

			return await ((IPrewarmFileContentAnalyzer)inner).ReadFactWithBudgetAsync(
				path,
				maximumReadBytes,
				byteBudget,
				decodeScratchGate,
				cancellationToken);
		}

		public ValueTask<IdentifiedFileContentMetricsResult> GetClassifiedMetricsWithIdentityAsync(
			string path,
			CancellationToken cancellationToken = default) =>
			((IPrewarmFileContentAnalyzer)inner).GetClassifiedMetricsWithIdentityAsync(
				path,
				cancellationToken);

		public FileContentClassification? ClassifyWithoutReading(string path) =>
			inner.ClassifyWithoutReading(path);

		public ValueTask<FileContentReadResult> ReadClassifiedAsync(
			string path,
			long maxSizeForFullRead,
			CancellationToken cancellationToken = default) =>
			inner.ReadClassifiedAsync(path, maxSizeForFullRead, cancellationToken);

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
			CancellationToken cancellationToken = default) =>
			inner.GetClassifiedMetricsAsync(path, cancellationToken);

		public ValueTask<IFileContentSnapshot> OpenCompleteSnapshotAsync(
			string path,
			CancellationToken cancellationToken = default) =>
			inner.OpenCompleteSnapshotAsync(path, cancellationToken);

		public ValueTask<ICompleteTextFileBuffer> OpenCompleteTextBufferAsync(
			string path,
			long maximumBytes,
			CancellationToken cancellationToken = default) =>
			inner.OpenCompleteTextBufferAsync(path, maximumBytes, cancellationToken);

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

	private sealed class CommentsOnlyCssCompressor : ICodeCompressor
	{
		public string TransformIdentity => "metrics-comments-only:v1";

		public bool IsSupported(string relativePath) => false;

		public bool IsSupported(string relativePath, CodeTransformKinds kinds) =>
			Path.GetExtension(relativePath).Equals(".css", StringComparison.OrdinalIgnoreCase) &&
			(kinds & CodeTransformKinds.Comments) != 0;

		public CodeTransformKinds GetEffectiveTransformKinds(
			string relativePath,
			CodeTransformKinds kinds) =>
			IsSupported(relativePath, kinds)
				? kinds & CodeTransformKinds.Comments
				: CodeTransformKinds.None;

		public ICodeCompressionScope CreateScope(string projectRoot) => new Scope(this);

		public ICodeCompressionScope CreateScope(
			string projectRoot,
			CodeTransformKinds kinds) =>
			new Scope(this);

		private sealed class Scope(CommentsOnlyCssCompressor owner) : ICodeCompressionScope
		{
			public CodeCompressionAnalysis Analyze(
				string fullPath,
				string relativePath,
				string content,
				CancellationToken cancellationToken) =>
				new(
					CodeCompressionPlan.Unchanged(
						relativePath,
						"css",
						CodeCompressionOutcome.UnchangedNoBenefit,
						content.Length,
						owner.TransformIdentity),
					null);

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
