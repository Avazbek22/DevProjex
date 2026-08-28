using System.Diagnostics;

namespace DevProjex.Tests.Integration;

public sealed class WorkspaceInventoryPerformanceTests(ITestOutputHelper output)
{
	[Fact]
	[Trait("Category", "LocalPerformance")]
	public void UnifiedInventoryCaptureBenchmark()
	{
		if (!string.Equals(
			    Environment.GetEnvironmentVariable("DEVPROJEX_RUN_LARGE_PERF_TESTS"),
			    "1",
			    StringComparison.Ordinal))
		{
			Assert.Skip("Set DEVPROJEX_RUN_LARGE_PERF_TESTS=1 for the pre-release performance gate.");
		}

		const int fileCount = 20_000;
		using var temp = new TemporaryDirectory();
		for (var index = 0; index < fileCount; index++)
			temp.CreateFile($"src/bucket-{index / 100:D4}/file-{index:D6}.cs", "content");

		var scanner = new FileSystemScanner();
		var rules = CreateRules();
		var request = new ProjectWorkspaceScanRequest(
			temp.Path,
			["src"],
			rules,
			rules,
			EffectiveExtensionPolicy: null,
			CaptureTreeInventory: true,
			IncludeDirectoryToggleProbeRoots: false,
			IncludeControllerImpactProbeRoots: false);

		var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
		var stopwatch = Stopwatch.StartNew();
		var snapshot = scanner.ScanProjectWorkspace(
			request,
			TestContext.Current.CancellationToken);
		stopwatch.Stop();

		var inventory = Assert.IsType<ProjectTreeInventorySnapshot>(snapshot.Value.TreeInventory);
		Assert.Equal(fileCount, inventory.Entries.Count(static entry => !entry.IsDirectory));
		output.WriteLine(
			$"Unified inventory capture: {stopwatch.Elapsed.TotalMilliseconds:F3} ms / " +
			$"{GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore:N0} B for {fileCount:N0} files.");
	}

	private static IgnoreRules CreateRules() =>
		new(
			IgnoreHiddenFolders: false,
			IgnoreHiddenFiles: false,
			IgnoreDotFolders: false,
			IgnoreDotFiles: false,
			SmartIgnoredFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
			SmartIgnoredFiles: new HashSet<string>(StringComparer.OrdinalIgnoreCase));
}
