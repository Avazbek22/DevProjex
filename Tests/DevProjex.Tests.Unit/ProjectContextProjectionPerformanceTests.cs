using System.Diagnostics;
using DevProjex.Application.Context;
using DevProjex.Application.Selection;

namespace DevProjex.Tests.Unit;

public sealed class ProjectContextProjectionPerformanceTests(ITestOutputHelper output)
{
	[Fact]
	[Trait("Category", "LocalPerformance")]
	public void CompleteTreeProjectionBenchmark()
	{
		if (!string.Equals(
			    Environment.GetEnvironmentVariable("DEVPROJEX_RUN_LARGE_PERF_TESTS"),
			    "1",
			    StringComparison.Ordinal))
		{
			Assert.Skip("Set DEVPROJEX_RUN_LARGE_PERF_TESTS=1 for the pre-release performance gate.");
		}

		var root = CreateTree(directoryCount: 100, filesPerDirectory: 999);
		var selection = new HashSet<string>(PathComparer.Default);
		var included = ProjectTreeSelectionProjection.BuildIncludedNodes(root, selection);

		var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
		var stopwatch = Stopwatch.StartNew();
		var projected = ProjectContextPlanner.ResolveProjectedTree(
			root,
			selection,
			included,
			selectsNoEffectivePaths: false,
			TestContext.Current.CancellationToken);
		stopwatch.Stop();

		Assert.Same(root, projected);
		output.WriteLine(
			$"Complete tree projection: {stopwatch.Elapsed.TotalMilliseconds:F3} ms / " +
			$"{GC.GetAllocatedBytesForCurrentThread() - allocatedBefore:N0} B for {included.Count:N0} nodes.");
	}

	[Fact]
	public void ResolveProjectedTree_DistinguishesCompleteAndMissingExplicitSelections()
	{
		var root = CreateTree(directoryCount: 1, filesPerDirectory: 1);
		var fullSelection = new HashSet<string>(PathComparer.Default);
		var included = ProjectTreeSelectionProjection.BuildIncludedNodes(root, fullSelection);

		var complete = ProjectContextPlanner.ResolveProjectedTree(
			root,
			fullSelection,
			included,
			selectsNoEffectivePaths: false,
			TestContext.Current.CancellationToken);
		var missing = ProjectContextPlanner.ResolveProjectedTree(
			root,
			fullSelection,
			[],
			selectsNoEffectivePaths: true,
			TestContext.Current.CancellationToken);

		Assert.Same(root, complete);
		Assert.Empty(missing.Children);
		Assert.NotSame(root, missing);
	}

	private static TreeNodeDescriptor CreateTree(int directoryCount, int filesPerDirectory)
	{
		const string rootPath = "/benchmark/project";
		var directories = new List<TreeNodeDescriptor>(directoryCount);
		for (var directoryIndex = 0; directoryIndex < directoryCount; directoryIndex++)
		{
			var directoryPath = $"{rootPath}/dir-{directoryIndex:D3}";
			var files = new List<TreeNodeDescriptor>(filesPerDirectory);
			for (var fileIndex = 0; fileIndex < filesPerDirectory; fileIndex++)
			{
				files.Add(new TreeNodeDescriptor(
					$"file-{fileIndex:D4}.cs",
					$"{directoryPath}/file-{fileIndex:D4}.cs",
					IsDirectory: false,
					IsAccessDenied: false,
					"csharp",
					[]));
			}

			directories.Add(new TreeNodeDescriptor(
				$"dir-{directoryIndex:D3}",
				directoryPath,
				IsDirectory: true,
				IsAccessDenied: false,
				"folder",
				files));
		}

		return new TreeNodeDescriptor(
			"project",
			rootPath,
			IsDirectory: true,
			IsAccessDenied: false,
			"folder",
			directories);
	}
}
