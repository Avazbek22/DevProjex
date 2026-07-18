using System.Diagnostics;
using Avalonia.Threading;
using DevProjex.Application.Preview;

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
        private readonly object _sync = new();

        public int GetMetricsCallCount(string path)
        {
            lock (_sync)
                return _metricsCalls.GetValueOrDefault(path);
        }

        public Task<bool> IsTextFileAsync(string path, CancellationToken cancellationToken = default) =>
            inner.IsTextFileAsync(path, cancellationToken);

        public Task<TextFileMetrics?> GetTextFileMetricsAsync(
            string path,
            CancellationToken cancellationToken = default)
        {
            lock (_sync)
                _metricsCalls[path] = _metricsCalls.GetValueOrDefault(path) + 1;

            return inner.GetTextFileMetricsAsync(path, cancellationToken);
        }

        public Task<TextFileContent?> TryReadAsTextAsync(
            string path,
            CancellationToken cancellationToken = default) =>
            inner.TryReadAsTextAsync(path, cancellationToken);

        public Task<TextFileContent?> TryReadAsTextAsync(
            string path,
            long maxSizeForFullRead,
            CancellationToken cancellationToken = default) =>
            inner.TryReadAsTextAsync(path, maxSizeForFullRead, cancellationToken);
    }
}
