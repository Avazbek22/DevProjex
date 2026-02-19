namespace DevProjex.Tests.Integration.Performance;

[Collection("LocalPerformance")]
[Trait("Category", "LocalPerformance")]
public sealed class FileSystemScannerLocalPerformanceTests
{
    private readonly ITestOutputHelper _output;
    private readonly PerfBaselineStore _baselineStore;

    public FileSystemScannerLocalPerformanceTests(ITestOutputHelper output)
    {
        _output = output;
        _baselineStore = new PerfBaselineStore(LocalPerformanceSettings.BaselineFilePath);
    }

    public static IEnumerable<object[]> ExtensionScanCases()
    {
        var depths = new[] { 2, 3, 4 };
        var branchFactors = new[] { 2, 3 };
        var filesPerDirectory = new[] { 6, 12 };
        var ignoreProfiles = new[]
        {
            PerfIgnoreProfile.None,
            PerfIgnoreProfile.SmartIgnore,
            PerfIgnoreProfile.DotAndHidden
        };

        foreach (var depth in depths)
        foreach (var branchFactor in branchFactors)
        foreach (var fileCount in filesPerDirectory)
        foreach (var profile in ignoreProfiles)
            yield return new object[] { new FileSystemPerfCase(depth, branchFactor, fileCount, profile) };
    }

    [LocalPerformanceTheory]
    [MemberData(nameof(ExtensionScanCases))]
    public void GetExtensions_ShouldRemainFastAndMemoryEfficient(FileSystemPerfCase perfCase)
    {
        using var temp = new TemporaryDirectory();
        _ = PerfDatasetBuilder.CreateDirectoryTree(temp.Path, perfCase);

        var scanner = new FileSystemScanner();
        var rules = PerfDatasetBuilder.BuildIgnoreRules(perfCase.IgnoreProfile);

        ScanResult<HashSet<string>>? latest = null;
        var measurement = PerfMeasurementRunner.Measure(() =>
        {
            latest = scanner.GetExtensions(temp.Path, rules);
        });

        Assert.NotNull(latest);
        Assert.NotEmpty(latest!.Value);
        Assert.False(latest.RootAccessDenied);

        var scenarioId = $"scanner.extensions.{perfCase}";
        var verdict = _baselineStore.Evaluate(scenarioId, measurement);
        _output.WriteLine(verdict.Message);

        if (LocalPerformanceSettings.ShouldEnforceBaselineRegression)
        {
            Assert.False(verdict.IsRegression, verdict.Message);
        }
        else if (verdict.IsRegression)
        {
            _output.WriteLine($"[regression-detected-nonblocking] {scenarioId}");
        }

        Assert.True(measurement.MedianMilliseconds < 6000,
            $"Absolute latency guard failed for {scenarioId}: {measurement.MedianMilliseconds:F2} ms.");
        Assert.True(measurement.MedianAllocatedBytes < 256L * 1024 * 1024,
            $"Absolute allocation guard failed for {scenarioId}: {measurement.MedianAllocatedBytes} bytes.");
    }

    [LocalPerformanceTheory]
    [MemberData(nameof(ExtensionScanCases))]
    public void GetRootFolderNames_ShouldRemainFastAndMemoryEfficient(FileSystemPerfCase perfCase)
    {
        using var temp = new TemporaryDirectory();
        _ = PerfDatasetBuilder.CreateDirectoryTree(temp.Path, perfCase);

        var scanner = new FileSystemScanner();
        var rules = PerfDatasetBuilder.BuildIgnoreRules(perfCase.IgnoreProfile);

        ScanResult<List<string>>? latest = null;
        var measurement = PerfMeasurementRunner.Measure(() =>
        {
            latest = scanner.GetRootFolderNames(temp.Path, rules);
        });

        Assert.NotNull(latest);
        Assert.False(latest!.RootAccessDenied);
        Assert.NotNull(latest.Value);

        var scenarioId = $"scanner.root-folders.{perfCase}";
        var verdict = _baselineStore.Evaluate(scenarioId, measurement);
        _output.WriteLine(verdict.Message);

        if (LocalPerformanceSettings.ShouldEnforceBaselineRegression)
        {
            Assert.False(verdict.IsRegression, verdict.Message);
        }
        else if (verdict.IsRegression)
        {
            _output.WriteLine($"[regression-detected-nonblocking] {scenarioId}");
        }

        Assert.True(measurement.MedianMilliseconds < 3000,
            $"Absolute latency guard failed for {scenarioId}: {measurement.MedianMilliseconds:F2} ms.");
        Assert.True(measurement.MedianAllocatedBytes < 96L * 1024 * 1024,
            $"Absolute allocation guard failed for {scenarioId}: {measurement.MedianAllocatedBytes} bytes.");
    }
}
