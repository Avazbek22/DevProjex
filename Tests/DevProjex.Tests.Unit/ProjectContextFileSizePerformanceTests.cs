using System.Diagnostics;
using DevProjex.Application.Context;

namespace DevProjex.Tests.Unit;

public sealed class ProjectContextFileSizePerformanceTests(ITestOutputHelper output)
{
	[Fact]
	[Trait("Category", "LocalPerformance")]
	public void CompleteInventoryFileSizeBenchmark()
	{
		if (!string.Equals(
			    Environment.GetEnvironmentVariable("DEVPROJEX_RUN_LARGE_PERF_TESTS"),
			    "1",
			    StringComparison.Ordinal))
		{
			Assert.Skip("Set DEVPROJEX_RUN_LARGE_PERF_TESTS=1 for the pre-release performance gate.");
		}

		const int fileCount = 100_000;
		var (root, inventory) = CreateSnapshot(fileCount);
		var orderedFilePaths = root.Children.Select(static node => node.FullPath).ToArray();

		var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
		var stopwatch = Stopwatch.StartNew();
		var sizes = ProjectContextPlanner.BuildEffectiveFileSizes(
			root,
			orderedFilePaths,
			inventory,
			TestContext.Current.CancellationToken);
		stopwatch.Stop();

		Assert.Equal(fileCount, sizes.Count);
		Assert.Equal(fileCount - 1, sizes.Values.Max());
		output.WriteLine(
			$"Complete inventory sizes: {stopwatch.Elapsed.TotalMilliseconds:F3} ms / " +
			$"{GC.GetAllocatedBytesForCurrentThread() - allocatedBefore:N0} B for {fileCount:N0} files.");
	}

	private static (TreeNodeDescriptor Root, ProjectTreeInventorySnapshot Inventory) CreateSnapshot(int fileCount)
	{
		var rootPath = Path.Combine(Path.GetTempPath(), "dpx-size-benchmark");
		var children = new List<TreeNodeDescriptor>(fileCount);
		var entries = new List<ProjectTreeInventoryEntry>(fileCount + 1)
		{
			new("dpx-size-benchmark", rootPath, string.Empty, -1, true, false, 0)
			{
				FirstChildIndex = 1,
				ChildCount = fileCount
			}
		};
		for (var index = 0; index < fileCount; index++)
		{
			var name = $"file-{index:D6}.cs";
			var fullPath = Path.Combine(rootPath, name);
			children.Add(new TreeNodeDescriptor(name, fullPath, false, false, "csharp", []));
			entries.Add(new ProjectTreeInventoryEntry(
				name,
				fullPath,
				name,
				parentIndex: 0,
				isDirectory: false,
				isHidden: false,
				length: index));
		}

		return (
			new TreeNodeDescriptor("dpx-size-benchmark", rootPath, true, false, "folder", children),
			new ProjectTreeInventorySnapshot(entries, rootAccessDenied: false, hadAccessDenied: false));
	}
}
