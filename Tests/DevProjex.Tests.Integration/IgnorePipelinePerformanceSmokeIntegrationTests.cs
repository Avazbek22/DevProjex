namespace DevProjex.Tests.Integration;

public sealed class IgnorePipelinePerformanceSmokeIntegrationTests
{
	[Fact]
	public void IgnoreSectionSnapshot_TenThousandFiles_CompletesWithinSmokeBudget()
	{
		using var temp = CreateSyntheticWorkspace(fileCount: 10_000);
		var elapsed = MeasureIgnoreSnapshot(temp.Path);

		Assert.True(
			elapsed < TimeSpan.FromSeconds(30),
			$"10k ignore snapshot smoke exceeded budget: {elapsed}.");
	}

	[Fact]
	public void IgnoreSectionSnapshot_RepeatedLargeScans_DoNotRetainWorkspaceSizedManagedMemory()
	{
		using var temp = CreateSyntheticWorkspace(fileCount: 10_000);

		// Warm up JIT/static caches before measuring retained memory. The assertion is about
		// scan result lifetime, not one-time runtime initialization costs.
		RunIgnoreSnapshot(temp.Path);
		ForceFullCollection();
		var baselineBytes = GC.GetTotalMemory(forceFullCollection: true);

		for (var attempt = 0; attempt < 3; attempt++)
			RunIgnoreSnapshot(temp.Path);

		ForceFullCollection();
		var retainedBytes = Math.Max(0, GC.GetTotalMemory(forceFullCollection: true) - baselineBytes);

		Assert.True(
			retainedBytes < 128L * 1024 * 1024,
			$"Repeated ignore scans retained too much managed memory: {retainedBytes:N0} bytes.");
	}

	[Theory]
	[InlineData(50_000, 90)]
	[InlineData(100_000, 180)]
	public void IgnoreSectionSnapshot_LargeWorkspaces_CompletesWithinOptInSmokeBudget(
		int fileCount,
		int maxSeconds)
	{
		if (!string.Equals(
			    Environment.GetEnvironmentVariable("DEVPROJEX_RUN_LARGE_PERF_TESTS"),
			    "1",
			    StringComparison.Ordinal))
		{
			return;
		}

		using var temp = CreateSyntheticWorkspace(fileCount);
		var elapsed = MeasureIgnoreSnapshot(temp.Path);

		Assert.True(
			elapsed < TimeSpan.FromSeconds(maxSeconds),
			$"{fileCount:N0} ignore snapshot smoke exceeded budget: {elapsed}.");
	}

	private static TimeSpan MeasureIgnoreSnapshot(string rootPath)
	{
		var stopwatch = Stopwatch.StartNew();
		RunIgnoreSnapshot(rootPath);
		stopwatch.Stop();

		return stopwatch.Elapsed;
	}

	private static void RunIgnoreSnapshot(string rootPath)
	{
		var scanOptions = new ScanOptionsUseCase(new FileSystemScanner());
		var rules = CreateIgnoreRules();
		var snapshot = scanOptions.GetIgnoreSectionSnapshotForRootFolders(
			rootPath,
			["src", "docs", "tests"],
			rules,
			rules,
			effectiveAllowedExtensions: null,
			includeDirectoryToggleProbeRoots: true);

		Assert.Contains(".cs", snapshot.Value.Extensions);
		Assert.True(snapshot.Value.EffectiveIgnoreOptionCounts.DotFolders >= 1);
	}

	private static IgnoreRules CreateIgnoreRules()
		=> new(
			IgnoreHiddenFolders: false,
			IgnoreHiddenFiles: false,
			IgnoreDotFolders: true,
			IgnoreDotFiles: true,
			SmartIgnoredFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "node_modules" },
			SmartIgnoredFiles: new HashSet<string>(StringComparer.OrdinalIgnoreCase))
		{
			IgnoreEmptyFolders = true,
			IgnoreEmptyFiles = true,
			IgnoreExtensionlessFiles = true,
			UseSmartIgnore = true
		};

	private static void ForceFullCollection()
	{
		GC.Collect();
		GC.WaitForPendingFinalizers();
		GC.Collect();
	}

	private static TemporaryDirectory CreateSyntheticWorkspace(int fileCount)
	{
		var temp = new TemporaryDirectory();
		temp.CreateFile("src/App.cs", "class App {}");
		temp.CreateFile("docs/readme.md", "# docs");
		temp.CreateFile("tests/AppTests.cs", "class AppTests {}");
		temp.CreateFile(".idea/workspace.xml", "<project />");
		temp.CreateFile("node_modules/pkg/generated.js", "x");

		var roots = new[] { "src", "docs", "tests" };
		for (var index = 0; index < fileCount; index++)
		{
			var root = roots[index % roots.Length];
			var bucket = index / 100;
			var extension = index % 5 == 0 ? ".md" : ".cs";
			temp.CreateFile($"{root}/bucket-{bucket:D4}/file-{index:D6}{extension}", "content");
		}

		return temp;
	}
}
