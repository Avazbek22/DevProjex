namespace DevProjex.Tests.Integration.Performance;

[Collection("LocalPerformance")]
[Trait("Category", "LocalPerformance")]
public sealed class TreeBuilderLocalPerformanceTests
{
    private readonly ITestOutputHelper _output;
    private readonly PerfBaselineStore _baselineStore;

    public TreeBuilderLocalPerformanceTests(ITestOutputHelper output)
    {
        _output = output;
        _baselineStore = new PerfBaselineStore(LocalPerformanceSettings.BaselineFilePath);
    }

    public static IEnumerable<object[]> BuildCases()
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
    [MemberData(nameof(BuildCases))]
    public void Build_ShouldRemainFastAndMemoryEfficient(FileSystemPerfCase perfCase)
    {
        using var temp = new TemporaryDirectory();
        var dataset = PerfDatasetBuilder.CreateDirectoryTree(temp.Path, perfCase);

        var builder = new TreeBuilder();
        var options = new TreeFilterOptions(
            AllowedExtensions: dataset.AllowedExtensions,
            AllowedRootFolders: dataset.RootFolderNames,
            IgnoreRules: PerfDatasetBuilder.BuildIgnoreRules(perfCase.IgnoreProfile),
            NameFilter: null);

        TreeBuildResult? latest = null;
        var measurement = PerfMeasurementRunner.Measure(() =>
        {
            latest = builder.Build(temp.Path, options);
        });

        Assert.NotNull(latest);
        Assert.NotNull(latest!.Root);
        Assert.True(latest.Root.Children.Count > 0, "Tree must contain visible root children.");

        var scenarioId = $"tree-builder.build.{perfCase}";
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

        Assert.True(measurement.MedianMilliseconds < 8000,
            $"Absolute latency guard failed for {scenarioId}: {measurement.MedianMilliseconds:F2} ms.");
        Assert.True(measurement.MedianAllocatedBytes < 320L * 1024 * 1024,
            $"Absolute allocation guard failed for {scenarioId}: {measurement.MedianAllocatedBytes} bytes.");
    }
}
