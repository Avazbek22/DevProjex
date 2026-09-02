using System.Diagnostics;

namespace DevProjex.Tests.Integration;

[Trait("Category", "LocalPerformance")]
public sealed class TreeExportMetricsPerformanceTests
{
	private const string EnabledVariable = "DEVPROJEX_RUN_TREE_METRICS_BENCHMARK";
	private const int DirectoryCount = 200;
	private const int FilesPerDirectory = 200;
	private const int MeasuredRuns = 5;

	[Fact]
	public void StructuredFormats_RecordTreeMetricsPerformance()
	{
		if (!string.Equals(Environment.GetEnvironmentVariable(EnabledVariable), "1", StringComparison.Ordinal))
			Assert.Skip($"Set {EnabledVariable}=1 to run the tree metrics benchmark.");

		var service = new TreeExportService();
		var root = BuildTree();
		var selectedPaths = root.Children
			.Where(static (_, index) => index % 2 == 0)
			.Select(static node => node.FullPath)
			.ToHashSet(PathComparer.Default);
		foreach (var format in new[] { TreeTextFormat.Json, TreeTextFormat.Xml, TreeTextFormat.Markdown })
		{
			RecordScenario(service, root, selectedPaths: null, format, "full");
			RecordScenario(service, root, selectedPaths, format, "selected");
		}
	}

	private static void RecordScenario(
		TreeExportService service,
		TreeNodeDescriptor root,
		IReadOnlySet<string>? selectedPaths,
		TreeTextFormat format,
		string scenario)
	{
		_ = Measure(service, root, selectedPaths, format);
		var runs = new List<MetricsBenchmarkRun>(MeasuredRuns);
		for (var index = 0; index < MeasuredRuns; index++)
			runs.Add(Measure(service, root, selectedPaths, format));

		var orderedMilliseconds = runs.Select(static run => run.ElapsedMilliseconds).Order().ToArray();
		var orderedAllocations = runs.Select(static run => run.AllocatedBytes).Order().ToArray();
		TestContext.Current.TestOutputHelper?.WriteLine(
			$"{format}/{scenario}: median={orderedMilliseconds[MeasuredRuns / 2]:F2} ms, " +
			$"allocated={orderedAllocations[MeasuredRuns / 2]:N0} bytes, " +
			$"chars={runs[0].Metrics.Chars:N0}");
	}

	private static MetricsBenchmarkRun Measure(
		TreeExportService service,
		TreeNodeDescriptor root,
		IReadOnlySet<string>? selectedPaths,
		TreeTextFormat format)
	{
		var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
		var stopwatch = Stopwatch.StartNew();
		var metrics = selectedPaths is null
			? service.CalculateFullTreeMetrics("/workspace", root, format)
			: service.CalculateSelectedTreeMetrics("/workspace", root, selectedPaths, format);
		stopwatch.Stop();
		var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
		Assert.True(metrics.Chars > 0);
		return new MetricsBenchmarkRun(stopwatch.Elapsed.TotalMilliseconds, allocatedBytes, metrics);
	}

	private static TreeNodeDescriptor BuildTree()
	{
		var directories = new TreeNodeDescriptor[DirectoryCount];
		for (var directoryIndex = 0; directoryIndex < directories.Length; directoryIndex++)
		{
			var directoryPath = $"/workspace/src/component-{directoryIndex:D3}";
			var files = new TreeNodeDescriptor[FilesPerDirectory];
			for (var fileIndex = 0; fileIndex < files.Length; fileIndex++)
			{
				var fileName = $"feature-{fileIndex:D3}-данные.cs";
				files[fileIndex] = new TreeNodeDescriptor(
					fileName,
					$"{directoryPath}/{fileName}",
					IsDirectory: false,
					IsAccessDenied: false,
					IconKey: "file",
					Children: []);
			}

			directories[directoryIndex] = new TreeNodeDescriptor(
				$"component-{directoryIndex:D3}",
				directoryPath,
				IsDirectory: true,
				IsAccessDenied: false,
				IconKey: "folder",
				Children: files);
		}

		return new TreeNodeDescriptor(
			"workspace",
			"/workspace",
			IsDirectory: true,
			IsAccessDenied: false,
			IconKey: "folder",
			Children: directories);
	}

	private readonly record struct MetricsBenchmarkRun(
		double ElapsedMilliseconds,
		long AllocatedBytes,
		ExportOutputMetrics Metrics);
}
