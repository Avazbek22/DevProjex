using DevProjex.Application.Context;

namespace DevProjex.Tests.Integration;

public sealed class GitIgnoreTrackedIndexIntegrationTests
{
	[Fact]
	public void InvalidRepositoryMetadataDoesNotProduceAuthoritativeComparisonSemantics()
	{
		using var temp = new TemporaryDirectory();
		var repositoryRoot = temp.CreateDirectory("repo");
		var gitMetadataPath = Directory.CreateDirectory(Path.Combine(repositoryRoot, ".git")).FullName;

		var semantics = GitTrackedPathIndexCache.ResolvePathComparisonSemantics(gitMetadataPath);

		Assert.False(semantics.IsAuthoritative);
	}

	[Fact]
	public void TrackedOnlyModeMatchesIndexCasingOnCaseInsensitiveMacFileSystem()
	{
		if (!OperatingSystem.IsMacOS())
			Assert.Skip("This regression test targets case-insensitive macOS repository volumes.");

		EnsureGitAvailable();
		using var temp = new TemporaryDirectory();
		var repositoryRoot = temp.CreateDirectory("repo");
		var originalPath = temp.CreateFile("repo/TrackedCase.cs", "tracked");
		InitializeIndex(repositoryRoot, "TrackedCase.cs");
		var semantics = GitTrackedPathIndexCache.ResolvePathComparisonSemantics(
			Path.Combine(repositoryRoot, ".git"));
		if (!semantics.IgnoreCase)
			Assert.Skip("The macOS temporary directory is on a case-sensitive volume.");

		var intermediatePath = Path.Combine(repositoryRoot, "case-rename.tmp");
		var workingTreePath = Path.Combine(repositoryRoot, "trackedcase.cs");
		File.Move(originalPath, intermediatePath);
		File.Move(intermediatePath, workingTreePath);

		var tree = BuildTree(
			repositoryRoot,
			RootSet(),
			ExtensionSet(".cs"),
			CreateTrackedOnlyRules(repositoryRoot));

		AssertVisible(tree.Paths, "trackedcase.cs");
		Assert.Equal(1, Assert.Single(tree.Inventory.DiscoveredGitTrackedPathIndexes).Count);
	}

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

		Assert.Equal(0, Assert.Single(observation.Inventory.DiscoveredGitTrackedPathIndexes).Count);
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
	public async Task StagedGitlinkIsNotReportedAsAProjectFileScope()
	{
		EnsureGitAvailable();
		using var temp = new TemporaryDirectory();
		var outerRoot = temp.CreateDirectory("outer");
		var nestedRoot = temp.CreateDirectory("outer/submodule");
		temp.CreateFile("outer/submodule/nested.cs", "namespace Nested;\n");
		RunGit(nestedRoot, "init", "--quiet");
		RunGit(nestedRoot, "config", "user.email", "tests@devprojex.local");
		RunGit(nestedRoot, "config", "user.name", "DevProjex Tests");
		RunGit(nestedRoot, "add", "--", "nested.cs");
		RunGit(nestedRoot, "commit", "--quiet", "-m", "nested seed");
		var nestedCommit = RunGit(nestedRoot, "rev-parse", "HEAD").Trim();
		RunGit(outerRoot, "init", "--quiet");
		RunGit(
			outerRoot,
			"update-index",
			"--add",
			"--cacheinfo",
			$"160000,{nestedCommit},submodule");

		var result = await new GitScopePathProvider().ResolveAsync(
			outerRoot,
			GitFilteringMode.Staged,
			diffRange: null,
			TestContext.Current.CancellationToken);

		Assert.True(result.IsAvailable, result.FailureReason);
		Assert.Empty(result.IncludedPaths);
		Assert.Equal(0, result.DeletedPathCount);
	}

	[Fact]
	public async Task RemovedGitlinkWithAnExistingWorktreeDirectoryIsNotReportedAsADeletedFile()
	{
		EnsureGitAvailable();
		using var temp = new TemporaryDirectory();
		var outerRoot = temp.CreateDirectory("outer");
		var nestedRoot = temp.CreateDirectory("outer/submodule");
		temp.CreateFile("outer/submodule/nested.cs", "namespace Nested;\n");
		InitializeCommittedRepository(nestedRoot, "nested.cs");
		var nestedCommit = RunGit(nestedRoot, "rev-parse", "HEAD").Trim();
		RunGit(outerRoot, "init", "--quiet");
		RunGit(outerRoot, "config", "user.email", "tests@devprojex.local");
		RunGit(outerRoot, "config", "user.name", "DevProjex Tests");
		RunGit(
			outerRoot,
			"update-index",
			"--add",
			"--cacheinfo",
			$"160000,{nestedCommit},submodule");
		RunGit(outerRoot, "commit", "--quiet", "-m", "add gitlink");
		RunGit(outerRoot, "rm", "--cached", "--quiet", "-f", "--", "submodule");

		var result = await new GitScopePathProvider().ResolveAsync(
			outerRoot,
			GitFilteringMode.Staged,
			diffRange: null,
			TestContext.Current.CancellationToken);

		Assert.True(result.IsAvailable, result.FailureReason);
		Assert.Empty(result.IncludedPaths);
		Assert.Equal(0, result.DeletedPathCount);
	}

	[Fact]
	public async Task DeletedPathTrackedByOverlappingRepositoriesIsReportedOnce()
	{
		EnsureGitAvailable();
		using var temp = new TemporaryDirectory();
		var outerRoot = temp.CreateDirectory("outer");
		var nestedRoot = temp.CreateDirectory("outer/nested");
		temp.CreateFile("outer/nested/shared.cs", "class Shared {}\n");
		InitializeCommittedRepository(nestedRoot, "shared.cs");
		RunGit(outerRoot, "init", "--quiet");
		RunGit(outerRoot, "config", "user.email", "tests@devprojex.local");
		RunGit(outerRoot, "config", "user.name", "DevProjex Tests");
		var blob = RunGit(outerRoot, "hash-object", "-w", "--", "nested/shared.cs").Trim();
		RunGit(
			outerRoot,
			"update-index",
			"--add",
			"--cacheinfo",
			$"100644,{blob},nested/shared.cs");
		RunGit(outerRoot, "commit", "--quiet", "-m", "track nested file");
		RunGit(nestedRoot, "rm", "--quiet", "--", "shared.cs");
		RunGit(outerRoot, "add", "-u", "--", "nested/shared.cs");

		var result = await new GitScopePathProvider().ResolveAsync(
			outerRoot,
			GitFilteringMode.Staged,
			diffRange: null,
			[outerRoot, nestedRoot],
			TestContext.Current.CancellationToken);

		Assert.True(result.IsAvailable, result.FailureReason);
		Assert.Empty(result.IncludedPaths);
		Assert.Equal(1, result.DeletedPathCount);
	}

	[Fact]
	public async Task TypeChangedFileReplacedByAGitlinkDirectoryIsReportedAsUnsupported()
	{
		EnsureGitAvailable();
		using var temp = new TemporaryDirectory();
		var repositoryRoot = temp.CreateDirectory("repo");
		temp.CreateFile("repo/component", "regular file\n");
		InitializeCommittedRepository(repositoryRoot, "component");
		File.Delete(Path.Combine(repositoryRoot, "component"));
		var nestedRoot = temp.CreateDirectory("repo/component");
		temp.CreateFile("repo/component/nested.cs", "class Nested {}\n");
		InitializeCommittedRepository(nestedRoot, "nested.cs");
		RunGit(repositoryRoot, "add", "--", "component");

		var result = await new GitScopePathProvider().ResolveAsync(
			repositoryRoot,
			GitFilteringMode.Staged,
			diffRange: null,
			TestContext.Current.CancellationToken);

		Assert.True(result.IsAvailable, result.FailureReason);
		Assert.Empty(result.IncludedPaths);
		Assert.Equal(1, result.DeletedPathCount);
	}

	[Fact]
	public async Task CaseSensitiveRepositoryKeepsCaseDistinctGitPaths()
	{
		EnsureGitAvailable();
		using var temp = new TemporaryDirectory();
		var repositoryRoot = temp.CreateDirectory("repo");
		File.WriteAllText(Path.Combine(repositoryRoot, "Foo.cs"), "class Upper {}\n");
		File.WriteAllText(Path.Combine(repositoryRoot, "foo.cs"), "class Lower {}\n");
		var caseVariants = Directory
			.EnumerateFiles(repositoryRoot, "*.cs", SearchOption.TopDirectoryOnly)
			.Select(Path.GetFileName)
			.ToHashSet(StringComparer.Ordinal);
		if (caseVariants.Count < 2)
			Assert.Skip("The temporary filesystem is not case-sensitive.");

		RunGit(repositoryRoot, "init", "--quiet");
		RunGit(repositoryRoot, "config", "core.ignorecase", "false");
		RunGit(repositoryRoot, "add", "--", "Foo.cs", "foo.cs");

		var result = await new GitScopePathProvider().ResolveAsync(
			repositoryRoot,
			GitFilteringMode.Staged,
			diffRange: null,
			TestContext.Current.CancellationToken);

		Assert.True(result.IsAvailable, result.FailureReason);
		Assert.Equal(2, result.IncludedPaths.Count);
		Assert.Contains(Path.Combine(repositoryRoot, "Foo.cs"), result.IncludedPaths, StringComparer.Ordinal);
		Assert.Contains(Path.Combine(repositoryRoot, "foo.cs"), result.IncludedPaths, StringComparer.Ordinal);
	}

	[Fact]
	public async Task StagedFileRemovedFromWorkingTreeIsReportedAsDeleted()
	{
		EnsureGitAvailable();
		using var temp = new TemporaryDirectory();
		var repositoryRoot = temp.CreateDirectory("repo");
		var filePath = temp.CreateFile("repo/staged.txt", "staged\n");
		RunGit(repositoryRoot, "init", "--quiet");
		RunGit(repositoryRoot, "add", "--", "staged.txt");
		File.Delete(filePath);

		var result = await new GitScopePathProvider().ResolveAsync(
			repositoryRoot,
			GitFilteringMode.Staged,
			diffRange: null,
			TestContext.Current.CancellationToken);

		Assert.True(result.IsAvailable, result.FailureReason);
		Assert.Empty(result.IncludedPaths);
		Assert.Equal(1, result.DeletedPathCount);
	}

	[Fact]
	public async Task ChangesScopeDoesNotReportRecreatedDeletedPathAsOmitted()
	{
		EnsureGitAvailable();
		using var temp = new TemporaryDirectory();
		var repositoryRoot = temp.CreateDirectory("repo");
		temp.CreateFile("repo/recreated.txt", "committed\n");
		InitializeIndex(repositoryRoot, "recreated.txt");
		RunGit(repositoryRoot, "config", "user.email", "tests@devprojex.local");
		RunGit(repositoryRoot, "config", "user.name", "DevProjex Tests");
		RunGit(repositoryRoot, "commit", "--quiet", "-m", "seed");
		RunGit(repositoryRoot, "rm", "--quiet", "--", "recreated.txt");
		var recreatedPath = PathUtility.Normalize(
			temp.CreateFile("repo/recreated.txt", "working tree\n"));

		var result = await new GitScopePathProvider().ResolveAsync(
			repositoryRoot,
			GitFilteringMode.Changes,
			diffRange: null,
			TestContext.Current.CancellationToken);

		Assert.True(result.IsAvailable, result.FailureReason);
		Assert.Equal(recreatedPath, Assert.Single(result.IncludedPaths), PathComparer.Default);
		Assert.Equal(0, result.DeletedPathCount);
	}

	[Fact]
	public async Task ChangesScopeReconcilesRecreatedPathUsingRepositoryCaseSemantics()
	{
		EnsureGitAvailable();
		using var temp = new TemporaryDirectory();
		var repositoryRoot = temp.CreateDirectory("repo");
		temp.CreateFile("repo/MixedCase.txt", "committed\n");
		InitializeCommittedRepository(repositoryRoot, "MixedCase.txt");
		RunGit(repositoryRoot, "config", "core.ignorecase", "true");
		RunGit(repositoryRoot, "rm", "--quiet", "--", "MixedCase.txt");
		var recreatedPath = PathUtility.Normalize(
			temp.CreateFile("repo/mixedcase.txt", "working tree\n"));

		var result = await new GitScopePathProvider().ResolveAsync(
			repositoryRoot,
			GitFilteringMode.Changes,
			diffRange: null,
			TestContext.Current.CancellationToken);

		Assert.True(result.IsAvailable, result.FailureReason);
		Assert.True(result.ContainsPath(recreatedPath));
		Assert.Equal(0, result.DeletedPathCount);
	}

	[Fact]
	public async Task StagedScopeReadsCurrentContentForARecreatedDeletedPath()
	{
		EnsureGitAvailable();
		using var temp = new TemporaryDirectory();
		var repositoryRoot = temp.CreateDirectory("repo");
		temp.CreateFile("repo/recreated.txt", "committed\n");
		InitializeCommittedRepository(repositoryRoot, "recreated.txt");
		RunGit(repositoryRoot, "rm", "--quiet", "--", "recreated.txt");
		var recreatedPath = PathUtility.Normalize(
			temp.CreateFile("repo/recreated.txt", "working tree\n"));

		var result = await new GitScopePathProvider().ResolveAsync(
			repositoryRoot,
			GitFilteringMode.Staged,
			diffRange: null,
			TestContext.Current.CancellationToken);

		Assert.True(result.IsAvailable, result.FailureReason);
		Assert.Equal(recreatedPath, Assert.Single(result.IncludedPaths), PathComparer.Default);
		Assert.Equal(0, result.DeletedPathCount);
	}

	[Fact]
	public async Task StagedProjectPlanReadsCurrentContentForARecreatedDeletedPath()
	{
		EnsureGitAvailable();
		using var temp = new TemporaryDirectory();
		var repositoryRoot = temp.CreateDirectory("repo");
		temp.CreateFile("repo/recreated.txt", "committed\n");
		InitializeCommittedRepository(repositoryRoot, "recreated.txt");
		RunGit(repositoryRoot, "rm", "--quiet", "--", "recreated.txt");
		var recreatedPath = PathUtility.Normalize(
			temp.CreateFile("repo/recreated.txt", "working tree\n"));
		var planner = new ProjectContextPlanner(CreateProjectAnalysisService());

		var plan = await planner.BuildStructureAsync(
			new ProjectContextRequest(
				repositoryRoot,
				ProjectSelectionSpec.Standard with { GitMode = GitFilteringMode.Staged }),
			TestContext.Current.CancellationToken);
		var scopedPlan = await GitScopeFilter.ApplyAsync(
			planner,
			plan,
			new GitScopePathProvider(),
			TestContext.Current.CancellationToken);

		Assert.False(scopedPlan.HasErrors);
		Assert.Equal(recreatedPath, Assert.Single(scopedPlan.IncludedFiles), PathComparer.Default);
		Assert.DoesNotContain(
			scopedPlan.Diagnostics,
			static diagnostic => diagnostic.Code == GitScopeFilter.DeletedDiagnosticCode);
	}

	[Fact]
	public async Task DiffScopeReadsCurrentContentForARecreatedDeletedPath()
	{
		EnsureGitAvailable();
		using var temp = new TemporaryDirectory();
		var repositoryRoot = temp.CreateDirectory("repo");
		temp.CreateFile("repo/recreated.txt", "committed\n");
		InitializeCommittedRepository(repositoryRoot, "recreated.txt");
		var baseCommit = RunGit(repositoryRoot, "rev-parse", "HEAD").Trim();
		RunGit(repositoryRoot, "rm", "--quiet", "--", "recreated.txt");
		RunGit(repositoryRoot, "commit", "--quiet", "-m", "delete");
		var deletedCommit = RunGit(repositoryRoot, "rev-parse", "HEAD").Trim();
		var recreatedPath = PathUtility.Normalize(
			temp.CreateFile("repo/recreated.txt", "working tree\n"));

		var result = await new GitScopePathProvider().ResolveAsync(
			repositoryRoot,
			GitFilteringMode.Diff,
			$"{baseCommit}..{deletedCommit}",
			TestContext.Current.CancellationToken);

		Assert.True(result.IsAvailable, result.FailureReason);
		Assert.Equal(recreatedPath, Assert.Single(result.IncludedPaths), PathComparer.Default);
		Assert.Equal(0, result.DeletedPathCount);
	}

	[Fact]
	public async Task DiffProjectPlanKeepsAComparedPathThatIsAbsentFromTheCurrentIndex()
	{
		EnsureGitAvailable();
		using var temp = new TemporaryDirectory();
		var repositoryRoot = temp.CreateDirectory("repo");
		temp.CreateFile("repo/recreated.txt", "base\n");
		InitializeCommittedRepository(repositoryRoot, "recreated.txt");
		var baseCommit = RunGit(repositoryRoot, "rev-parse", "HEAD").Trim();
		File.WriteAllText(Path.Combine(repositoryRoot, "recreated.txt"), "compared\n");
		RunGit(repositoryRoot, "add", "--", "recreated.txt");
		RunGit(repositoryRoot, "commit", "--quiet", "-m", "compared");
		var comparedCommit = RunGit(repositoryRoot, "rev-parse", "HEAD").Trim();
		RunGit(repositoryRoot, "rm", "--quiet", "--", "recreated.txt");
		RunGit(repositoryRoot, "commit", "--quiet", "-m", "current deletion");
		var recreatedPath = PathUtility.Normalize(
			temp.CreateFile("repo/recreated.txt", "current working tree\n"));
		var planner = new ProjectContextPlanner(CreateProjectAnalysisService());

		var plan = await planner.BuildStructureAsync(
			new ProjectContextRequest(
				repositoryRoot,
				ProjectSelectionSpec.Standard with
				{
					GitMode = GitFilteringMode.Diff,
					GitDiffRange = $"{baseCommit}..{comparedCommit}"
				}),
			TestContext.Current.CancellationToken);
		var scopedPlan = await GitScopeFilter.ApplyAsync(
			planner,
			plan,
			new GitScopePathProvider(),
			TestContext.Current.CancellationToken);

		Assert.False(scopedPlan.HasErrors);
		Assert.Equal(recreatedPath, Assert.Single(scopedPlan.IncludedFiles), PathComparer.Default);
		Assert.DoesNotContain(
			scopedPlan.Diagnostics,
			static diagnostic => diagnostic.Code == GitScopeFilter.DeletedDiagnosticCode);
	}

	[Fact]
	public async Task CaseDistinctDotGitNameDoesNotCreateARepositoryBoundary()
	{
		using var temp = new TemporaryDirectory();
		var projectRoot = temp.CreateDirectory("project");
		temp.CreateDirectory("project/.GIT");
		temp.CreateFile("project/App.cs", "class App {}\n");
		if (Directory.Exists(Path.Combine(projectRoot, ".git")))
		{
			Assert.Skip("The temporary file system is case-insensitive.");
			return;
		}
		var planner = new ProjectContextPlanner(CreateProjectAnalysisService());

		var plan = await planner.BuildStructureAsync(
			new ProjectContextRequest(projectRoot, ProjectSelectionSpec.Standard),
			TestContext.Current.CancellationToken);

		Assert.False(plan.GitReadiness.HasRepositoryBoundary);
	}

	[Fact]
	public async Task StagedScopeUnionsFilesFromAllDiscoveredNestedRepositories()
	{
		EnsureGitAvailable();
		using var temp = new TemporaryDirectory();
		var workspaceRoot = temp.CreateDirectory("workspace");
		var firstRepository = temp.CreateDirectory("workspace/first");
		var secondRepository = temp.CreateDirectory("workspace/second");
		temp.CreateFile("workspace/first/App.cs", "first-v1\n");
		temp.CreateFile("workspace/second/App.cs", "second-v1\n");
		InitializeCommittedRepository(firstRepository, "App.cs");
		InitializeCommittedRepository(secondRepository, "App.cs");
		File.WriteAllText(Path.Combine(firstRepository, "App.cs"), "first-v2\n");
		File.WriteAllText(Path.Combine(secondRepository, "App.cs"), "second-v2\n");
		RunGit(firstRepository, "add", "--", "App.cs");
		RunGit(secondRepository, "add", "--", "App.cs");

		var result = await new GitScopePathProvider().ResolveAsync(
			workspaceRoot,
			GitFilteringMode.Staged,
			diffRange: null,
			[firstRepository, secondRepository],
			TestContext.Current.CancellationToken);

		Assert.True(result.IsAvailable, result.FailureReason);
		Assert.Equal(2, result.IncludedPaths.Count);
		Assert.Contains(PathUtility.Normalize(Path.Combine(firstRepository, "App.cs")), result.IncludedPaths);
		Assert.Contains(PathUtility.Normalize(Path.Combine(secondRepository, "App.cs")), result.IncludedPaths);
		Assert.Equal(0, result.DeletedPathCount);
	}

	[Theory]
	[InlineData(GitFilteringMode.Staged)]
	[InlineData(GitFilteringMode.Changes)]
	[InlineData(GitFilteringMode.Diff)]
	public async Task MomentaryScopeKeepsCaseDistinctSiblingRepositoryOwnership(
		GitFilteringMode mode)
	{
		EnsureGitAvailable();
		using var temp = new TemporaryDirectory();
		var workspaceRoot = temp.CreateDirectory("workspace");
		var upperRepository = temp.CreateDirectory("workspace/Repo");
		var lowerRepository = temp.CreateDirectory("workspace/repo");
		var upperFile = temp.CreateFile("workspace/Repo/Upper.cs", "upper-v1\n");
		var lowerFile = temp.CreateFile("workspace/repo/Lower.cs", "lower-v1\n");
		if (Directory.EnumerateDirectories(workspaceRoot).Count() < 2)
			Assert.Skip("The temporary file system is case-insensitive.");

		InitializeCommittedRepository(upperRepository, "Upper.cs");
		InitializeCommittedRepository(lowerRepository, "Lower.cs");
		File.WriteAllText(upperFile, "upper-v2\n");
		File.WriteAllText(lowerFile, "lower-v2\n");
		RunGit(upperRepository, "add", "--", "Upper.cs");
		RunGit(lowerRepository, "add", "--", "Lower.cs");
		if (mode == GitFilteringMode.Diff)
		{
			RunGit(upperRepository, "commit", "--quiet", "-m", "upper change");
			RunGit(lowerRepository, "commit", "--quiet", "-m", "lower change");
		}
		RunGit(upperRepository, "config", "core.ignorecase", "true");

		var result = await new GitScopePathProvider().ResolveAsync(
			workspaceRoot,
			mode,
			mode == GitFilteringMode.Diff ? "HEAD~1..HEAD" : null,
			[upperRepository, lowerRepository],
			TestContext.Current.CancellationToken);

		Assert.True(result.IsAvailable, result.FailureReason);
		Assert.Equal(2, result.IncludedPaths.Count);
		Assert.True(result.ContainsPath(PathUtility.Normalize(upperFile)));
		Assert.True(result.ContainsPath(PathUtility.Normalize(lowerFile)));
	}

	[Theory]
	[InlineData(GitFilteringMode.RespectGitIgnore)]
	[InlineData(GitFilteringMode.TrackedFilesOnly)]
	public async Task PersistentGitModesKeepCaseDistinctSiblingRepositoryIndexes(
		GitFilteringMode mode)
	{
		EnsureGitAvailable();
		using var temp = new TemporaryDirectory();
		var workspaceRoot = temp.CreateDirectory("workspace");
		var upperRepository = temp.CreateDirectory("workspace/Repo");
		var lowerRepository = temp.CreateDirectory("workspace/repo");
		var upperFile = temp.CreateFile("workspace/Repo/Upper.cs", "upper\n");
		var lowerFile = temp.CreateFile("workspace/repo/Lower.cs", "lower\n");
		temp.CreateFile("workspace/Repo/.gitignore", "*.cs\n");
		temp.CreateFile("workspace/repo/.gitignore", "*.cs\n");
		if (Directory.EnumerateDirectories(workspaceRoot).Count() < 2)
			Assert.Skip("The temporary file system is case-insensitive.");

		InitializeCommittedRepository(upperRepository, ".gitignore", "Upper.cs");
		InitializeCommittedRepository(lowerRepository, ".gitignore", "Lower.cs");
		RunGit(upperRepository, "config", "core.ignorecase", "true");
		var planner = new ProjectContextPlanner(CreateProjectAnalysisService());

		var plan = await planner.BuildStructureAsync(
			new ProjectContextRequest(
				workspaceRoot,
				ProjectSelectionSpec.Standard with { GitMode = mode }),
			TestContext.Current.CancellationToken);

		Assert.False(plan.HasErrors);
		Assert.Contains(PathUtility.Normalize(upperFile), plan.IncludedFiles, StringComparer.Ordinal);
		Assert.Contains(PathUtility.Normalize(lowerFile), plan.IncludedFiles, StringComparer.Ordinal);
	}

	[Fact]
	public async Task StagedScopeRejectsAmbiguousCaseAliasInTheWorkingTree()
	{
		EnsureGitAvailable();
		using var temp = new TemporaryDirectory();
		var repositoryRoot = temp.CreateDirectory("repo");
		var upperFile = temp.CreateFile("repo/Foo.cs", "upper-v1\n");
		var lowerFile = temp.CreateFile("repo/foo.cs", "lower-v1\n");
		if (Directory.EnumerateFiles(repositoryRoot, "*.cs").Count() < 2)
			Assert.Skip("The temporary file system is case-insensitive.");

		InitializeCommittedRepository(repositoryRoot, "Foo.cs", "foo.cs");
		File.WriteAllText(upperFile, "upper-v2\n");
		RunGit(repositoryRoot, "add", "--", "Foo.cs");
		RunGit(repositoryRoot, "config", "core.ignorecase", "true");
		var planner = new ProjectContextPlanner(CreateProjectAnalysisService());
		var plan = await planner.BuildStructureAsync(
			new ProjectContextRequest(
				repositoryRoot,
				ProjectSelectionSpec.Standard with { GitMode = GitFilteringMode.Staged }),
			TestContext.Current.CancellationToken);

		var scoped = await GitScopeFilter.ApplyAsync(
			planner,
			plan,
			new GitScopePathProvider(),
			TestContext.Current.CancellationToken);

		Assert.False(scoped.HasErrors);
		Assert.Equal(PathUtility.Normalize(upperFile), Assert.Single(scoped.IncludedFiles));
		Assert.DoesNotContain(PathUtility.Normalize(lowerFile), scoped.IncludedFiles, StringComparer.Ordinal);
	}

	[Theory]
	[InlineData(GitFilteringMode.Staged)]
	[InlineData(GitFilteringMode.Changes)]
	public async Task ProjectPlanCarriesNestedRepositoryEvidenceAndScopesAllRepositories(
		GitFilteringMode scopeMode)
	{
		EnsureGitAvailable();
		using var temp = new TemporaryDirectory();
		var workspaceRoot = temp.CreateDirectory("workspace");
		var firstRepository = temp.CreateDirectory("workspace/first");
		var secondRepository = temp.CreateDirectory("workspace/second");
		temp.CreateFile("workspace/first/App.cs", "first-v1\n");
		temp.CreateFile("workspace/second/App.cs", "second-v1\n");
		InitializeCommittedRepository(firstRepository, "App.cs");
		InitializeCommittedRepository(secondRepository, "App.cs");
		File.WriteAllText(Path.Combine(firstRepository, "App.cs"), "first-v2\n");
		File.WriteAllText(Path.Combine(secondRepository, "App.cs"), "second-v2\n");
		RunGit(firstRepository, "add", "--", "App.cs");
		RunGit(secondRepository, "add", "--", "App.cs");
		var planner = new ProjectContextPlanner(CreateProjectAnalysisService());

		var baseline = await planner.BuildStructureAsync(
			new ProjectContextRequest(workspaceRoot, ProjectSelectionSpec.Standard),
			TestContext.Current.CancellationToken);
		var scopedPlan = await planner.BuildStructureAsync(
			new ProjectContextRequest(
				workspaceRoot,
				ProjectSelectionSpec.Standard with { GitMode = scopeMode }),
			TestContext.Current.CancellationToken);
		scopedPlan = await GitScopeFilter.ApplyAsync(
			planner,
			scopedPlan,
			new GitScopePathProvider(),
			TestContext.Current.CancellationToken);

		Assert.True(baseline.GitReadiness.HasRepositoryBoundary);
		Assert.False(scopedPlan.HasErrors);
		Assert.Equal(2, scopedPlan.IncludedFiles.Count);
		Assert.Contains(
			PathUtility.Normalize(Path.Combine(firstRepository, "App.cs")),
			scopedPlan.IncludedFiles);
		Assert.Contains(
			PathUtility.Normalize(Path.Combine(secondRepository, "App.cs")),
			scopedPlan.IncludedFiles);
	}

	[Fact]
	public async Task ExplicitPathSelectionDoesNotQueryAnUnrelatedBrokenRepository()
	{
		EnsureGitAvailable();
		using var temp = new TemporaryDirectory();
		var workspaceRoot = temp.CreateDirectory("workspace");
		var selectedRepository = temp.CreateDirectory("workspace/selected");
		var selectedFile = temp.CreateFile("workspace/selected/App.cs", "v1\n");
		InitializeCommittedRepository(selectedRepository, "App.cs");
		File.WriteAllText(selectedFile, "v2\n");
		var brokenRepository = temp.CreateDirectory("workspace/broken");
		temp.CreateDirectory("workspace/broken/.git");
		temp.CreateFile("workspace/broken/Other.cs", "content\n");
		var planner = new ProjectContextPlanner(CreateProjectAnalysisService());
		var selection = ProjectSelectionSpec.Standard with
		{
			GitMode = GitFilteringMode.Changes,
			SelectedPaths = ["selected/App.cs"]
		};

		var plan = await planner.BuildStructureAsync(
			new ProjectContextRequest(workspaceRoot, selection),
			TestContext.Current.CancellationToken);
		var scopedPlan = await GitScopeFilter.ApplyAsync(
			planner,
			plan,
			new GitScopePathProvider(),
			TestContext.Current.CancellationToken);

		Assert.False(scopedPlan.HasErrors);
		Assert.Equal(PathUtility.Normalize(selectedFile), Assert.Single(scopedPlan.IncludedFiles));
		Assert.True(Directory.Exists(brokenRepository));
	}

	[Fact]
	public async Task StagedPresentationDoesNotAdvertiseFilesOutsideTheExplicitPathSelection()
	{
		EnsureGitAvailable();
		using var temp = new TemporaryDirectory();
		var repositoryRoot = temp.CreateDirectory("repo");
		var selectedFile = temp.CreateFile("repo/selected/App.cs", "v1\n");
		var outsideFile = temp.CreateFile("repo/outside/Only.xyz", "v1\n");
		InitializeCommittedRepository(repositoryRoot, "selected/App.cs", "outside/Only.xyz");
		File.WriteAllText(selectedFile, "v2\n");
		File.WriteAllText(outsideFile, "v2\n");
		RunGit(repositoryRoot, "add", "--", "selected/App.cs", "outside/Only.xyz");
		var planner = new ProjectContextPlanner(CreateProjectAnalysisService());
		var selection = ProjectSelectionSpec.Standard with
		{
			GitMode = GitFilteringMode.Staged,
			SelectedPaths = ["selected/App.cs"]
		};

		var plan = await planner.BuildWithIgnoreImpactCountsAsync(
			new ProjectContextRequest(repositoryRoot, selection),
			TestContext.Current.CancellationToken);
		var scopedPlan = await GitScopeFilter.ApplyAsync(
			planner,
			plan,
			new GitScopePathProvider(),
			TestContext.Current.CancellationToken);

		Assert.False(scopedPlan.HasErrors);
		Assert.Equal(PathUtility.Normalize(selectedFile), Assert.Single(scopedPlan.IncludedFiles));
		Assert.Contains(".cs", scopedPlan.AvailableExtensions, StringComparer.OrdinalIgnoreCase);
		Assert.DoesNotContain(".xyz", scopedPlan.AvailableExtensions, StringComparer.OrdinalIgnoreCase);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task ExplicitNestedSelectionDoesNotQueryTheContainingRepositoryForDiff(
		bool selectFile)
	{
		EnsureGitAvailable();
		using var temp = new TemporaryDirectory();
		var outerRepository = temp.CreateDirectory("outer");
		temp.CreateFile("outer/README.md", "outer\n");
		InitializeCommittedRepository(outerRepository, "README.md");
		var nestedRepository = temp.CreateDirectory("outer/nested");
		var nestedFile = temp.CreateFile("outer/nested/App.cs", "v1\n");
		InitializeCommittedRepository(nestedRepository, "App.cs");
		File.WriteAllText(nestedFile, "v2\n");
		RunGit(nestedRepository, "add", "--", "App.cs");
		RunGit(nestedRepository, "commit", "--quiet", "-m", "change");
		var planner = new ProjectContextPlanner(CreateProjectAnalysisService());
		var selection = ProjectSelectionSpec.Standard with
		{
			Roots = selectFile ? null : ["nested"],
			SelectedPaths = selectFile ? ["nested/App.cs"] : [],
			GitMode = GitFilteringMode.Diff,
			GitDiffRange = "HEAD~1..HEAD"
		};

		var plan = await planner.BuildStructureAsync(
			new ProjectContextRequest(outerRepository, selection),
			TestContext.Current.CancellationToken);
		var scopedPlan = await GitScopeFilter.ApplyAsync(
			planner,
			plan,
			new GitScopePathProvider(),
			TestContext.Current.CancellationToken);

		Assert.False(scopedPlan.HasErrors);
		Assert.Equal(PathUtility.Normalize(nestedFile), Assert.Single(scopedPlan.IncludedFiles));
	}

	[Fact]
	public async Task GitScopePathIdentityUsesRepositoryIgnoreCaseSemanticsOnEveryPlatform()
	{
		EnsureGitAvailable();
		using var temp = new TemporaryDirectory();
		var repositoryRoot = temp.CreateDirectory("repo");
		temp.CreateFile("repo/MixedCase.cs", "content\n");
		RunGit(repositoryRoot, "init", "--quiet");
		RunGit(repositoryRoot, "config", "core.ignorecase", "true");
		RunGit(repositoryRoot, "add", "--", "MixedCase.cs");

		var result = await new GitScopePathProvider().ResolveAsync(
			repositoryRoot,
			GitFilteringMode.Staged,
			diffRange: null,
			TestContext.Current.CancellationToken);

		Assert.True(result.IsAvailable, result.FailureReason);
		Assert.True(result.ContainsPath(Path.Combine(repositoryRoot, "mixedcase.cs")));
	}

	[Fact]
	public async Task GitScopePathIdentityNormalizesUnicodeOnMacOS()
	{
		if (!OperatingSystem.IsMacOS())
			Assert.Skip("This regression test targets Git's macOS precomposeunicode semantics.");

		EnsureGitAvailable();
		using var temp = new TemporaryDirectory();
		var repositoryRoot = temp.CreateDirectory("repo");
		const string decomposedName = "Cafe\u0301.cs";
		const string composedName = "Caf\u00e9.cs";
		temp.CreateFile($"repo/{decomposedName}", "content\n");
		RunGit(repositoryRoot, "init", "--quiet");
		RunGit(repositoryRoot, "config", "core.precomposeunicode", "true");
		RunGit(repositoryRoot, "add", "--", decomposedName);

		var result = await new GitScopePathProvider().ResolveAsync(
			repositoryRoot,
			GitFilteringMode.Staged,
			diffRange: null,
			TestContext.Current.CancellationToken);

		Assert.True(result.IsAvailable, result.FailureReason);
		Assert.True(result.ContainsPath(Path.Combine(repositoryRoot, composedName)));
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

	[Fact]
	public void TrackedOnlyModeProjectsTheIndexWhileGitIgnoreModeKeepsUntrackedNonIgnoredFiles()
	{
		EnsureGitAvailable();
		using var temp = new TemporaryDirectory();
		var repositoryRoot = temp.CreateDirectory("repo");
		temp.CreateFile("repo/.gitignore", "*.ignored\n");
		temp.CreateFile("repo/src/tracked.cs", "tracked");
		temp.CreateFile("repo/src/untracked.cs", "untracked");
		temp.CreateFile("repo/src/forced.ignored", "tracked despite ignore");
		temp.CreateFile("repo/src/untracked.ignored", "ignored");
		temp.CreateFile("repo/assets/данные.bin", "\0\u0001\u0002");
		InitializeIndex(
			repositoryRoot,
			".gitignore",
			"src/tracked.cs",
			"src/forced.ignored",
			"assets/данные.bin");
		var services = ProjectLoadWorkflowRefreshHarness.CreateServices();
		var gitIgnoreRules = services.IgnoreRulesService.Build(
			repositoryRoot,
			[IgnoreOptionId.UseGitIgnore],
			selectedRootFolders: null);
		var trackedOnlyRules = services.IgnoreRulesService.Build(
			repositoryRoot,
			[IgnoreOptionId.TrackedGitFilesOnly],
			selectedRootFolders: null);
		var roots = RootSet("src", "assets");
		var extensions = ExtensionSet(".cs", ".ignored", ".bin");

		var gitIgnoreTree = BuildTree(repositoryRoot, roots, extensions, gitIgnoreRules);
		var trackedOnlyTree = BuildTree(repositoryRoot, roots, extensions, trackedOnlyRules);
		var scanner = new FileSystemScanner();
		var trackedRoots = scanner.GetRootFolderNames(
			repositoryRoot,
			trackedOnlyRules,
			TestContext.Current.CancellationToken);
		var trackedExtensions = scanner.GetExtensions(
			repositoryRoot,
			trackedOnlyRules,
			TestContext.Current.CancellationToken);

		Assert.Equal(GitFilteringMode.RespectGitIgnore, gitIgnoreRules.GitFilteringMode);
		Assert.Equal(GitFilteringMode.TrackedFilesOnly, trackedOnlyRules.GitFilteringMode);
		AssertVisible(gitIgnoreTree.Paths, "src/tracked.cs");
		AssertVisible(gitIgnoreTree.Paths, "src/untracked.cs");
		AssertVisible(gitIgnoreTree.Paths, "src/forced.ignored");
		AssertHidden(gitIgnoreTree.Paths, "src/untracked.ignored");
		AssertVisible(trackedOnlyTree.Paths, "src/tracked.cs");
		AssertHidden(trackedOnlyTree.Paths, "src/untracked.cs");
		AssertVisible(trackedOnlyTree.Paths, "src/forced.ignored");
		AssertHidden(trackedOnlyTree.Paths, "src/untracked.ignored");
		AssertVisible(trackedOnlyTree.Paths, "assets/данные.bin");
		Assert.Equal(new[] { "assets", "src" }, trackedRoots.Value);
		Assert.Contains(".bin", trackedExtensions.Value);
		Assert.Contains(".cs", trackedExtensions.Value);
		Assert.Contains(".ignored", trackedExtensions.Value);
	}

	[Fact]
	public void TrackedOnlyModeReflectsStagedModifiedAndDeletedWorkingTreeState()
	{
		EnsureGitAvailable();
		using var temp = new TemporaryDirectory();
		var repositoryRoot = temp.CreateDirectory("repo");
		temp.CreateFile("repo/src/modified.txt", "before");
		temp.CreateFile("repo/src/deleted.txt", "delete me");
		InitializeIndex(repositoryRoot, "src/modified.txt", "src/deleted.txt");
		temp.CreateFile("repo/src/modified.txt", "after");
		temp.CreateFile("repo/src/staged.txt", "staged");
		temp.CreateFile("repo/src/untracked.txt", "untracked");
		RunGit(repositoryRoot, "add", "--", "src/staged.txt");
		File.Delete(Path.Combine(repositoryRoot, "src", "deleted.txt"));
		var rules = CreateTrackedOnlyRules(repositoryRoot);

		var tree = BuildTree(
			repositoryRoot,
			RootSet("src"),
			ExtensionSet(".txt"),
			rules);

		AssertVisible(tree.Paths, "src/modified.txt");
		AssertVisible(tree.Paths, "src/staged.txt");
		AssertHidden(tree.Paths, "src/deleted.txt");
		AssertHidden(tree.Paths, "src/untracked.txt");
	}

	[Fact]
	public void TrackedOnlyModeHandlesRootFilesAndPathologicalGitNamesWithoutSplittingIndexOutput()
	{
		EnsureGitAvailable();
		using var temp = new TemporaryDirectory();
		var repositoryRoot = temp.CreateDirectory("repo");
		var trackedPaths = new List<string>
		{
			"LICENSE",
			".editorconfig",
			"src/file with spaces.txt",
			"src/-leading-name.txt",
			"src/данные-ß.txt",
			"src/empty.txt"
		};
		if (!OperatingSystem.IsWindows())
		{
			trackedPaths.Add("src/line\nbreak.txt");
			trackedPaths.Add("src/tab\tname.txt");
		}

		foreach (var trackedPath in trackedPaths)
			temp.CreateFile(Path.Combine("repo", trackedPath), trackedPath == "src/empty.txt" ? string.Empty : "tracked");
		temp.CreateFile("repo/src/local with spaces.txt", "untracked");
		temp.CreateFile("repo/UNTRACKED", "untracked");
		InitializeIndex(repositoryRoot, [.. trackedPaths]);
		var rules = CreateTrackedOnlyRules(repositoryRoot);

		var tree = BuildTree(
			repositoryRoot,
			RootSet("src"),
			ExtensionSet(".txt", ".editorconfig"),
			rules);

		foreach (var trackedPath in trackedPaths)
			AssertVisible(tree.Paths, trackedPath.Replace('\\', '/'));
		AssertHidden(tree.Paths, "src/local with spaces.txt");
		AssertHidden(tree.Paths, "UNTRACKED");
		Assert.Equal(trackedPaths.Count, Assert.Single(tree.Inventory.DiscoveredGitTrackedPathIndexes).Count);
	}

	[Fact]
	public void TrackedOnlyModeReflectsIntentToAddAndStagedRename()
	{
		EnsureGitAvailable();
		using var temp = new TemporaryDirectory();
		var repositoryRoot = temp.CreateDirectory("repo");
		temp.CreateFile("repo/src/old-name.txt", "tracked");
		InitializeIndex(repositoryRoot, "src/old-name.txt");
		RunGit(repositoryRoot, "mv", "--", "src/old-name.txt", "src/new-name.txt");
		temp.CreateFile("repo/src/intent-to-add.txt", "present in the index without staged content");
		RunGit(repositoryRoot, "add", "-N", "--", "src/intent-to-add.txt");
		temp.CreateFile("repo/src/untracked.txt", "untracked");
		var rules = CreateTrackedOnlyRules(repositoryRoot);

		var tree = BuildTree(
			repositoryRoot,
			RootSet("src"),
			ExtensionSet(".txt"),
			rules);

		AssertVisible(tree.Paths, "src/new-name.txt");
		AssertVisible(tree.Paths, "src/intent-to-add.txt");
		AssertHidden(tree.Paths, "src/old-name.txt");
		AssertHidden(tree.Paths, "src/untracked.txt");
	}

	[Fact]
	public void TrackedOnlyModeOpenedBelowRepositoryRootUsesNearestIndex()
	{
		EnsureGitAvailable();
		using var temp = new TemporaryDirectory();
		var repositoryRoot = temp.CreateDirectory("repo");
		temp.CreateFile("repo/src/app.cs", "tracked");
		temp.CreateFile("repo/src/local.cs", "untracked");
		InitializeIndex(repositoryRoot, "src/app.cs");
		var openedRoot = Path.Combine(repositoryRoot, "src");
		var rules = CreateTrackedOnlyRules(openedRoot);

		var tree = BuildTree(
			openedRoot,
			RootSet(),
			ExtensionSet(".cs"),
			rules);

		AssertVisible(tree.Paths, "app.cs");
		AssertHidden(tree.Paths, "local.cs");
		Assert.Single(tree.Inventory.DiscoveredGitTrackedPathIndexes);
		Assert.Equal(
			PathUtility.Normalize(repositoryRoot),
			tree.Inventory.DiscoveredGitTrackedPathIndexes[0].RepositoryRootPath,
			PathComparer.Default);
	}

	[Fact]
	public void TrackedOnlyModeFindsDeepRepositoryInsideUntrackedOuterRepositoryDirectory()
	{
		EnsureGitAvailable();
		using var temp = new TemporaryDirectory();
		temp.CreateFile("outer-tracked.txt", "tracked by the outer repository");
		InitializeIndex(temp.Path, "outer-tracked.txt");
		var repositoryRelativePath = string.Join(
			Path.DirectorySeparatorChar,
			new[] { "workspace" }.Concat(Enumerable.Range(1, 12).Select(static index => $"d{index:D2}"))
				.Append("repo"));
		var repositoryRoot = temp.CreateDirectory(repositoryRelativePath);
		var trackedPath = Path.Combine(repositoryRelativePath, "src", "tracked.cs");
		var untrackedPath = Path.Combine(repositoryRelativePath, "src", "untracked.cs");
		temp.CreateFile(trackedPath, "tracked");
		temp.CreateFile(untrackedPath, "untracked");
		InitializeIndex(repositoryRoot, "src/tracked.cs");
		var rules = CreateTrackedOnlyRules(temp.Path);

		var tree = BuildTree(
			temp.Path,
			RootSet("workspace"),
			ExtensionSet(".cs"),
			rules);

		AssertVisible(
			tree.Paths,
			Path.Combine(repositoryRelativePath, "src", "tracked.cs").Replace('\\', '/'));
		AssertHidden(
			tree.Paths,
			Path.Combine(repositoryRelativePath, "src", "untracked.cs").Replace('\\', '/'));
		Assert.Equal(2, tree.Inventory.DiscoveredGitTrackedPathIndexes.Count);
	}

	[Fact]
	public void TrackedOnlyModeKeepsSiblingRepositoryIndexesIsolated()
	{
		EnsureGitAvailable();
		using var temp = new TemporaryDirectory();
		var alphaRoot = temp.CreateDirectory("alpha");
		var betaRoot = temp.CreateDirectory("beta");
		temp.CreateFile("alpha/shared.txt", "tracked in alpha");
		temp.CreateFile("alpha/local.txt", "untracked in alpha");
		temp.CreateFile("beta/shared.txt", "untracked in beta");
		temp.CreateFile("beta/other.txt", "tracked in beta");
		InitializeIndex(alphaRoot, "shared.txt");
		InitializeIndex(betaRoot, "other.txt");
		var rules = CreateTrackedOnlyRules(temp.Path);

		var tree = BuildTree(
			temp.Path,
			RootSet("alpha", "beta"),
			ExtensionSet(".txt"),
			rules);

		AssertVisible(tree.Paths, "alpha/shared.txt");
		AssertHidden(tree.Paths, "alpha/local.txt");
		AssertHidden(tree.Paths, "beta/shared.txt");
		AssertVisible(tree.Paths, "beta/other.txt");
		Assert.Equal(2, tree.Inventory.DiscoveredGitTrackedPathIndexes.Count);
	}

	[Fact]
	public void TrackedOnlyModeComposesRootAndExtensionSelectionsWithoutLeakingOtherTrackedPaths()
	{
		EnsureGitAvailable();
		using var temp = new TemporaryDirectory();
		var repositoryRoot = temp.CreateDirectory("repo");
		temp.CreateFile("repo/LICENSE", "tracked root file without extension");
		temp.CreateFile("repo/src/app.cs", "tracked selected extension");
		temp.CreateFile("repo/src/readme.md", "tracked excluded extension");
		temp.CreateFile("repo/src/local.cs", "untracked selected extension");
		temp.CreateFile("repo/docs/guide.cs", "tracked unselected root");
		InitializeIndex(
			repositoryRoot,
			"LICENSE",
			"src/app.cs",
			"src/readme.md",
			"docs/guide.cs");
		var rules = CreateTrackedOnlyRules(repositoryRoot);

		var tree = BuildTree(
			repositoryRoot,
			RootSet("src"),
			ExtensionSet(".cs"),
			rules);

		AssertVisible(tree.Paths, "LICENSE");
		AssertVisible(tree.Paths, "src/app.cs");
		AssertHidden(tree.Paths, "src/readme.md");
		AssertHidden(tree.Paths, "src/local.cs");
		AssertHidden(tree.Paths, "docs/guide.cs");
	}

	[Fact]
	public void TrackedOnlyModeMasksOuterIndexAtNestedRepositoryBoundaryWithoutReadableIndex()
	{
		EnsureGitAvailable();
		using var temp = new TemporaryDirectory();
		var outerRoot = temp.CreateDirectory("outer");
		temp.CreateFile("outer/nested/previously-outer-tracked.txt", "tracked only by the outer index");
		InitializeIndex(outerRoot, "nested/previously-outer-tracked.txt");
		var nestedRoot = Path.Combine(outerRoot, "nested");
		RunGit(nestedRoot, "init", "--quiet");
		temp.CreateFile("outer/nested/local.txt", "untracked in the nested repository");
		var rules = CreateTrackedOnlyRules(outerRoot);

		var tree = BuildTree(
			outerRoot,
			RootSet("nested"),
			ExtensionSet(".txt"),
			rules);

		AssertHidden(tree.Paths, "nested/previously-outer-tracked.txt");
		AssertHidden(tree.Paths, "nested/local.txt");
		var nestedBoundary = Assert.Single(
			tree.Inventory.DiscoveredGitTrackedPathIndexes,
			index => PathComparer.Default.Equals(index.RepositoryRootPath, nestedRoot));
		Assert.Equal(0, nestedBoundary.Count);
	}

	[Fact]
	public void TrackedOnlyModeReportsPartialReadinessWhenOuterIndexIsUnavailableAndNestedIndexLoads()
	{
		EnsureGitAvailable();
		using var temp = new TemporaryDirectory();
		var outerRoot = temp.CreateDirectory("outer");
		temp.CreateFile("outer/README.md", "tracked before the index becomes unreadable");
		InitializeIndex(outerRoot, "README.md");
		var nestedRoot = temp.CreateDirectory("outer/nested");
		temp.CreateFile("outer/nested/tracked.cs", "tracked in nested repository");
		InitializeIndex(nestedRoot, "tracked.cs");
		File.WriteAllBytes(Path.Combine(outerRoot, ".git", "index"), [0x44, 0x50, 0x58]);
		var rules = CreateTrackedOnlyRules(outerRoot);

		var tree = BuildTree(
			outerRoot,
			RootSet("nested"),
			ExtensionSet(".cs", ".md"),
			rules);
		var readiness = ProjectContextGitReadiness.Evaluate(
			GitFilteringMode.TrackedFilesOnly,
			tree.Inventory);

		AssertHidden(tree.Paths, "README.md");
		AssertVisible(tree.Paths, "nested/tracked.cs");
		Assert.Equal(2, tree.Inventory.DiscoveredGitTrackedPathIndexes.Count);
		Assert.Single(tree.Inventory.DiscoveredGitTrackedPathIndexes, static index => !index.IsAvailable);
		Assert.Equal(1, readiness.LoadedTrackedIndexCount);
		Assert.Equal(1, readiness.UnavailableTrackedIndexCount);
		Assert.True(readiness.IsReady);
		Assert.Equal(
			ProjectContextGitReadiness.PartialDiagnosticCode,
			readiness.CreateDiagnostic(outerRoot)?.Code);
	}

	[Fact]
	public void StandaloneGitIgnoreDoesNotInventUnavailableRepositoryBoundary()
	{
		EnsureGitAvailable();
		using var temp = new TemporaryDirectory();
		temp.CreateFile(".gitignore", "*.local\n");
		temp.CreateFile("loose.local", "not owned by a repository");
		var nestedRoot = temp.CreateDirectory("nested");
		temp.CreateFile("nested/tracked.cs", "tracked in nested repository");
		InitializeIndex(nestedRoot, "tracked.cs");
		var rules = CreateTrackedOnlyRules(temp.Path);

		var tree = BuildTree(
			temp.Path,
			RootSet("nested"),
			ExtensionSet(".cs", ".local"),
			rules);

		AssertHidden(tree.Paths, "loose.local");
		AssertVisible(tree.Paths, "nested/tracked.cs");
		var discovered = Assert.Single(tree.Inventory.DiscoveredGitTrackedPathIndexes);
		Assert.True(discovered.IsAvailable);
		Assert.Equal(PathUtility.Normalize(nestedRoot), discovered.RepositoryRootPath, PathComparer.Default);
	}

	[Fact]
	public void TrackedOnlyModeStillAppliesSmartAndOrdinaryFiltersAfterGitOwnership()
	{
		EnsureGitAvailable();
		using var temp = new TemporaryDirectory();
		var repositoryRoot = temp.CreateDirectory("repo");
		temp.CreateFile("repo/pyproject.toml", "[project]\nname = \"sample\"\n");
		temp.CreateFile("repo/src/app.py", "print('ok')");
		temp.CreateFile("repo/src/.secret.py", "secret");
		temp.CreateFile("repo/src/__pycache__/app.pyc", "compiled");
		InitializeIndex(
			repositoryRoot,
			"pyproject.toml",
			"src/app.py",
			"src/.secret.py",
			"src/__pycache__/app.pyc");
		var services = ProjectLoadWorkflowRefreshHarness.CreateServices();
		var rules = services.IgnoreRulesService.Build(
			repositoryRoot,
			[
				IgnoreOptionId.TrackedGitFilesOnly,
				IgnoreOptionId.SmartIgnore,
				IgnoreOptionId.DotFiles
			],
			selectedRootFolders: null);

		var tree = BuildTree(
			repositoryRoot,
			RootSet("src"),
			ExtensionSet(".py", ".pyc"),
			rules);

		AssertVisible(tree.Paths, "src/app.py");
		AssertHidden(tree.Paths, "src/.secret.py");
		AssertHidden(tree.Paths, "src/__pycache__/app.pyc");
	}

	[Fact]
	public void TrackedOnlyModeWithoutReadableIndexFailsClosed()
	{
		EnsureGitAvailable();
		using var temp = new TemporaryDirectory();
		var repositoryRoot = temp.CreateDirectory("repo");
		temp.CreateFile("repo/src/untracked.cs", "untracked");
		RunGit(repositoryRoot, "init", "--quiet");
		var rules = CreateTrackedOnlyRules(repositoryRoot);

		var tree = BuildTree(
			repositoryRoot,
			RootSet("src"),
			ExtensionSet(".cs"),
			rules);

		AssertHidden(tree.Paths, "src/untracked.cs");
		Assert.Equal(0, Assert.Single(tree.Inventory.DiscoveredGitTrackedPathIndexes).Count);
	}

	[Fact]
	public void GitFilteringModesComposeWithRootExtensionSmartAndDotSelectionsAcrossFullMatrix()
	{
		EnsureGitAvailable();
		using var temp = new TemporaryDirectory();
		var repositoryRoot = CreateSettingsIslandRepository(temp);
		var services = ProjectLoadWorkflowRefreshHarness.CreateServices();

		foreach (var gitMode in Enum.GetValues<GitFilteringMode>())
		{
			foreach (var selectAllPayloadRoots in new[] { false, true })
			{
				foreach (var selectAllPayloadExtensions in new[] { false, true })
				{
					foreach (var useSmartIgnore in new[] { false, true })
					{
						foreach (var ignoreDotFiles in new[] { false, true })
						{
							var context = CreateSettingsIslandContext(
								repositoryRoot,
								gitMode,
								selectAllPayloadRoots,
								selectAllPayloadExtensions,
								useSmartIgnore,
								ignoreDotFiles);
							var snapshot = services.Engine.ComputeFullRefreshSnapshot(
								context,
								TestContext.Current.CancellationToken);

							SelectionSnapshotContractAssertions.AssertAllSectionsConsistent(
								repositoryRoot,
								services.IgnoreRulesService,
								snapshot);
							AssertGitFilteringMode(snapshot, gitMode);

							var actualFiles = BuildEffectiveFileSet(
								repositoryRoot,
								snapshot,
								services.IgnoreRulesService,
								payloadOnly: true);
							var expectedFiles = BuildExpectedPayloadFileSet(
								gitMode,
								selectAllPayloadRoots,
								selectAllPayloadExtensions,
								useSmartIgnore,
								ignoreDotFiles);
							var scenario =
								$"git={gitMode}, rootsAll={selectAllPayloadRoots}, " +
								$"extensionsAll={selectAllPayloadExtensions}, smart={useSmartIgnore}, " +
								$"dotFiles={ignoreDotFiles}";

							Assert.True(
								expectedFiles.SetEquals(actualFiles),
								$"{scenario}. Expected=[{string.Join(", ", expectedFiles.Order())}], " +
								$"Actual=[{string.Join(", ", actualFiles.Order())}]");
						}
					}
				}
			}
		}
	}

	[Fact]
	public void TrackedOnlyProfileRoundTripRestoresAllSettingsSectionsAndExactTree()
	{
		EnsureGitAvailable();
		using var project = new TemporaryDirectory();
		using var appData = new TemporaryDirectory();
		var repositoryRoot = CreateSettingsIslandRepository(project);
		var store = new ProjectProfileStore(() => appData.Path);
		var profile = new ProjectSelectionProfile(
			SelectedRootFolders: ["api"],
			SelectedExtensions: [".cs"],
			SelectedIgnoreOptions:
			[
				IgnoreOptionId.TrackedGitFilesOnly,
				IgnoreOptionId.SmartIgnore,
				IgnoreOptionId.DotFiles
			],
			RootFolderStates: new Dictionary<string, bool>(PathComparer.Default)
			{
				["api"] = true,
				["web"] = false,
				["docs"] = false
			},
			ExtensionStates: new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
			{
				[".cs"] = true,
				[".dll"] = false,
				[".md"] = false,
				[".ignored"] = false,
				[".ts"] = false,
				[".js"] = false,
				[".csproj"] = false,
				[".json"] = false,
				[".gitignore"] = false
			},
			IgnoreOptionStates: Enum.GetValues<IgnoreOptionId>().ToDictionary(
				static optionId => optionId,
				static optionId => optionId is
					IgnoreOptionId.TrackedGitFilesOnly or
					IgnoreOptionId.SmartIgnore or
					IgnoreOptionId.DotFiles));

		store.SaveProfile(repositoryRoot, profile);

		Assert.True(store.TryLoadProfile(repositoryRoot, out var loaded));
		Assert.Contains(IgnoreOptionId.TrackedGitFilesOnly, loaded.SelectedIgnoreOptions);
		Assert.DoesNotContain(IgnoreOptionId.UseGitIgnore, loaded.SelectedIgnoreOptions);
		Assert.NotNull(loaded.IgnoreOptionStates);
		Assert.True(loaded.IgnoreOptionStates![IgnoreOptionId.TrackedGitFilesOnly]);
		Assert.False(loaded.IgnoreOptionStates[IgnoreOptionId.UseGitIgnore]);

		var services = ProjectLoadWorkflowRefreshHarness.CreateServices();
		var context = CreateProfileContext(repositoryRoot, loaded);
		var snapshot = services.Engine.ComputeFullRefreshSnapshot(
			context,
			TestContext.Current.CancellationToken);

		SelectionSnapshotContractAssertions.AssertAllSectionsConsistent(
			repositoryRoot,
			services.IgnoreRulesService,
			snapshot);
		AssertGitFilteringMode(snapshot, GitFilteringMode.TrackedFilesOnly);
		Assert.Equal(["api"], ProjectLoadWorkflowRefreshHarness.CollectCheckedRootNames(snapshot));
		Assert.Equal([".cs"], ProjectLoadWorkflowRefreshHarness.CollectCheckedExtensionNames(snapshot));
		Assert.Equal(
			new HashSet<string>(StringComparer.Ordinal) { "root.cs", "api/main.cs" },
			BuildEffectiveFileSet(
				repositoryRoot,
				snapshot,
				services.IgnoreRulesService,
				payloadOnly: false));
	}

	[Fact]
	public void TrackedOnlyProfileReopenChecksNewTrackedExtensionAndHidesNewUntrackedFile()
	{
		EnsureGitAvailable();
		using var project = new TemporaryDirectory();
		using var appData = new TemporaryDirectory();
		var repositoryRoot = project.CreateDirectory("repo");
		project.CreateFile("repo/src/App.cs", "class App {}\n");
		InitializeIndex(repositoryRoot, "src/App.cs");
		var store = new ProjectProfileStore(() => appData.Path);
		store.SaveProfile(repositoryRoot, CreateTrackedOnlyProfile());

		project.CreateFile("repo/src/NewFeature.ts", "export const ready = true;\n");
		project.CreateFile("repo/src/local-only.py", "print('untracked')\n");
		RunGit(repositoryRoot, "add", "--", "src/NewFeature.ts");

		Assert.True(store.TryLoadProfile(repositoryRoot, out var loaded));
		var services = ProjectLoadWorkflowRefreshHarness.CreateServices();
		var snapshot = services.Engine.ComputeFullRefreshSnapshot(
			CreateProfileContext(repositoryRoot, loaded),
			TestContext.Current.CancellationToken);

		AssertGitFilteringMode(snapshot, GitFilteringMode.TrackedFilesOnly);
		Assert.Contains(snapshot.EffectiveExtensionOptions, static option =>
			option.Name == ".ts" && option.IsChecked);
		Assert.DoesNotContain(snapshot.EffectiveExtensionOptions, static option =>
			option.Name == ".py");
		Assert.Equal(
			new HashSet<string>(StringComparer.Ordinal)
			{
				"src/App.cs",
				"src/NewFeature.ts"
			},
			BuildEffectiveFileSet(
				repositoryRoot,
				snapshot,
				services.IgnoreRulesService,
				payloadOnly: false));
	}

	[Fact]
	public void TrackedOnlyProfileReopenWithoutRepositoryEvidenceRemainsFailClosed()
	{
		EnsureGitAvailable();
		using var project = new TemporaryDirectory();
		using var appData = new TemporaryDirectory();
		var repositoryRoot = project.CreateDirectory("repo");
		project.CreateFile("repo/src/App.cs", "class App {}\n");
		project.CreateFile("repo/src/local-only.py", "print('untracked')\n");
		InitializeIndex(repositoryRoot, "src/App.cs");
		var store = new ProjectProfileStore(() => appData.Path);
		store.SaveProfile(repositoryRoot, CreateTrackedOnlyProfile());
		Assert.True(store.TryLoadProfile(repositoryRoot, out var loaded));

		Directory.Move(
			Path.Combine(repositoryRoot, ".git"),
			Path.Combine(project.Path, "detached-git"));
		var services = ProjectLoadWorkflowRefreshHarness.CreateServices();
		var snapshot = services.Engine.ComputeFullRefreshSnapshot(
			CreateProfileContext(repositoryRoot, loaded),
			TestContext.Current.CancellationToken);
		var readiness = ProjectContextGitReadiness.Evaluate(
			GitFilteringMode.TrackedFilesOnly,
			snapshot.TreeInventory);

		Assert.Contains(snapshot.IgnoreOptions, static option =>
			option.Id == IgnoreOptionId.TrackedGitFilesOnly && option.IsChecked);
		Assert.Contains(IgnoreOptionId.TrackedGitFilesOnly, snapshot.EffectiveIgnoreOptions);
		Assert.Empty(BuildEffectiveFileSet(
			repositoryRoot,
			snapshot,
			services.IgnoreRulesService,
			payloadOnly: false));
		Assert.False(readiness.IsReady);
		Assert.Equal(
			ProjectContextGitReadiness.UnavailableDiagnosticCode,
			readiness.CreateDiagnostic(repositoryRoot)?.Code);
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

	private static string CreateSettingsIslandRepository(TemporaryDirectory temp)
	{
		var repositoryRoot = temp.CreateDirectory("repo");
		temp.CreateFile("repo/.gitignore", "*.ignored\n");
		temp.CreateFile("repo/App.csproj", "<Project />\n");
		temp.CreateFile("repo/package.json", "{}\n");
		temp.CreateFile("repo/root.cs", "tracked root file");
		temp.CreateFile("repo/local-root.cs", "untracked root file");
		temp.CreateFile("repo/api/main.cs", "tracked");
		temp.CreateFile("repo/api/.secret.cs", "tracked dot file");
		temp.CreateFile("repo/api/bin/Debug/generated.dll", "tracked smart artifact");
		temp.CreateFile("repo/api/readme.md", "tracked");
		temp.CreateFile("repo/api/local.cs", "untracked");
		temp.CreateFile("repo/api/drop.ignored", "untracked and ignored");
		temp.CreateFile("repo/web/app.ts", "tracked");
		temp.CreateFile("repo/web/node_modules/pkg/index.js", "tracked smart artifact");
		temp.CreateFile("repo/web/local.ts", "untracked");
		temp.CreateFile("repo/docs/guide.md", "tracked");
		InitializeIndex(
			repositoryRoot,
			".gitignore",
			"App.csproj",
			"package.json",
			"root.cs",
			"api/main.cs",
			"api/.secret.cs",
			"api/bin/Debug/generated.dll",
			"api/readme.md",
			"web/app.ts",
			"web/node_modules/pkg/index.js",
			"docs/guide.md");
		return repositoryRoot;
	}

	private static ProjectSelectionProfile CreateTrackedOnlyProfile() =>
		new(
			SelectedRootFolders: ["src"],
			SelectedExtensions: [".cs"],
			SelectedIgnoreOptions: [IgnoreOptionId.TrackedGitFilesOnly],
			RootFolderStates: new Dictionary<string, bool>(PathComparer.Default)
			{
				["src"] = true
			},
			ExtensionStates: new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
			{
				[".cs"] = true
			},
			IgnoreOptionStates: Enum.GetValues<IgnoreOptionId>().ToDictionary(
				static optionId => optionId,
				static optionId => optionId == IgnoreOptionId.TrackedGitFilesOnly));

	private static SelectionRefreshContext CreateProfileContext(
		string repositoryRoot,
		ProjectSelectionProfile profile) =>
		ProjectLoadWorkflowRefreshHarness.CreateDefaultContext(repositoryRoot) with
		{
			PreparedSelectionMode = PreparedSelectionMode.Profile,
			AllRootFoldersChecked = false,
			AllExtensionsChecked = false,
			RootSelectionInitialized = true,
			RootSelectionCache = new HashSet<string>(profile.SelectedRootFolders, PathComparer.Default),
			RootOptionStateCache = profile.RootFolderStates,
			ExtensionsSelectionInitialized = true,
			ExtensionsSelectionCache = new HashSet<string>(
				profile.SelectedExtensions,
				StringComparer.OrdinalIgnoreCase),
			ExtensionOptionStateCache = profile.ExtensionStates,
			IgnoreSelectionInitialized = true,
			IgnoreSelectionCache = new HashSet<IgnoreOptionId>(profile.SelectedIgnoreOptions),
			IgnoreOptionStateCache = profile.IgnoreOptionStates ??
				profile.SelectedIgnoreOptions.ToDictionary(static optionId => optionId, static _ => true),
			IgnoreOptionStateCacheIsComplete = profile.IgnoreOptionStates is not null,
			CaptureTreeInventory = true
		};

	private static SelectionRefreshContext CreateSettingsIslandContext(
		string repositoryRoot,
		GitFilteringMode gitMode,
		bool selectAllPayloadRoots,
		bool selectAllPayloadExtensions,
		bool useSmartIgnore,
		bool ignoreDotFiles)
	{
		var underlayMode = GitScopeSelection.ToUnderlayMode(gitMode);
		var selectedRoots = selectAllPayloadRoots
			? RootSet("api", "web", "docs")
			: RootSet("api");
		var selectedExtensions = selectAllPayloadExtensions
			? ExtensionSet(".cs", ".dll", ".md", ".ignored", ".ts", ".js")
			: ExtensionSet(".cs");
		var selectedIgnoreOptions = new HashSet<IgnoreOptionId>();
		if (underlayMode == GitFilteringMode.RespectGitIgnore)
			selectedIgnoreOptions.Add(IgnoreOptionId.UseGitIgnore);
		else if (underlayMode == GitFilteringMode.TrackedFilesOnly)
			selectedIgnoreOptions.Add(IgnoreOptionId.TrackedGitFilesOnly);
		if (useSmartIgnore)
			selectedIgnoreOptions.Add(IgnoreOptionId.SmartIgnore);
		if (ignoreDotFiles)
			selectedIgnoreOptions.Add(IgnoreOptionId.DotFiles);

		var rootStates = new Dictionary<string, bool>(PathComparer.Default)
		{
			["api"] = true,
			["web"] = selectAllPayloadRoots,
			["docs"] = selectAllPayloadRoots
		};
		var extensionStates = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
		foreach (var extension in new[] { ".cs", ".dll", ".md", ".ignored", ".ts", ".js" })
			extensionStates[extension] = selectedExtensions.Contains(extension);
		var ignoreStates = Enum.GetValues<IgnoreOptionId>().ToDictionary(
			static optionId => optionId,
			selectedIgnoreOptions.Contains);

		return ProjectLoadWorkflowRefreshHarness.CreateDefaultContext(repositoryRoot) with
		{
			AllRootFoldersChecked = false,
			AllExtensionsChecked = false,
			RootSelectionInitialized = true,
			RootSelectionCache = selectedRoots,
			RootOptionStateCache = rootStates,
			ExtensionsSelectionInitialized = true,
			ExtensionsSelectionCache = selectedExtensions,
			ExtensionOptionStateCache = extensionStates,
			IgnoreSelectionInitialized = true,
			IgnoreSelectionCache = selectedIgnoreOptions,
			IgnoreOptionStateCache = ignoreStates,
			IgnoreOptionStateCacheIsComplete = true,
			CaptureTreeInventory = true
		};
	}

	private static HashSet<string> BuildExpectedPayloadFileSet(
		GitFilteringMode gitMode,
		bool selectAllPayloadRoots,
		bool selectAllPayloadExtensions,
		bool useSmartIgnore,
		bool ignoreDotFiles)
	{
		var underlayMode = GitScopeSelection.ToUnderlayMode(gitMode);
		var expected = new HashSet<string>(StringComparer.Ordinal)
		{
			"api/main.cs"
		};
		if (!ignoreDotFiles)
			expected.Add("api/.secret.cs");
		if (underlayMode != GitFilteringMode.TrackedFilesOnly)
			expected.Add("api/local.cs");

		if (selectAllPayloadExtensions)
		{
			expected.Add("api/readme.md");
			if (!useSmartIgnore)
				expected.Add("api/bin/Debug/generated.dll");
			if (underlayMode == GitFilteringMode.None)
				expected.Add("api/drop.ignored");
		}

		if (!selectAllPayloadRoots || !selectAllPayloadExtensions)
			return expected;

		expected.Add("web/app.ts");
		expected.Add("docs/guide.md");
		if (underlayMode != GitFilteringMode.TrackedFilesOnly)
			expected.Add("web/local.ts");
		if (!useSmartIgnore)
			expected.Add("web/node_modules/pkg/index.js");
		return expected;
	}

	private static HashSet<string> BuildEffectiveFileSet(
		string repositoryRoot,
		SelectionRefreshSnapshot snapshot,
		IgnoreRulesService ignoreRulesService,
		bool payloadOnly)
	{
		var selectedRoots = ProjectLoadWorkflowRefreshHarness.CollectCheckedRootNames(snapshot);
		var selectedExtensions = snapshot.EffectiveExtensionOptions
			.Where(static option => option.IsChecked)
			.Select(static option => option.Name)
			.ToHashSet(StringComparer.OrdinalIgnoreCase);
		var selectedIgnoreOptions =
			ProjectLoadWorkflowRefreshHarness.CollectCheckedIgnoreOptionIds(snapshot);
		var rules = ignoreRulesService.Build(repositoryRoot, selectedIgnoreOptions, selectedRoots);
		var tree = new TreeBuilder().Build(
			Assert.IsType<ProjectTreeInventorySnapshot>(snapshot.TreeInventory),
			new TreeFilterOptions(selectedExtensions, selectedRoots, rules),
			TestContext.Current.CancellationToken);
		var payloadRoots = new[] { "api/", "web/", "docs/" };
		var files = new HashSet<string>(StringComparer.Ordinal);
		var pending = new Stack<FileSystemNode>(tree.Root.Children.Reverse());
		while (pending.Count > 0)
		{
			var node = pending.Pop();
			var relativePath = Path.GetRelativePath(repositoryRoot, node.FullPath).Replace('\\', '/');
			if (!node.IsDirectory &&
			    (!payloadOnly ||
			     payloadRoots.Any(root => relativePath.StartsWith(root, StringComparison.Ordinal))))
			{
				files.Add(relativePath);
			}

			for (var index = node.Children.Count - 1; index >= 0; index--)
				pending.Push(node.Children[index]);
		}

		return files;
	}

	private static void AssertGitFilteringMode(
		SelectionRefreshSnapshot snapshot,
		GitFilteringMode expectedMode)
	{
		var expectedUnderlayMode = GitScopeSelection.ToUnderlayMode(expectedMode);
		var useGitIgnore = Assert.Single(
			snapshot.IgnoreOptions,
			static option => option.Id == IgnoreOptionId.UseGitIgnore);
		var trackedOnly = Assert.Single(
			snapshot.IgnoreOptions,
			static option => option.Id == IgnoreOptionId.TrackedGitFilesOnly);

		Assert.Equal(expectedUnderlayMode == GitFilteringMode.RespectGitIgnore, useGitIgnore.IsChecked);
		Assert.Equal(expectedUnderlayMode == GitFilteringMode.TrackedFilesOnly, trackedOnly.IsChecked);
		Assert.False(useGitIgnore.IsChecked && trackedOnly.IsChecked);
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

	private static IgnoreRules CreateTrackedOnlyRules(string rootPath)
	{
		var services = ProjectLoadWorkflowRefreshHarness.CreateServices();
		return services.IgnoreRulesService.Build(
			rootPath,
			[IgnoreOptionId.TrackedGitFilesOnly],
			selectedRootFolders: null);
	}

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

	private static void InitializeCommittedRepository(string repositoryRoot, params string[] trackedPaths)
	{
		InitializeIndex(repositoryRoot, trackedPaths);
		RunGit(repositoryRoot, "config", "user.email", "tests@devprojex.local");
		RunGit(repositoryRoot, "config", "user.name", "DevProjex Tests");
		RunGit(repositoryRoot, "commit", "--quiet", "-m", "seed");
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
