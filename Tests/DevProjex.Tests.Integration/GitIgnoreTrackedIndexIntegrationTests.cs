namespace DevProjex.Tests.Integration;

public sealed class GitIgnoreTrackedIndexIntegrationTests
{
	[Fact]
	public void TrackedIgnoredFilesAndDirectoriesRemainVisibleWhileUntrackedSiblingsStayIgnored()
	{
		EnsureGitAvailable();
		using var temp = new TemporaryDirectory();
		var repositoryRoot = temp.CreateDirectory("repo");
		temp.CreateFile("repo/.gitignore", "*.tmp\nignored-dir/\ndata/\n*.ignored\n*-no-extension\n");
		temp.CreateFile("repo/tracked.tmp", "tracked");
		temp.CreateFile("repo/untracked.tmp", "untracked");
		temp.CreateFile("repo/ignored-dir/tracked.bin", "tracked");
		temp.CreateFile("repo/ignored-dir/untracked.bin", "untracked");
		temp.CreateFile("repo/data/.gitkeep", string.Empty);
		temp.CreateFile("repo/unicode/данные.ignored", "tracked");
		temp.CreateFile("repo/tracked-no-extension", "tracked");
		InitializeIndex(
			repositoryRoot,
			".gitignore",
			"tracked.tmp",
			"ignored-dir/tracked.bin",
			"data/.gitkeep",
			"unicode/данные.ignored",
			"tracked-no-extension");

		var observation = BuildTree(
			repositoryRoot,
			RootSet("ignored-dir", "data", "unicode"),
			ExtensionSet(".tmp", ".bin", ".gitkeep", ".ignored", "tracked-no-extension"));

		Assert.Single(observation.Inventory.DiscoveredGitTrackedPathIndexes);
		Assert.True(observation.Inventory.DiscoveredGitTrackedPathIndexes[0].Count >= 5);
		AssertVisible(observation.Paths, "tracked.tmp");
		AssertVisible(observation.Paths, "ignored-dir");
		AssertVisible(observation.Paths, "ignored-dir/tracked.bin");
		AssertVisible(observation.Paths, "data/.gitkeep");
		AssertVisible(observation.Paths, "unicode/данные.ignored");
		AssertVisible(observation.Paths, "tracked-no-extension");
		AssertHidden(observation.Paths, "untracked.tmp");
		AssertHidden(observation.Paths, "ignored-dir/untracked.bin");
	}

	[Fact]
	public void ExistingRepositoryWithoutIndexKeepsPatternOnlyFallback()
	{
		EnsureGitAvailable();
		using var temp = new TemporaryDirectory();
		var repositoryRoot = temp.CreateDirectory("repo");
		temp.CreateFile("repo/.gitignore", "*.tmp\n");
		temp.CreateFile("repo/untracked.tmp", "untracked");
		RunGit(repositoryRoot, "init", "--quiet");

		var observation = BuildTree(
			repositoryRoot,
			RootSet(),
			ExtensionSet(".tmp"));

		Assert.Empty(observation.Inventory.DiscoveredGitTrackedPathIndexes);
		AssertHidden(observation.Paths, "untracked.tmp");
	}

	[Fact]
	public void ChangedIndexInvalidatesCachedTrackedPathsWithoutProjectRestart()
	{
		EnsureGitAvailable();
		using var temp = new TemporaryDirectory();
		var repositoryRoot = temp.CreateDirectory("repo");
		temp.CreateFile("repo/.gitignore", "*.tmp\n");
		temp.CreateFile("repo/late.tmp", "initially untracked");
		InitializeIndex(repositoryRoot, ".gitignore");

		var before = BuildTree(repositoryRoot, RootSet(), ExtensionSet(".tmp"));
		AssertHidden(before.Paths, "late.tmp");

		RunGit(repositoryRoot, "add", "-f", "--", "late.tmp");

		var after = BuildTree(repositoryRoot, RootSet(), ExtensionSet(".tmp"));
		AssertVisible(after.Paths, "late.tmp");
		Assert.Single(after.Inventory.DiscoveredGitTrackedPathIndexes);
	}

	[Fact]
	public void ChangedIndexWithRestoredTimestampStillInvalidatesCachedTrackedPaths()
	{
		EnsureGitAvailable();
		using var temp = new TemporaryDirectory();
		var repositoryRoot = temp.CreateDirectory("repo");
		temp.CreateFile("repo/.gitignore", "*.tmp\n");
		temp.CreateFile("repo/alpha.tmp", "alpha");
		temp.CreateFile("repo/bravo.tmp", "bravo");
		InitializeIndex(repositoryRoot, ".gitignore", "alpha.tmp");
		var indexPath = Path.Combine(repositoryRoot, ".git", "index");
		var originalTimestamp = File.GetLastWriteTimeUtc(indexPath);
		var originalLength = new FileInfo(indexPath).Length;

		var before = BuildTree(repositoryRoot, RootSet(), ExtensionSet(".tmp"));
		AssertVisible(before.Paths, "alpha.tmp");
		AssertHidden(before.Paths, "bravo.tmp");

		RunGit(repositoryRoot, "rm", "--cached", "--", "alpha.tmp");
		RunGit(repositoryRoot, "add", "-f", "--", "bravo.tmp");
		File.SetLastWriteTimeUtc(indexPath, originalTimestamp);
		Assert.Equal(originalLength, new FileInfo(indexPath).Length);

		var after = BuildTree(repositoryRoot, RootSet(), ExtensionSet(".tmp"));
		AssertHidden(after.Paths, "alpha.tmp");
		AssertVisible(after.Paths, "bravo.tmp");
	}

	[Fact]
	public async Task ConcurrentInventoryBuildsShareOneConsistentTrackedIndexProjection()
	{
		EnsureGitAvailable();
		using var temp = new TemporaryDirectory();
		var repositoryRoot = temp.CreateDirectory("repo");
		temp.CreateFile("repo/.gitignore", "ignored/\n");
		temp.CreateFile("repo/ignored/tracked.tmp", "tracked");
		temp.CreateFile("repo/ignored/untracked.tmp", "untracked");
		InitializeIndex(repositoryRoot, ".gitignore", "ignored/tracked.tmp");
		var rules = CreateTraversalRules();
		var options = new TreeFilterOptions(
			ExtensionSet(".tmp"),
			RootSet("ignored"),
			rules);
		using var startGate = new ManualResetEventSlim(initialState: false);

		var builds = Enumerable
			.Range(0, 16)
			.Select(_ => Task.Run(
				() =>
				{
					startGate.Wait();
					var builder = new TreeBuilder();
					var inventory = builder.ReadInventory(
						repositoryRoot,
						options,
						CancellationToken.None);
					var tree = builder.Build(
						inventory,
						options,
						CancellationToken.None);
					return new TreeObservation(
						inventory,
						FlattenRelativePaths(repositoryRoot, tree.Root));
				}))
			.ToArray();

		startGate.Set();
		var observations = await Task.WhenAll(builds);

		Assert.All(
			observations,
			observation =>
			{
				Assert.Single(observation.Inventory.DiscoveredGitTrackedPathIndexes);
				AssertVisible(observation.Paths, "ignored/tracked.tmp");
				AssertHidden(observation.Paths, "ignored/untracked.tmp");
			});
		Assert.All(
			observations.Skip(1),
			observation => Assert.Equal(observations[0].Paths, observation.Paths));
	}

	[Fact]
	public void NestedGitIgnoreWithoutRootGitIgnoreLoadsNearestRepositoryIndexOnDemand()
	{
		EnsureGitAvailable();
		using var temp = new TemporaryDirectory();
		var repositoryRoot = temp.CreateDirectory("repo");
		temp.CreateFile("repo/src/.gitignore", "*.tmp\n");
		temp.CreateFile("repo/src/tracked.tmp", "tracked");
		temp.CreateFile("repo/src/untracked.tmp", "untracked");
		InitializeIndex(repositoryRoot, "src/.gitignore", "src/tracked.tmp");
		var services = ProjectLoadWorkflowRefreshHarness.CreateServices();
		var rules = services.IgnoreRulesService.Build(
			repositoryRoot,
			[IgnoreOptionId.UseGitIgnore],
			selectedRootFolders: null);

		var observation = BuildTree(
			repositoryRoot,
			RootSet("src"),
			ExtensionSet(".tmp"),
			rules);

		Assert.Single(observation.Inventory.DiscoveredGitTrackedPathIndexes);
		AssertVisible(observation.Paths, "src/tracked.tmp");
		AssertHidden(observation.Paths, "src/untracked.tmp");
	}

	[Fact]
	public void RepositoryWithoutGitIgnoreRulesDoesNotRetainTrackedIndex()
	{
		EnsureGitAvailable();
		using var temp = new TemporaryDirectory();
		var repositoryRoot = temp.CreateDirectory("repo");
		temp.CreateFile("repo/src/app.cs", "namespace App;\n");
		InitializeIndex(repositoryRoot, "src/app.cs");

		var observation = BuildTree(
			repositoryRoot,
			RootSet("src"),
			ExtensionSet(".cs"));

		Assert.Empty(observation.Inventory.DiscoveredGitTrackedPathIndexes);
		AssertVisible(observation.Paths, "src/app.cs");
	}

	[Fact]
	public void NestedRepositoriesUseTheirOwnIndexesWithoutLeakingTrackedStateAcrossSiblings()
	{
		EnsureGitAvailable();
		using var temp = new TemporaryDirectory();
		var alphaRoot = temp.CreateDirectory("alpha");
		var betaRoot = temp.CreateDirectory("beta");
		temp.CreateFile("alpha/.gitignore", "*.secret\n");
		temp.CreateFile("alpha/shared.secret", "tracked in alpha");
		temp.CreateFile("beta/.gitignore", "*.secret\n");
		temp.CreateFile("beta/shared.secret", "untracked in beta");
		InitializeIndex(alphaRoot, ".gitignore", "shared.secret");
		InitializeIndex(betaRoot, ".gitignore");

		var observation = BuildTree(
			temp.Path,
			RootSet("alpha", "beta"),
			ExtensionSet(".secret"));

		Assert.Equal(2, observation.Inventory.DiscoveredGitTrackedPathIndexes.Count);
		AssertVisible(observation.Paths, "alpha/shared.secret");
		AssertHidden(observation.Paths, "beta/shared.secret");
	}

	[Fact]
	public void NestedRepositoryWithoutLocalGitIgnoreUsesItsOwnIndexForInheritedRules()
	{
		EnsureGitAvailable();
		using var temp = new TemporaryDirectory();
		var repositoryRoot = temp.CreateDirectory("repo");
		var nestedRepositoryRoot = temp.CreateDirectory("repo/nested");
		temp.CreateFile("repo/.gitignore", "*.tmp\n");
		temp.CreateFile("repo/nested/tracked.tmp", "tracked in nested repository");
		temp.CreateFile("repo/nested/untracked.tmp", "untracked in nested repository");
		InitializeIndex(repositoryRoot, ".gitignore");
		InitializeIndex(nestedRepositoryRoot, "tracked.tmp");

		var observation = BuildTree(
			repositoryRoot,
			RootSet("nested"),
			ExtensionSet(".tmp"));

		Assert.Equal(2, observation.Inventory.DiscoveredGitTrackedPathIndexes.Count);
		AssertVisible(observation.Paths, "nested/tracked.tmp");
		AssertHidden(observation.Paths, "nested/untracked.tmp");
	}

	[Fact]
	public void GitFileWorktreeResolvesItsOwnIndexAndPreservesTrackedIgnoredFile()
	{
		EnsureGitAvailable();
		using var temp = new TemporaryDirectory();
		var repositoryRoot = temp.CreateDirectory("main");
		var worktreeRoot = Path.Combine(temp.Path, "worktree");
		temp.CreateFile("main/.gitignore", "*.tmp\n");
		temp.CreateFile("main/tracked.tmp", "tracked");
		RunGit(repositoryRoot, "init", "--quiet");
		RunGit(repositoryRoot, "config", "user.email", "tests@devprojex.local");
		RunGit(repositoryRoot, "config", "user.name", "DevProjex Tests");
		RunGit(repositoryRoot, "add", "-f", "--", ".gitignore", "tracked.tmp");
		RunGit(repositoryRoot, "commit", "--quiet", "-m", "seed");
		RunGit(repositoryRoot, "worktree", "add", "--quiet", "--detach", worktreeRoot, "HEAD");

		var observation = BuildTree(worktreeRoot, RootSet(), ExtensionSet(".tmp"));

		Assert.True(File.Exists(Path.Combine(worktreeRoot, ".git")));
		Assert.Single(observation.Inventory.DiscoveredGitTrackedPathIndexes);
		AssertVisible(observation.Paths, "tracked.tmp");
	}

	[Fact]
	public void TrackedGitlinkDirectoryIsNotHiddenByOuterGitIgnoreAndUsesNestedIndex()
	{
		EnsureGitAvailable();
		using var temp = new TemporaryDirectory();
		var outerRoot = temp.CreateDirectory("outer");
		var nestedRoot = temp.CreateDirectory("outer/submodule");
		temp.CreateFile("outer/.gitignore", "submodule/\n");
		temp.CreateFile("outer/submodule/.gitignore", "*.cs\n");
		temp.CreateFile("outer/submodule/tracked.cs", "namespace Nested;\n");
		RunGit(nestedRoot, "init", "--quiet");
		RunGit(nestedRoot, "config", "user.email", "tests@devprojex.local");
		RunGit(nestedRoot, "config", "user.name", "DevProjex Tests");
		RunGit(nestedRoot, "add", "-f", "--", ".gitignore", "tracked.cs");
		RunGit(nestedRoot, "commit", "--quiet", "-m", "nested seed");
		var nestedCommit = RunGit(nestedRoot, "rev-parse", "HEAD").Trim();
		RunGit(outerRoot, "init", "--quiet");
		RunGit(outerRoot, "add", "--", ".gitignore");
		RunGit(
			outerRoot,
			"update-index",
			"--add",
			"--cacheinfo",
			$"160000,{nestedCommit},submodule");

		var observation = BuildTree(
			outerRoot,
			RootSet("submodule"),
			ExtensionSet(".cs"));

		Assert.Equal(2, observation.Inventory.DiscoveredGitTrackedPathIndexes.Count);
		AssertVisible(observation.Paths, "submodule");
		AssertVisible(observation.Paths, "submodule/tracked.cs");
	}

	[Fact]
	public void RootFoldersAndExtensionsMatchTheIndexAwareEffectiveTree()
	{
		EnsureGitAvailable();
		using var temp = new TemporaryDirectory();
		var repositoryRoot = temp.CreateDirectory("repo");
		temp.CreateFile("repo/.gitignore", "tracked-root/\nuntracked-root/\ndata/\n*.generated\n");
		temp.CreateFile("repo/tracked-root/kept.generated", "tracked");
		temp.CreateFile("repo/untracked-root/drop.generated", "untracked");
		temp.CreateFile("repo/data/.gitkeep", string.Empty);
		InitializeIndex(repositoryRoot, ".gitignore", "tracked-root/kept.generated", "data/.gitkeep");
		var services = ProjectLoadWorkflowRefreshHarness.CreateServices();
		var rules = services.IgnoreRulesService.Build(
			repositoryRoot,
			[IgnoreOptionId.UseGitIgnore],
			selectedRootFolders: null);
		var scanner = new FileSystemScanner();

		var roots = scanner.GetRootFolderNames(
			repositoryRoot,
			rules,
			TestContext.Current.CancellationToken);
		var extensions = scanner.GetExtensions(
			repositoryRoot,
			rules,
			TestContext.Current.CancellationToken);
		var tree = BuildTree(
			repositoryRoot,
			RootSet("tracked-root", "untracked-root", "data"),
			ExtensionSet(".generated", ".gitkeep"),
			rules);

		Assert.Equal(new[] { "data", "tracked-root" }, roots.Value);
		Assert.Contains(".generated", extensions.Value);
		Assert.Contains(".gitkeep", extensions.Value);
		AssertVisible(tree.Paths, "tracked-root/kept.generated");
		AssertVisible(tree.Paths, "data/.gitkeep");
		AssertHidden(tree.Paths, "untracked-root/drop.generated");
	}

	[Fact]
	public void CapturedWorkspaceInventoryPreservesTrackedIgnoredRootFile()
	{
		EnsureGitAvailable();
		using var temp = new TemporaryDirectory();
		var repositoryRoot = temp.CreateDirectory("repo");
		temp.CreateFile("repo/.gitignore", "*.generated\n");
		temp.CreateFile("repo/kept.generated", "tracked root file");
		InitializeIndex(repositoryRoot, ".gitignore", "kept.generated");
		var services = ProjectLoadWorkflowRefreshHarness.CreateServices();
		var rules = services.IgnoreRulesService.Build(
			repositoryRoot,
			[IgnoreOptionId.UseGitIgnore],
			selectedRootFolders: null);
		var roots = RootSet();
		var extensions = ExtensionSet(".generated");
		var scanner = new FileSystemScanner();

		var workspace = scanner.ScanProjectWorkspace(
			new ProjectWorkspaceScanRequest(
				repositoryRoot,
				roots,
				rules,
				rules,
				new ExtensionSetInclusionPolicy(extensions),
				CaptureTreeInventory: true,
				IncludeDirectoryToggleProbeRoots: false,
				IncludeControllerImpactProbeRoots: false),
			TestContext.Current.CancellationToken);
		var inventory = Assert.IsType<ProjectTreeInventorySnapshot>(workspace.Value.TreeInventory);
		var tree = new TreeBuilder().Build(
			inventory,
			new TreeFilterOptions(extensions, roots, rules),
			TestContext.Current.CancellationToken);

		Assert.Single(inventory.DiscoveredGitTrackedPathIndexes);
		AssertVisible(FlattenRelativePaths(repositoryRoot, tree.Root), "kept.generated");
	}

	[Fact]
	public void ExplicitProjectAnalysisPreservesTrackedEmptyDotFileInsideIgnoredRoot()
	{
		EnsureGitAvailable();
		using var temp = new TemporaryDirectory();
		var repositoryRoot = temp.CreateDirectory("repo");
		temp.CreateFile("repo/.gitignore", "data/\n");
		temp.CreateFile("repo/data/.gitkeep", string.Empty);
		InitializeIndex(repositoryRoot, ".gitignore", "data/.gitkeep");
		var service = CreateProjectAnalysisService();

		var loaded = service.Load(
			new ProjectAnalysisRequest(
				repositoryRoot,
				SelectedRootFolders: null,
				SelectedExtensions: null,
				SelectedIgnoreOptions: [IgnoreOptionId.UseGitIgnore]),
			TestContext.Current.CancellationToken);

		Assert.Contains("data", loaded.AvailableRootFolders, PathComparer.Default);
		Assert.Contains(".gitkeep", loaded.AvailableExtensions, StringComparer.OrdinalIgnoreCase);
		Assert.Contains("data", loaded.SelectedRootFolders, PathComparer.Default);
		Assert.Contains(".gitkeep", loaded.SelectedExtensions, StringComparer.OrdinalIgnoreCase);
	}

	[Fact]
	public void GitIgnoreImpactCountsOnlyEffectiveUntrackedMatchesWithoutStaleState()
	{
		EnsureGitAvailable();
		using var temp = new TemporaryDirectory();
		var repositoryRoot = temp.CreateDirectory("repo");
		temp.CreateFile("repo/.gitignore", "*.tmp\n");
		temp.CreateFile("repo/tracked.tmp", "tracked");
		InitializeIndex(repositoryRoot, ".gitignore", "tracked.tmp");

		var trackedOnly = ComputeConvergedSelectionSnapshot(repositoryRoot);
		var trackedOnlyOption = Assert.Single(
			trackedOnly.IgnoreOptions,
			static option => option.Id == IgnoreOptionId.UseGitIgnore);
		Assert.True(trackedOnlyOption.IsChecked);
		var administrativeImpact = trackedOnly.ControllerImpactCounts.GitIgnore;
		Assert.Equal(1, administrativeImpact);

		var untrackedPath = temp.CreateFile("repo/untracked.tmp", "untracked");
		var withUntrackedMatch = ComputeConvergedSelectionSnapshot(repositoryRoot);
		var gitIgnoreOption = Assert.Single(
			withUntrackedMatch.IgnoreOptions,
			static option => option.Id == IgnoreOptionId.UseGitIgnore);
		Assert.True(gitIgnoreOption.IsChecked);
		Assert.Equal(administrativeImpact + 1, withUntrackedMatch.ControllerImpactCounts.GitIgnore);

		File.Delete(untrackedPath);
		var restored = ComputeConvergedSelectionSnapshot(repositoryRoot);
		var restoredOption = Assert.Single(
			restored.IgnoreOptions,
			static option => option.Id == IgnoreOptionId.UseGitIgnore);
		Assert.True(restoredOption.IsChecked);
		Assert.Equal(administrativeImpact, restored.ControllerImpactCounts.GitIgnore);
	}

	private static TreeObservation BuildTree(
		string rootPath,
		IReadOnlySet<string> allowedRoots,
		IReadOnlySet<string> allowedExtensions,
		IgnoreRules? rules = null)
	{
		rules ??= CreateTraversalRules();
		var options = new TreeFilterOptions(allowedExtensions, allowedRoots, rules);
		var builder = new TreeBuilder();
		var inventory = builder.ReadInventory(
			rootPath,
			options,
			TestContext.Current.CancellationToken);
		var projected = builder.Build(
			inventory,
			options,
			TestContext.Current.CancellationToken);
		var direct = builder.Build(
			rootPath,
			options,
			TestContext.Current.CancellationToken);
		var projectedPaths = FlattenRelativePaths(rootPath, projected.Root);
		var directPaths = FlattenRelativePaths(rootPath, direct.Root);

		Assert.Equal(projectedPaths, directPaths);
		return new TreeObservation(inventory, projectedPaths);
	}

	private static IgnoreRules CreateTraversalRules() =>
		new(
			IgnoreHiddenFolders: false,
			IgnoreHiddenFiles: false,
			IgnoreDotFolders: false,
			IgnoreDotFiles: false,
			SmartIgnoredFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
			SmartIgnoredFiles: new HashSet<string>(StringComparer.OrdinalIgnoreCase))
		{
			UseGitIgnore = true,
			EnableGitIgnoreTraversal = true
		};

	private static SelectionRefreshSnapshot ComputeConvergedSelectionSnapshot(string rootPath)
	{
		var services = ProjectLoadWorkflowRefreshHarness.CreateServices();
		var first = services.Engine.ComputeFullRefreshSnapshot(
			ProjectLoadWorkflowRefreshHarness.CreateDefaultContext(rootPath),
			TestContext.Current.CancellationToken);
		var converged = services.Engine.ComputeFullRefreshSnapshot(
			ProjectLoadWorkflowRefreshHarness.CreateContextFromSnapshot(rootPath, first),
			TestContext.Current.CancellationToken);

		Assert.Equal(first.RootOptions, converged.RootOptions);
		Assert.Equal(first.EffectiveExtensionOptions, converged.EffectiveExtensionOptions);
		Assert.Equal(first.ControllerImpactCounts, converged.ControllerImpactCounts);
		Assert.Equal(first.IgnoreOptionCounts, converged.IgnoreOptionCounts);
		Assert.Equal(first.IgnoreOptions, converged.IgnoreOptions);
		return converged;
	}

	private static ProjectAnalysisService CreateProjectAnalysisService()
	{
		var localization = ProjectLoadWorkflowRuntime.CreateLocalizationService();

		return new ProjectAnalysisService(
			new ScanOptionsUseCase(new FileSystemScanner()),
			ProjectLoadWorkflowRuntime.CreateBuildTreeUseCase(),
			new FilterOptionSelectionService(),
			new IgnoreOptionsService(localization),
			ProjectLoadWorkflowRuntime.CreateIgnoreRulesService(),
			new TreeExportService(),
			new FileContentAnalyzer());
	}

	private static HashSet<string> RootSet(params string[] values) =>
		new(values, PathComparer.Default);

	private static HashSet<string> ExtensionSet(params string[] values) =>
		new(values, StringComparer.OrdinalIgnoreCase);

	private static List<string> FlattenRelativePaths(string rootPath, FileSystemNode root)
	{
		var paths = new List<string>();
		var pending = new Stack<FileSystemNode>();
		for (var index = root.Children.Count - 1; index >= 0; index--)
			pending.Push(root.Children[index]);

		while (pending.Count > 0)
		{
			var node = pending.Pop();
			paths.Add(Path.GetRelativePath(rootPath, node.FullPath).Replace('\\', '/'));
			for (var index = node.Children.Count - 1; index >= 0; index--)
				pending.Push(node.Children[index]);
		}

		paths.Sort(StringComparer.OrdinalIgnoreCase);
		return paths;
	}

	private static void InitializeIndex(string repositoryRoot, params string[] trackedPaths)
	{
		RunGit(repositoryRoot, "init", "--quiet");
		if (trackedPaths.Length > 0)
			RunGit(repositoryRoot, ["add", "-f", "--", .. trackedPaths]);
	}

	private static void EnsureGitAvailable()
	{
		var startInfo = CreateGitStartInfo(workingDirectory: null);
		startInfo.ArgumentList.Add("--version");
		Process? startedProcess;
		try
		{
			startedProcess = Process.Start(startInfo);
		}
		catch (System.ComponentModel.Win32Exception)
		{
			Assert.Skip("Git is not available in this test environment.");
			return;
		}

		using var process = startedProcess;
		if (process is null)
			Assert.Skip("Git is not available in this test environment.");
		process.StandardOutput.ReadToEnd();
		process.StandardError.ReadToEnd();
		if (!process.WaitForExit(10_000) || process.ExitCode != 0)
			Assert.Skip("Git is not available in this test environment.");
	}

	private static string RunGit(string workingDirectory, params string[] arguments)
	{
		var startInfo = CreateGitStartInfo(workingDirectory);
		foreach (var argument in arguments)
			startInfo.ArgumentList.Add(argument);

		using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start git.");
		var output = process.StandardOutput.ReadToEnd();
		var error = process.StandardError.ReadToEnd();
		if (!process.WaitForExit(20_000))
		{
			process.Kill(entireProcessTree: true);
			throw new TimeoutException("Git command did not complete within 20 seconds.");
		}

		Assert.True(process.ExitCode == 0, $"git failed ({process.ExitCode}): {error}{output}");
		return output;
	}

	private static ProcessStartInfo CreateGitStartInfo(string? workingDirectory) =>
		new("git")
		{
			WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			StandardOutputEncoding = Encoding.UTF8,
			StandardErrorEncoding = Encoding.UTF8
		};

	private static void AssertVisible(IReadOnlyCollection<string> paths, string path) =>
		Assert.Contains(path, paths, StringComparer.OrdinalIgnoreCase);

	private static void AssertHidden(IReadOnlyCollection<string> paths, string path) =>
		Assert.DoesNotContain(path, paths, StringComparer.OrdinalIgnoreCase);

	private sealed record TreeObservation(
		ProjectTreeInventorySnapshot Inventory,
		IReadOnlyList<string> Paths);
}
