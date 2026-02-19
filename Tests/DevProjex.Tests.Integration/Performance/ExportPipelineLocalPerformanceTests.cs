namespace DevProjex.Tests.Integration.Performance;

[Collection("LocalPerformance")]
[Trait("Category", "LocalPerformance")]
public sealed class ExportPipelineLocalPerformanceTests
{
    private readonly ITestOutputHelper _output;
    private readonly PerfBaselineStore _baselineStore;

    public ExportPipelineLocalPerformanceTests(ITestOutputHelper output)
    {
        _output = output;
        _baselineStore = new PerfBaselineStore(LocalPerformanceSettings.BaselineFilePath);
    }

    public static IEnumerable<object[]> TreeExportCases()
    {
        var depths = new[] { 2, 3, 4 };
        var branchFactors = new[] { 2, 3 };
        var filesPerDirectory = new[] { 4, 8 };
        var formats = new[] { TreeTextFormat.Ascii, TreeTextFormat.Json };

        foreach (var depth in depths)
        foreach (var branchFactor in branchFactors)
        foreach (var files in filesPerDirectory)
        foreach (var format in formats)
            yield return new object[] { new TreeExportPerfCase(depth, branchFactor, files, format) };
    }

    public static IEnumerable<object[]> ContentExportCases()
    {
        var fileCounts = new[] { 50, 150, 300 };
        var fileSizes = new[] { 128, 512 };
        var selectionModes = new[] { SelectionMode.All, SelectionMode.Half };
        var formats = new[] { TreeTextFormat.Ascii, TreeTextFormat.Json };

        foreach (var count in fileCounts)
        foreach (var size in fileSizes)
        foreach (var selection in selectionModes)
        foreach (var format in formats)
            yield return new object[] { new ContentExportPerfCase(count, size, selection, format) };
    }

    [LocalPerformanceTheory]
    [MemberData(nameof(TreeExportCases))]
    public void BuildFullTree_ShouldRemainFastAndMemoryEfficient(TreeExportPerfCase perfCase)
    {
        var rootPath = Path.Combine(Path.DirectorySeparatorChar.ToString(), "perf-root");
        var descriptor = BuildDescriptor(rootPath, perfCase.Depth, perfCase.BranchFactor, perfCase.FilesPerDirectory);
        var service = new TreeExportService();

        string? latest = null;
        var measurement = PerfMeasurementRunner.Measure(() =>
        {
            latest = service.BuildFullTree(rootPath, descriptor, perfCase.Format);
        });

        Assert.False(string.IsNullOrWhiteSpace(latest));

        var scenarioId = $"export.tree-full.{perfCase}";
        ReportAndAssert(scenarioId, measurement, maxMs: 5000, maxAllocatedBytes: 256L * 1024 * 1024);
    }

    [LocalPerformanceTheory]
    [MemberData(nameof(TreeExportCases))]
    public void BuildSelectedTree_ShouldRemainFastAndMemoryEfficient(TreeExportPerfCase perfCase)
    {
        var rootPath = Path.Combine(Path.DirectorySeparatorChar.ToString(), "perf-root");
        var descriptor = BuildDescriptor(rootPath, perfCase.Depth, perfCase.BranchFactor, perfCase.FilesPerDirectory);
        var selectedPaths = BuildSelectedFileSet(descriptor, everyNth: 3);
        var service = new TreeExportService();

        string? latest = null;
        var measurement = PerfMeasurementRunner.Measure(() =>
        {
            latest = service.BuildSelectedTree(rootPath, descriptor, selectedPaths, perfCase.Format);
        });

        Assert.False(string.IsNullOrWhiteSpace(latest));

        var scenarioId = $"export.tree-selected.{perfCase}";
        ReportAndAssert(scenarioId, measurement, maxMs: 5000, maxAllocatedBytes: 256L * 1024 * 1024);
    }

    [LocalPerformanceTheory]
    [MemberData(nameof(ContentExportCases))]
    public void BuildSelectedContent_ShouldRemainFastAndMemoryEfficient(ContentExportPerfCase perfCase)
    {
        using var temp = new TemporaryDirectory();
        var files = CreateTextFiles(temp.Path, perfCase.FileCount, perfCase.FileSizeBytes);
        var selectedPaths = BuildSelection(files, perfCase.SelectionMode);
        var analyzer = new FileContentAnalyzer();
        var service = new SelectedContentExportService(analyzer);

        string? latest = null;
        var measurement = PerfMeasurementRunner.Measure(() =>
        {
            latest = service.Build(selectedPaths);
        }, warmupIterations: 1, measuredIterations: 2);

        Assert.False(string.IsNullOrWhiteSpace(latest));

        var scenarioId = $"export.content-selected.{perfCase.WithFormat(TreeTextFormat.Ascii)}";
        ReportAndAssert(scenarioId, measurement, maxMs: 8000, maxAllocatedBytes: 384L * 1024 * 1024);
    }

    [LocalPerformanceTheory]
    [MemberData(nameof(ContentExportCases))]
    public void BuildTreeAndContent_ShouldRemainFastAndMemoryEfficient(ContentExportPerfCase perfCase)
    {
        using var temp = new TemporaryDirectory();
        var files = CreateTextFiles(temp.Path, perfCase.FileCount, perfCase.FileSizeBytes);
        var root = BuildFlatDescriptor(temp.Path, files);
        var selectedPaths = BuildSelection(files, perfCase.SelectionMode);

        var analyzer = new FileContentAnalyzer();
        var treeExport = new TreeExportService();
        var contentExport = new SelectedContentExportService(analyzer);
        var service = new TreeAndContentExportService(treeExport, contentExport);

        string? latest = null;
        var measurement = PerfMeasurementRunner.Measure(() =>
        {
            latest = service.Build(temp.Path, root, selectedPaths, perfCase.Format);
        }, warmupIterations: 1, measuredIterations: 2);

        Assert.False(string.IsNullOrWhiteSpace(latest));

        var scenarioId = $"export.tree-and-content.{perfCase}";
        ReportAndAssert(scenarioId, measurement, maxMs: 10000, maxAllocatedBytes: 512L * 1024 * 1024);
    }

    private void ReportAndAssert(string scenarioId, PerfMeasurement measurement, double maxMs, long maxAllocatedBytes)
    {
        var verdict = _baselineStore.Evaluate(scenarioId, measurement);
        _output.WriteLine(verdict.Message);

        if (verdict.IsAcceleration)
            _output.WriteLine($"[acceleration-detected] {scenarioId}");

        if (LocalPerformanceSettings.ShouldEnforceBaselineRegression)
        {
            Assert.False(verdict.IsRegression, verdict.Message);
        }
        else if (verdict.IsRegression)
        {
            _output.WriteLine($"[regression-detected-nonblocking] {scenarioId}");
        }

        Assert.True(measurement.MedianMilliseconds < maxMs,
            $"Absolute latency guard failed for {scenarioId}: {measurement.MedianMilliseconds:F2} ms.");
        Assert.True(measurement.MedianAllocatedBytes < maxAllocatedBytes,
            $"Absolute allocation guard failed for {scenarioId}: {measurement.MedianAllocatedBytes} bytes.");
    }

    private static TreeNodeDescriptor BuildDescriptor(
        string rootPath,
        int depth,
        int branchFactor,
        int filesPerDirectory)
    {
        TreeNodeDescriptor BuildDirectory(string directoryPath, int level, int branchIndex)
        {
            var children = new List<TreeNodeDescriptor>();

            for (var fileIndex = 0; fileIndex < filesPerDirectory; fileIndex++)
            {
                var fileName = $"file_{level}_{branchIndex}_{fileIndex}.txt";
                var filePath = Path.Combine(directoryPath, fileName);
                children.Add(new TreeNodeDescriptor(fileName, filePath, false, false, "text", []));
            }

            if (level < depth)
            {
                for (var childBranch = 0; childBranch < branchFactor; childBranch++)
                {
                    var childDirName = $"dir_{level}_{branchIndex}_{childBranch}";
                    var childDirPath = Path.Combine(directoryPath, childDirName);
                    children.Add(BuildDirectory(childDirPath, level + 1, childBranch));
                }
            }

            return new TreeNodeDescriptor(
                DisplayName: Path.GetFileName(directoryPath),
                FullPath: directoryPath,
                IsDirectory: true,
                IsAccessDenied: false,
                IconKey: "folder",
                Children: children);
        }

        return BuildDirectory(rootPath, level: 1, branchIndex: 0);
    }

    private static HashSet<string> BuildSelectedFileSet(TreeNodeDescriptor root, int everyNth)
    {
        var allFiles = new List<string>();
        CollectFiles(root, allFiles);

        var selected = new HashSet<string>(PathComparer.Default);
        for (var index = 0; index < allFiles.Count; index++)
        {
            if (index % everyNth == 0)
                selected.Add(allFiles[index]);
        }

        return selected;
    }

    private static void CollectFiles(TreeNodeDescriptor node, List<string> destination)
    {
        if (!node.IsDirectory)
        {
            destination.Add(node.FullPath);
            return;
        }

        foreach (var child in node.Children)
            CollectFiles(child, destination);
    }

    private static IReadOnlyList<string> CreateTextFiles(string rootPath, int fileCount, int fileSizeBytes)
    {
        var files = new List<string>(fileCount);
        var line = "The quick brown fox jumps over the lazy dog. ";
        var content = BuildFixedSizeContent(line, fileSizeBytes);

        for (var index = 0; index < fileCount; index++)
        {
            var filePath = Path.Combine(rootPath, $"perf_{index:D5}.txt");
            File.WriteAllText(filePath, content);
            files.Add(filePath);
        }

        return files;
    }

    private static string BuildFixedSizeContent(string pattern, int sizeBytes)
    {
        var patternByteSize = Encoding.UTF8.GetByteCount(pattern);
        var repeatCount = Math.Max(1, (int)Math.Ceiling((double)sizeBytes / patternByteSize));

        var sb = new StringBuilder(repeatCount * pattern.Length);
        for (var index = 0; index < repeatCount; index++)
            sb.Append(pattern);

        return sb.ToString();
    }

    private static HashSet<string> BuildSelection(IReadOnlyList<string> files, SelectionMode mode)
    {
        var selected = new HashSet<string>(PathComparer.Default);
        for (var index = 0; index < files.Count; index++)
        {
            if (mode == SelectionMode.All || index % 2 == 0)
                selected.Add(files[index]);
        }

        return selected;
    }

    private static TreeNodeDescriptor BuildFlatDescriptor(string rootPath, IReadOnlyList<string> files)
    {
        var children = new List<TreeNodeDescriptor>(files.Count);
        foreach (var file in files)
        {
            children.Add(new TreeNodeDescriptor(
                DisplayName: Path.GetFileName(file),
                FullPath: file,
                IsDirectory: false,
                IsAccessDenied: false,
                IconKey: "text",
                Children: []));
        }

        return new TreeNodeDescriptor(
            DisplayName: Path.GetFileName(rootPath),
            FullPath: rootPath,
            IsDirectory: true,
            IsAccessDenied: false,
            IconKey: "folder",
            Children: children);
    }

    public sealed record TreeExportPerfCase(
        int Depth,
        int BranchFactor,
        int FilesPerDirectory,
        TreeTextFormat Format)
    {
        public override string ToString()
            => $"d{Depth}-b{BranchFactor}-f{FilesPerDirectory}-{Format}";
    }

    public sealed record ContentExportPerfCase(
        int FileCount,
        int FileSizeBytes,
        SelectionMode SelectionMode,
        TreeTextFormat Format)
    {
        public override string ToString()
            => $"n{FileCount}-size{FileSizeBytes}-{SelectionMode}-{Format}";

        public ContentExportPerfCase WithFormat(TreeTextFormat format) => this with { Format = format };
    }

    public enum SelectionMode
    {
        All,
        Half
    }
}
