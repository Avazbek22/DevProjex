namespace DevProjex.Tests.Integration;

public sealed partial class DeepGitWorkspaceEvidenceIntegrationTests
{
	[Fact]
	public void FullRefresh_DeepRealRepositorySwitchesGitModesWithoutChangingAvailability()
	{
		EnsureGitAvailable();
		using var temp = new TemporaryDirectory();
		var repositoryRoot = CreateDeepDirectory(temp.Path, "workspace", depth: 12);
		temp.CreateFile(
			Path.GetRelativePath(temp.Path, Path.Combine(repositoryRoot, ".gitignore")),
			"*.generated\n");
		temp.CreateFile(
			Path.GetRelativePath(temp.Path, Path.Combine(repositoryRoot, "src", "tracked.cs")),
			"tracked\n");
		temp.CreateFile(
			Path.GetRelativePath(temp.Path, Path.Combine(repositoryRoot, "src", "local.cs")),
			"untracked\n");
		temp.CreateFile(
			Path.GetRelativePath(temp.Path, Path.Combine(repositoryRoot, "src", "kept.generated")),
			"tracked despite ignore\n");
		temp.CreateFile(
			Path.GetRelativePath(temp.Path, Path.Combine(repositoryRoot, "src", "drop.generated")),
			"untracked and ignored\n");
		InitializeIndex(repositoryRoot, ".gitignore", "src/tracked.cs", "src/kept.generated");
		var services = ProjectLoadWorkflowRefreshHarness.CreateServices();

		var gitIgnoreSnapshot = services.Engine.ComputeFullRefreshSnapshot(
			ProjectLoadWorkflowRefreshHarness.CreateDefaultContext(temp.Path) with
			{
				CaptureTreeInventory = true
			},
			TestContext.Current.CancellationToken);
		var gitIgnoreFiles = BuildEffectiveFileSet(
			temp.Path,
			gitIgnoreSnapshot,
			services.IgnoreRulesService);

		AssertGitMode(gitIgnoreSnapshot, GitFilteringMode.RespectGitIgnore);
		AssertFileSuffixVisible(gitIgnoreFiles, "src/tracked.cs");
		AssertFileSuffixVisible(gitIgnoreFiles, "src/local.cs");
		AssertFileSuffixVisible(gitIgnoreFiles, "src/kept.generated");
		AssertFileSuffixHidden(gitIgnoreFiles, "src/drop.generated");

		var trackedOnlySnapshot = services.Engine.ComputeFullRefreshSnapshot(
			SetGitMode(temp.Path, gitIgnoreSnapshot, GitFilteringMode.TrackedFilesOnly),
			TestContext.Current.CancellationToken);
		var trackedOnlyFiles = BuildEffectiveFileSet(
			temp.Path,
			trackedOnlySnapshot,
			services.IgnoreRulesService);

		AssertGitMode(trackedOnlySnapshot, GitFilteringMode.TrackedFilesOnly);
		AssertFileSuffixVisible(trackedOnlyFiles, "src/tracked.cs");
		AssertFileSuffixVisible(trackedOnlyFiles, "src/kept.generated");
		AssertFileSuffixHidden(trackedOnlyFiles, "src/local.cs");
		AssertFileSuffixHidden(trackedOnlyFiles, "src/drop.generated");
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void FullRefresh_DeepTrackedOnlyProfileSurvivesFirstRefreshAndProjectsRealIndex(
		bool stateCacheIsComplete)
	{
		EnsureGitAvailable();
		using var temp = new TemporaryDirectory();
		var repositoryRoot = CreateDeepDirectory(temp.Path, "workspace", depth: 12);
		temp.CreateFile(
			Path.GetRelativePath(temp.Path, Path.Combine(repositoryRoot, "src", "tracked.cs")),
			"tracked\n");
		temp.CreateFile(
			Path.GetRelativePath(temp.Path, Path.Combine(repositoryRoot, "src", "local.cs")),
			"untracked\n");
		InitializeIndex(repositoryRoot, "src/tracked.cs");
		var services = ProjectLoadWorkflowRefreshHarness.CreateServices();
		var stateCache = stateCacheIsComplete
			? new Dictionary<IgnoreOptionId, bool>
			{
				[IgnoreOptionId.UseGitIgnore] = false,
				[IgnoreOptionId.TrackedGitFilesOnly] = true
			}
			: new Dictionary<IgnoreOptionId, bool>();
		var context = ProjectLoadWorkflowRefreshHarness.CreateDefaultContext(temp.Path) with
		{
			PreparedSelectionMode = PreparedSelectionMode.Profile,
			AllRootFoldersChecked = false,
			AllExtensionsChecked = false,
			RootSelectionInitialized = true,
			RootSelectionCache = new HashSet<string>(["workspace"], PathComparer.Default),
			RootOptionStateCache = new Dictionary<string, bool>(PathComparer.Default)
			{
				["workspace"] = true
			},
			ExtensionsSelectionInitialized = true,
			ExtensionsSelectionCache = new HashSet<string>(
				[".cs"],
				StringComparer.OrdinalIgnoreCase),
			ExtensionOptionStateCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
			{
				[".cs"] = true
			},
			IgnoreSelectionInitialized = true,
			IgnoreSelectionCache = new HashSet<IgnoreOptionId>
			{
				IgnoreOptionId.TrackedGitFilesOnly
			},
			IgnoreOptionStateCache = stateCache,
			IgnoreOptionStateCacheIsComplete = stateCacheIsComplete,
			CaptureTreeInventory = true
		};

		var snapshot = services.Engine.ComputeFullRefreshSnapshot(
			context,
			TestContext.Current.CancellationToken);
		var files = BuildEffectiveFileSet(temp.Path, snapshot, services.IgnoreRulesService);

		Assert.True(snapshot.GitEvidence.HasRepositoryBoundary);
		AssertGitMode(snapshot, GitFilteringMode.TrackedFilesOnly);
		AssertFileSuffixVisible(files, "src/tracked.cs");
		AssertFileSuffixHidden(files, "src/local.cs");
	}

	[Fact]
	public void FullRefresh_OpenedBelowRepositoryRootUsesAncestorIndexAndKeepsGitModesVisible()
	{
		EnsureGitAvailable();
		using var temp = new TemporaryDirectory();
		var repositoryRoot = temp.CreateDirectory("repository");
		temp.CreateFile("repository/src/tracked.cs", "tracked\n");
		temp.CreateFile("repository/src/local.cs", "untracked\n");
		InitializeIndex(repositoryRoot, "src/tracked.cs");
		var openedRoot = Path.Combine(repositoryRoot, "src");
		var services = ProjectLoadWorkflowRefreshHarness.CreateServices();
		var context = ProjectLoadWorkflowRefreshHarness.CreateDefaultContext(openedRoot) with
		{
			IgnoreSelectionInitialized = true,
			IgnoreSelectionCache = new HashSet<IgnoreOptionId>
			{
				IgnoreOptionId.TrackedGitFilesOnly
			},
			IgnoreOptionStateCache = new Dictionary<IgnoreOptionId, bool>
			{
				[IgnoreOptionId.UseGitIgnore] = false,
				[IgnoreOptionId.TrackedGitFilesOnly] = true
			},
			IgnoreOptionStateCacheIsComplete = true,
			CaptureTreeInventory = true
		};

		var snapshot = services.Engine.ComputeFullRefreshSnapshot(
			context,
			TestContext.Current.CancellationToken);
		var files = BuildEffectiveFileSet(openedRoot, snapshot, services.IgnoreRulesService);

		Assert.False(snapshot.GitEvidence.HasRepositoryBoundary);
		AssertGitMode(snapshot, GitFilteringMode.TrackedFilesOnly);
		Assert.Contains("tracked.cs", files);
		Assert.DoesNotContain("local.cs", files);
	}

	[Fact]
	public void FullRefresh_DeepRepositoryBeyondScopeProbeStillExposesBothGitModes()
	{
		using var temp = new TemporaryDirectory();
		var repositoryRoot = CreateDeepDirectory(temp.Path, "workspace", depth: 16);
		Directory.CreateDirectory(Path.Combine(repositoryRoot, ".git"));
		temp.CreateFile(
			Path.GetRelativePath(temp.Path, Path.Combine(repositoryRoot, "src", "App.cs")),
			"class App {}\n");
		var services = ProjectLoadWorkflowRefreshHarness.CreateServices();

		var boundedAvailability = services.IgnoreRulesService.GetIgnoreOptionsAvailability(
			temp.Path,
			["workspace"]);
		var snapshot = services.Engine.ComputeFullRefreshSnapshot(
			ProjectLoadWorkflowRefreshHarness.CreateDefaultContext(temp.Path),
			TestContext.Current.CancellationToken);

		Assert.False(boundedAvailability.IncludeTrackedGitFilesOnly);
		Assert.True(snapshot.GitEvidence.HasRepositoryBoundary);
		Assert.Contains(snapshot.IgnoreOptions, static option =>
			option.Id == IgnoreOptionId.UseGitIgnore && option.IsChecked);
		Assert.Contains(snapshot.IgnoreOptions, static option =>
			option.Id == IgnoreOptionId.TrackedGitFilesOnly && !option.IsChecked);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void WorkspaceScan_ProjectRootGitDirectoryOrWorktreeFileProducesEvidence(bool useGitFile)
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("src/App.cs", "class App {}\n");
		if (useGitFile)
			temp.CreateFile(".git", "gitdir: ../worktrees/sample\n");
		else
			temp.CreateDirectory(".git");

		var snapshot = Scan(temp.Path, ["src"]);

		Assert.True(snapshot.IgnoreSection.GitEvidence.HasRepositoryBoundary);
	}

	[Fact]
	public void WorkspaceScan_DeepWorktreeGitFileProducesEvidence()
	{
		using var temp = new TemporaryDirectory();
		var repositoryRoot = CreateDeepDirectory(temp.Path, "workspace", depth: 16);
		File.WriteAllText(
			Path.Combine(repositoryRoot, ".git"),
			"gitdir: ../../git-metadata/worktree\n");
		temp.CreateFile(
			Path.GetRelativePath(temp.Path, Path.Combine(repositoryRoot, "main.rs")),
			"fn main() {}\n");

		var snapshot = Scan(temp.Path, ["workspace"]);

		Assert.True(snapshot.IgnoreSection.GitEvidence.HasRepositoryBoundary);
	}

	[Fact]
	public void WorkspaceScan_OnlySelectedRootsContributeNestedRepositoryEvidence()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("plain/App.cs", "class App {}\n");
		var repositoryRoot = CreateDeepDirectory(temp.Path, "repository", depth: 12);
		Directory.CreateDirectory(Path.Combine(repositoryRoot, ".git"));
		temp.CreateFile(
			Path.GetRelativePath(temp.Path, Path.Combine(repositoryRoot, "main.go")),
			"package main\n");

		var plainOnly = Scan(temp.Path, ["plain"]);
		var repositoryOnly = Scan(temp.Path, ["repository"]);
		var plainAgain = Scan(temp.Path, ["plain"]);

		Assert.False(plainOnly.IgnoreSection.GitEvidence.HasRepositoryBoundary);
		Assert.True(repositoryOnly.IgnoreSection.GitEvidence.HasRepositoryBoundary);
		Assert.False(plainAgain.IgnoreSection.GitEvidence.HasRepositoryBoundary);
	}

	[Fact]
	public void FullRefresh_RemovedDeepRepositoryHidesGitModesWithoutStaleEvidence()
	{
		using var temp = new TemporaryDirectory();
		var repositoryRoot = CreateDeepDirectory(temp.Path, "workspace", depth: 12);
		var gitPath = Path.Combine(repositoryRoot, ".git");
		Directory.CreateDirectory(gitPath);
		temp.CreateFile(
			Path.GetRelativePath(temp.Path, Path.Combine(repositoryRoot, "src", "App.cs")),
			"class App {}\n");
		var services = ProjectLoadWorkflowRefreshHarness.CreateServices();
		var initial = services.Engine.ComputeFullRefreshSnapshot(
			ProjectLoadWorkflowRefreshHarness.CreateDefaultContext(temp.Path),
			TestContext.Current.CancellationToken);

		Directory.Delete(gitPath);
		var refreshed = services.Engine.ComputeFullRefreshSnapshot(
			ProjectLoadWorkflowRefreshHarness.CreateContextFromSnapshot(temp.Path, initial),
			TestContext.Current.CancellationToken);

		Assert.True(initial.GitEvidence.HasRepositoryBoundary);
		Assert.False(refreshed.GitEvidence.HasRepositoryBoundary);
		Assert.DoesNotContain(refreshed.IgnoreOptions, static option =>
			option.Id is IgnoreOptionId.UseGitIgnore or IgnoreOptionId.TrackedGitFilesOnly);
	}

	[Fact]
	public void FullRefresh_RootSelectionSwitchRecomputesGitEvidenceAndExtensions()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("plain/readme.md", "# Plain\n");
		var repositoryRoot = CreateDeepDirectory(temp.Path, "repository", depth: 12);
		Directory.CreateDirectory(Path.Combine(repositoryRoot, ".git"));
		temp.CreateFile(
			Path.GetRelativePath(temp.Path, Path.Combine(repositoryRoot, "src", "App.cs")),
			"class App {}\n");
		var services = ProjectLoadWorkflowRefreshHarness.CreateServices();
		var initial = services.Engine.ComputeFullRefreshSnapshot(
			ProjectLoadWorkflowRefreshHarness.CreateDefaultContext(temp.Path),
			TestContext.Current.CancellationToken);

		var plainOnly = services.Engine.ComputeFullRefreshSnapshot(
			SelectRoot(temp.Path, initial, "plain"),
			TestContext.Current.CancellationToken);
		var repositoryOnly = services.Engine.ComputeFullRefreshSnapshot(
			SelectRoot(temp.Path, plainOnly, "repository"),
			TestContext.Current.CancellationToken);

		Assert.False(plainOnly.GitEvidence.HasRepositoryBoundary);
		Assert.DoesNotContain(plainOnly.IgnoreOptions, static option =>
			option.Id is IgnoreOptionId.UseGitIgnore or IgnoreOptionId.TrackedGitFilesOnly);
		Assert.Contains(plainOnly.EffectiveExtensionOptions, static option => option.Name == ".md");
		Assert.DoesNotContain(plainOnly.EffectiveExtensionOptions, static option => option.Name == ".cs");

		Assert.True(repositoryOnly.GitEvidence.HasRepositoryBoundary);
		Assert.Contains(repositoryOnly.IgnoreOptions, static option =>
			option.Id == IgnoreOptionId.UseGitIgnore);
		Assert.Contains(repositoryOnly.IgnoreOptions, static option =>
			option.Id == IgnoreOptionId.TrackedGitFilesOnly);
		Assert.Contains(repositoryOnly.EffectiveExtensionOptions, static option => option.Name == ".cs");
		Assert.DoesNotContain(repositoryOnly.EffectiveExtensionOptions, static option => option.Name == ".md");
	}

	[Fact]
	public void FullRefresh_DisablingSmartIgnoreDiscoversRepositoryInNewlyVisibleRoot()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("package.json", "{}\n");
		temp.CreateFile("src/App.cs", "class App {}\n");
		var repositoryRoot = CreateDeepDirectory(temp.Path, "node_modules", depth: 12);
		Directory.CreateDirectory(Path.Combine(repositoryRoot, ".git"));
		temp.CreateFile(
			Path.GetRelativePath(temp.Path, Path.Combine(repositoryRoot, "index.js")),
			"export {};\n");
		var services = ProjectLoadWorkflowRefreshHarness.CreateServices();
		var initial = services.Engine.ComputeFullRefreshSnapshot(
			ProjectLoadWorkflowRefreshHarness.CreateDefaultContext(temp.Path),
			TestContext.Current.CancellationToken);

		var smartDisabled = services.Engine.ComputeFullRefreshSnapshot(
			SetIgnoreOption(temp.Path, initial, IgnoreOptionId.SmartIgnore, isChecked: false),
			TestContext.Current.CancellationToken);

		Assert.False(initial.GitEvidence.HasRepositoryBoundary);
		Assert.DoesNotContain(initial.RootOptions!, static option => option.Name == "node_modules");
		Assert.DoesNotContain(initial.IgnoreOptions, static option =>
			option.Id is IgnoreOptionId.UseGitIgnore or IgnoreOptionId.TrackedGitFilesOnly);
		Assert.True(smartDisabled.GitEvidence.HasRepositoryBoundary);
		Assert.Contains(smartDisabled.RootOptions!, static option =>
			option.Name == "node_modules" && option.IsChecked);
		Assert.Contains(smartDisabled.IgnoreOptions, static option =>
			option.Id == IgnoreOptionId.UseGitIgnore);
		Assert.Contains(smartDisabled.IgnoreOptions, static option =>
			option.Id == IgnoreOptionId.TrackedGitFilesOnly);
	}

	[Fact]
	public void FullRefresh_DisablingGitIgnoreDiscoversRepositoryInNewlyVisibleRoot()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(".gitignore", "vendor/\n");
		temp.CreateFile("src/App.cs", "class App {}\n");
		var repositoryRoot = CreateDeepDirectory(temp.Path, "vendor", depth: 12);
		Directory.CreateDirectory(Path.Combine(repositoryRoot, ".git"));
		temp.CreateFile(
			Path.GetRelativePath(temp.Path, Path.Combine(repositoryRoot, "main.py")),
			"print('ready')\n");
		var services = ProjectLoadWorkflowRefreshHarness.CreateServices();
		var initial = services.Engine.ComputeFullRefreshSnapshot(
			ProjectLoadWorkflowRefreshHarness.CreateDefaultContext(temp.Path),
			TestContext.Current.CancellationToken);

		var gitIgnoreDisabled = services.Engine.ComputeFullRefreshSnapshot(
			SetIgnoreOption(temp.Path, initial, IgnoreOptionId.UseGitIgnore, isChecked: false),
			TestContext.Current.CancellationToken);

		Assert.False(initial.GitEvidence.HasRepositoryBoundary);
		Assert.Contains(initial.IgnoreOptions, static option =>
			option.Id == IgnoreOptionId.UseGitIgnore && option.IsChecked);
		Assert.DoesNotContain(initial.RootOptions!, static option => option.Name == "vendor");
		Assert.True(gitIgnoreDisabled.GitEvidence.HasRepositoryBoundary);
		Assert.Contains(gitIgnoreDisabled.RootOptions!, static option =>
			option.Name == "vendor" && option.IsChecked);
		Assert.Contains(gitIgnoreDisabled.IgnoreOptions, static option =>
			option.Id == IgnoreOptionId.UseGitIgnore && !option.IsChecked);
		Assert.Contains(gitIgnoreDisabled.IgnoreOptions, static option =>
			option.Id == IgnoreOptionId.TrackedGitFilesOnly);
	}

	[Fact]
	public void FullRefresh_AddedDeepRepositoryExposesGitModesWithoutProjectRestart()
	{
		using var temp = new TemporaryDirectory();
		var repositoryRoot = CreateDeepDirectory(temp.Path, "workspace", depth: 12);
		temp.CreateFile(
			Path.GetRelativePath(temp.Path, Path.Combine(repositoryRoot, "src", "App.cs")),
			"class App {}\n");
		var services = ProjectLoadWorkflowRefreshHarness.CreateServices();
		var initial = services.Engine.ComputeFullRefreshSnapshot(
			ProjectLoadWorkflowRefreshHarness.CreateDefaultContext(temp.Path),
			TestContext.Current.CancellationToken);

		Directory.CreateDirectory(Path.Combine(repositoryRoot, ".git"));
		var refreshed = services.Engine.ComputeFullRefreshSnapshot(
			ProjectLoadWorkflowRefreshHarness.CreateContextFromSnapshot(temp.Path, initial),
			TestContext.Current.CancellationToken);

		Assert.False(initial.GitEvidence.HasRepositoryBoundary);
		Assert.DoesNotContain(initial.IgnoreOptions, static option =>
			option.Id is IgnoreOptionId.UseGitIgnore or IgnoreOptionId.TrackedGitFilesOnly);
		Assert.True(refreshed.GitEvidence.HasRepositoryBoundary);
		Assert.Contains(refreshed.IgnoreOptions, static option =>
			option.Id == IgnoreOptionId.UseGitIgnore);
		Assert.Contains(refreshed.IgnoreOptions, static option =>
			option.Id == IgnoreOptionId.TrackedGitFilesOnly);
	}

	[Fact]
	public void WorkspaceScan_ManyParallelRootsAggregateGitEvidenceWithoutSelectionLeakage()
	{
		using var temp = new TemporaryDirectory();
		const int rootCount = 48;
		const int repositoryRootIndex = 37;
		var selectedRoots = Enumerable.Range(0, rootCount)
			.Select(static index => $"root-{index:D2}")
			.ToArray();
		for (var index = 0; index < selectedRoots.Length; index++)
			temp.CreateFile($"{selectedRoots[index]}/src/file-{index:D2}.cs", $"class File{index:D2} {{}}\n");

		var repositoryRoot = CreateDeepDirectory(
			temp.Path,
			selectedRoots[repositoryRootIndex],
			depth: 10);
		Directory.CreateDirectory(Path.Combine(repositoryRoot, ".git"));
		var rootsWithoutRepository = selectedRoots
			.Where((_, index) => index != repositoryRootIndex)
			.ToArray();

		for (var iteration = 0; iteration < 4; iteration++)
		{
			Assert.True(Scan(temp.Path, selectedRoots)
				.IgnoreSection.GitEvidence.HasRepositoryBoundary);
			Assert.False(Scan(temp.Path, rootsWithoutRepository)
				.IgnoreSection.GitEvidence.HasRepositoryBoundary);
		}
	}

	[Fact]
	public void FullRefresh_ExtensionSelectionCannotHideDeepRepositoryAvailability()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("workspace/readme.md", "# Workspace\n");
		var repositoryRoot = CreateDeepDirectory(temp.Path, "workspace", depth: 12);
		Directory.CreateDirectory(Path.Combine(repositoryRoot, ".git"));
		temp.CreateFile(
			Path.GetRelativePath(temp.Path, Path.Combine(repositoryRoot, "src", "App.cs")),
			"class App {}\n");
		var services = ProjectLoadWorkflowRefreshHarness.CreateServices();
		var initial = services.Engine.ComputeFullRefreshSnapshot(
			ProjectLoadWorkflowRefreshHarness.CreateDefaultContext(temp.Path) with
			{
				CaptureTreeInventory = true
			},
			TestContext.Current.CancellationToken);
		var extensionStates = initial.ExtensionOptions.ToDictionary(
			static option => option.Name,
			static option => option.Name.Equals(".md", StringComparison.OrdinalIgnoreCase),
			StringComparer.OrdinalIgnoreCase);
		var markdownOnly = services.Engine.ComputeFullRefreshSnapshot(
			ProjectLoadWorkflowRefreshHarness.CreateContextFromSnapshot(temp.Path, initial) with
			{
				AllExtensionsChecked = false,
				ExtensionsSelectionInitialized = true,
				ExtensionsSelectionCache = new HashSet<string>(
					[".md"],
					StringComparer.OrdinalIgnoreCase),
				ExtensionOptionStateCache = extensionStates,
				CaptureTreeInventory = true
			},
			TestContext.Current.CancellationToken);
		var files = BuildEffectiveFileSet(temp.Path, markdownOnly, services.IgnoreRulesService);

		Assert.True(markdownOnly.GitEvidence.HasRepositoryBoundary);
		Assert.Contains(markdownOnly.IgnoreOptions, static option =>
			option.Id == IgnoreOptionId.UseGitIgnore);
		Assert.Contains(markdownOnly.IgnoreOptions, static option =>
			option.Id == IgnoreOptionId.TrackedGitFilesOnly);
		AssertFileSuffixVisible(files, "workspace/readme.md");
		AssertFileSuffixHidden(files, "src/App.cs");
	}

	[Fact]
	public void FullRefresh_GitLookalikeEntriesDoNotExposeGitModes()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("workspace/.github/workflows/build.yml", "name: build\n");
		temp.CreateFile("workspace/.git-backup/HEAD", "ref: refs/heads/main\n");
		temp.CreateFile("workspace/.gitmodules", "[submodule]\n");
		temp.CreateFile("workspace/.gitkeep", string.Empty);
		temp.CreateFile("workspace/.gitignore.sample", "*.tmp\n");
		temp.CreateFile("workspace/src/App.cs", "class App {}\n");
		var services = ProjectLoadWorkflowRefreshHarness.CreateServices();

		var snapshot = services.Engine.ComputeFullRefreshSnapshot(
			ProjectLoadWorkflowRefreshHarness.CreateDefaultContext(temp.Path),
			TestContext.Current.CancellationToken);

		Assert.False(snapshot.GitEvidence.HasRepositoryBoundary);
		Assert.DoesNotContain(snapshot.IgnoreOptions, static option =>
			option.Id is IgnoreOptionId.UseGitIgnore or IgnoreOptionId.TrackedGitFilesOnly);
	}

	[Fact]
	public void WorkspaceScan_GitMetadataSymlinkDoesNotExposeOrTraverseExternalRepository()
	{
		using var temp = new TemporaryDirectory();
		using var outside = new TemporaryDirectory();
		var repositoryRoot = CreateDeepDirectory(temp.Path, "workspace", depth: 10);
		outside.CreateFile("objects/external.cs", "class External {}\n");
		var gitPath = Path.Combine(repositoryRoot, ".git");
		CreateDirectorySymlinkOrSkip(gitPath, outside.Path);

		var snapshot = Scan(temp.Path, ["workspace"]);

		Assert.False(snapshot.IgnoreSection.GitEvidence.HasRepositoryBoundary);
		var inventory = Assert.IsType<ProjectTreeInventorySnapshot>(snapshot.TreeInventory);
		Assert.DoesNotContain(inventory.Entries, static entry =>
			entry.Name.Equals("external.cs", StringComparison.OrdinalIgnoreCase));
	}

	[Fact]
	public void WorkspaceScan_DirectorySymlinkContainingRepositoryDoesNotContributeEvidence()
	{
		using var temp = new TemporaryDirectory();
		using var outside = new TemporaryDirectory();
		temp.CreateFile("workspace/local.cs", "class Local {}\n");
		outside.CreateDirectory(".git");
		outside.CreateFile("external.cs", "class External {}\n");
		var linkedRepository = Path.Combine(temp.Path, "workspace", "linked-repository");
		CreateDirectorySymlinkOrSkip(linkedRepository, outside.Path);

		var snapshot = Scan(temp.Path, ["workspace"]);

		Assert.False(snapshot.IgnoreSection.GitEvidence.HasRepositoryBoundary);
		var inventory = Assert.IsType<ProjectTreeInventorySnapshot>(snapshot.TreeInventory);
		Assert.DoesNotContain(inventory.Entries, static entry =>
			entry.Name.Equals("external.cs", StringComparison.OrdinalIgnoreCase));
	}

	[Fact]
	public void FullRefresh_ProjectRootGitSymlinkDoesNotExposeGitModes()
	{
		using var temp = new TemporaryDirectory();
		using var outside = new TemporaryDirectory();
		temp.CreateFile("src/App.cs", "class App {}\n");
		outside.CreateFile("HEAD", "ref: refs/heads/main\n");
		var gitPath = Path.Combine(temp.Path, ".git");
		CreateDirectorySymlinkOrSkip(gitPath, outside.Path);

		var services = ProjectLoadWorkflowRefreshHarness.CreateServices();
		var snapshot = services.Engine.ComputeFullRefreshSnapshot(
			ProjectLoadWorkflowRefreshHarness.CreateDefaultContext(temp.Path),
			TestContext.Current.CancellationToken);

		Assert.False(snapshot.GitEvidence.HasRepositoryBoundary);
		Assert.DoesNotContain(snapshot.IgnoreOptions, static option =>
			option.Id is IgnoreOptionId.UseGitIgnore or IgnoreOptionId.TrackedGitFilesOnly);
	}

	[Fact]
	public void FullRefresh_AncestorGitSymlinkDoesNotExposeModesForOpenedSubdirectory()
	{
		using var temp = new TemporaryDirectory();
		using var outside = new TemporaryDirectory();
		var repositoryRoot = temp.CreateDirectory("repository");
		var openedRoot = temp.CreateDirectory("repository/src");
		temp.CreateFile("repository/src/App.cs", "class App {}\n");
		outside.CreateFile("HEAD", "ref: refs/heads/main\n");
		CreateDirectorySymlinkOrSkip(
			Path.Combine(repositoryRoot, ".git"),
			outside.Path);
		var services = ProjectLoadWorkflowRefreshHarness.CreateServices();

		var structuralAvailability = services.IgnoreRulesService.GetIgnoreOptionsAvailability(
			openedRoot,
			[]);
		var snapshot = services.Engine.ComputeFullRefreshSnapshot(
			ProjectLoadWorkflowRefreshHarness.CreateDefaultContext(openedRoot),
			TestContext.Current.CancellationToken);

		Assert.False(structuralAvailability.IncludeTrackedGitFilesOnly);
		Assert.False(snapshot.GitEvidence.HasRepositoryBoundary);
		Assert.DoesNotContain(snapshot.IgnoreOptions, static option =>
			option.Id is IgnoreOptionId.UseGitIgnore or IgnoreOptionId.TrackedGitFilesOnly);
	}

	[Fact]
	public void FullRefresh_AncestorGitJunctionDoesNotExposeModesForOpenedSubdirectory()
	{
		if (!OperatingSystem.IsWindows())
			Assert.Skip("Windows junctions are only available on Windows.");

		using var temp = new TemporaryDirectory();
		using var outside = new TemporaryDirectory();
		var repositoryRoot = temp.CreateDirectory("repository");
		var openedRoot = temp.CreateDirectory("repository/src");
		temp.CreateFile("repository/src/App.cs", "class App {}\n");
		outside.CreateFile("HEAD", "ref: refs/heads/main\n");
		var gitMetadataPath = Path.Combine(repositoryRoot, ".git");
		CreateWindowsJunctionOrSkip(gitMetadataPath, outside.Path);
		try
		{
			var services = ProjectLoadWorkflowRefreshHarness.CreateServices();

			var structuralAvailability = services.IgnoreRulesService.GetIgnoreOptionsAvailability(
				openedRoot,
				[]);
			var snapshot = services.Engine.ComputeFullRefreshSnapshot(
				ProjectLoadWorkflowRefreshHarness.CreateDefaultContext(openedRoot),
				TestContext.Current.CancellationToken);

			Assert.False(structuralAvailability.IncludeTrackedGitFilesOnly);
			Assert.False(snapshot.GitEvidence.HasRepositoryBoundary);
			Assert.DoesNotContain(snapshot.IgnoreOptions, static option =>
				option.Id is IgnoreOptionId.UseGitIgnore or IgnoreOptionId.TrackedGitFilesOnly);
		}
		finally
		{
			DeleteDirectoryLink(gitMetadataPath);
		}
	}

	private static ProjectWorkspaceScanSnapshot Scan(
		string rootPath,
		IReadOnlyCollection<string> selectedRoots)
	{
		var rules = new IgnoreRules(
			IgnoreHiddenFolders: false,
			IgnoreHiddenFiles: false,
			IgnoreDotFolders: false,
			IgnoreDotFiles: false,
			SmartIgnoredFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
			SmartIgnoredFiles: new HashSet<string>(StringComparer.OrdinalIgnoreCase));
		var result = new ScanOptionsUseCase(new FileSystemScanner())
			.GetProjectWorkspaceSnapshotForRootFolders(
				rootPath,
				selectedRoots,
				extensionDiscoveryRules: rules,
				effectiveRules: rules,
				effectiveExtensionPolicy: null,
				cancellationToken: TestContext.Current.CancellationToken);

		Assert.False(result.RootAccessDenied);
		Assert.False(result.HadAccessDenied);
		return result.Value;
	}

	private static SelectionRefreshContext SelectRoot(
		string rootPath,
		SelectionRefreshSnapshot snapshot,
		string selectedRoot)
	{
		var rootStates = Assert.IsAssignableFrom<IReadOnlyList<DevProjex.Application.Models.SelectionOption>>(
				snapshot.RootOptions)
			.ToDictionary(
				static option => option.Name,
				option => PathComparer.Default.Equals(option.Name, selectedRoot),
				PathComparer.Default);

		return ProjectLoadWorkflowRefreshHarness.CreateContextFromSnapshot(rootPath, snapshot) with
		{
			AllRootFoldersChecked = false,
			RootSelectionInitialized = true,
			RootSelectionCache = new HashSet<string>([selectedRoot], PathComparer.Default),
			RootOptionStateCache = rootStates
		};
	}

	private static SelectionRefreshContext SetIgnoreOption(
		string rootPath,
		SelectionRefreshSnapshot snapshot,
		IgnoreOptionId optionId,
		bool isChecked)
	{
		var selected = ProjectLoadWorkflowRefreshHarness.CollectCheckedIgnoreOptionIds(snapshot);
		if (isChecked)
			selected.Add(optionId);
		else
			selected.Remove(optionId);

		var states = new Dictionary<IgnoreOptionId, bool>(snapshot.IgnoreOptionStateCache)
		{
			[optionId] = isChecked
		};
		return ProjectLoadWorkflowRefreshHarness.CreateContextFromSnapshot(rootPath, snapshot) with
		{
			IgnoreSelectionInitialized = true,
			IgnoreSelectionCache = selected,
			IgnoreOptionStateCache = states,
			IgnoreOptionStateCacheIsComplete = true,
			IgnoreAllPreference = null
		};
	}

	private static SelectionRefreshContext SetGitMode(
		string rootPath,
		SelectionRefreshSnapshot snapshot,
		GitFilteringMode mode)
	{
		var selected = ProjectLoadWorkflowRefreshHarness.CollectCheckedIgnoreOptionIds(snapshot);
		selected.Remove(IgnoreOptionId.UseGitIgnore);
		selected.Remove(IgnoreOptionId.TrackedGitFilesOnly);
		if (mode == GitFilteringMode.RespectGitIgnore)
			selected.Add(IgnoreOptionId.UseGitIgnore);
		else if (mode == GitFilteringMode.TrackedFilesOnly)
			selected.Add(IgnoreOptionId.TrackedGitFilesOnly);

		var states = new Dictionary<IgnoreOptionId, bool>(snapshot.IgnoreOptionStateCache)
		{
			[IgnoreOptionId.UseGitIgnore] = mode == GitFilteringMode.RespectGitIgnore,
			[IgnoreOptionId.TrackedGitFilesOnly] = mode == GitFilteringMode.TrackedFilesOnly
		};
		return ProjectLoadWorkflowRefreshHarness.CreateContextFromSnapshot(rootPath, snapshot) with
		{
			IgnoreSelectionInitialized = true,
			IgnoreSelectionCache = selected,
			IgnoreOptionStateCache = states,
			IgnoreOptionStateCacheIsComplete = true,
			IgnoreAllPreference = null,
			CaptureTreeInventory = true
		};
	}

	private static HashSet<string> BuildEffectiveFileSet(
		string rootPath,
		SelectionRefreshSnapshot snapshot,
		IgnoreRulesService ignoreRulesService)
	{
		var selectedRoots = ProjectLoadWorkflowRefreshHarness.CollectCheckedRootNames(snapshot);
		var selectedExtensions = snapshot.EffectiveExtensionOptions
			.Where(static option => option.IsChecked)
			.Select(static option => option.Name)
			.ToHashSet(StringComparer.OrdinalIgnoreCase);
		var selectedIgnoreOptions =
			ProjectLoadWorkflowRefreshHarness.CollectCheckedIgnoreOptionIds(snapshot);
		var rules = ignoreRulesService.Build(rootPath, selectedIgnoreOptions, selectedRoots);
		var tree = new TreeBuilder().Build(
			Assert.IsType<ProjectTreeInventorySnapshot>(snapshot.TreeInventory),
			new TreeFilterOptions(selectedExtensions, selectedRoots, rules),
			TestContext.Current.CancellationToken);
		var files = new HashSet<string>(StringComparer.Ordinal);
		var pending = new Stack<FileSystemNode>(tree.Root.Children.Reverse());
		while (pending.Count > 0)
		{
			var node = pending.Pop();
			if (!node.IsDirectory)
			{
				files.Add(
					Path.GetRelativePath(rootPath, node.FullPath)
						.Replace('\\', '/'));
			}

			for (var index = node.Children.Count - 1; index >= 0; index--)
				pending.Push(node.Children[index]);
		}

		return files;
	}

	private static void AssertGitMode(
		SelectionRefreshSnapshot snapshot,
		GitFilteringMode expectedMode)
	{
		var useGitIgnore = Assert.Single(
			snapshot.IgnoreOptions,
			static option => option.Id == IgnoreOptionId.UseGitIgnore);
		var trackedOnly = Assert.Single(
			snapshot.IgnoreOptions,
			static option => option.Id == IgnoreOptionId.TrackedGitFilesOnly);

		Assert.Equal(expectedMode == GitFilteringMode.RespectGitIgnore, useGitIgnore.IsChecked);
		Assert.Equal(expectedMode == GitFilteringMode.TrackedFilesOnly, trackedOnly.IsChecked);
	}

	private static void AssertFileSuffixVisible(
		IEnumerable<string> files,
		string relativePathSuffix) =>
		Assert.Contains(
			files,
			path => path.EndsWith(relativePathSuffix, StringComparison.OrdinalIgnoreCase));

	private static void AssertFileSuffixHidden(
		IEnumerable<string> files,
		string relativePathSuffix) =>
		Assert.DoesNotContain(
			files,
			path => path.EndsWith(relativePathSuffix, StringComparison.OrdinalIgnoreCase));

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

	private static void RunGit(string workingDirectory, params string[] arguments)
	{
		var startInfo = CreateGitStartInfo(workingDirectory);
		foreach (var argument in arguments)
			startInfo.ArgumentList.Add(argument);

		using var process = Process.Start(startInfo) ??
		                    throw new InvalidOperationException("Could not start git.");
		var output = process.StandardOutput.ReadToEnd();
		var error = process.StandardError.ReadToEnd();
		if (!process.WaitForExit(20_000))
		{
			process.Kill(entireProcessTree: true);
			throw new TimeoutException("Git command did not complete within 20 seconds.");
		}

		Assert.True(process.ExitCode == 0, $"git failed ({process.ExitCode}): {error}{output}");
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

	private static void CreateDirectorySymlinkOrSkip(string linkPath, string targetPath)
	{
		try
		{
			Directory.CreateSymbolicLink(linkPath, targetPath);
			if (!File.GetAttributes(linkPath).HasFlag(FileAttributes.ReparsePoint))
				Assert.Skip("The created directory link is not reported as a reparse point.");
		}
		catch (Exception exception) when (
			exception is IOException or
			UnauthorizedAccessException or
			PlatformNotSupportedException)
		{
			Assert.Skip($"Symbolic links are unavailable in this environment: {exception.GetType().Name}");
		}
	}

	private static void CreateWindowsJunctionOrSkip(string junctionPath, string targetPath)
	{
		using var process = new Process
		{
			StartInfo = new ProcessStartInfo("cmd.exe")
			{
				UseShellExecute = false,
				CreateNoWindow = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true
			}
		};
		process.StartInfo.ArgumentList.Add("/c");
		process.StartInfo.ArgumentList.Add("mklink");
		process.StartInfo.ArgumentList.Add("/J");
		process.StartInfo.ArgumentList.Add(junctionPath);
		process.StartInfo.ArgumentList.Add(targetPath);

		try
		{
			process.Start();
			if (!process.WaitForExit(5_000))
			{
				process.Kill(entireProcessTree: true);
				Assert.Skip("Windows junction creation timed out.");
			}

			if (process.ExitCode != 0 ||
			    !Directory.Exists(junctionPath) ||
			    !File.GetAttributes(junctionPath).HasFlag(FileAttributes.ReparsePoint))
			{
				Assert.Skip("The test environment did not allow creating a Windows junction.");
			}
		}
		catch (Exception exception) when (
			exception is InvalidOperationException or
			IOException or
			System.ComponentModel.Win32Exception)
		{
			Assert.Skip($"Windows junction creation is unavailable: {exception.GetType().Name}.");
		}
	}

	private static void DeleteDirectoryLink(string path)
	{
		try
		{
			if (Directory.Exists(path))
				Directory.Delete(path);
		}
		catch (Exception exception) when (
			exception is IOException or UnauthorizedAccessException)
		{
			// The enclosing temporary workspace performs a final best-effort cleanup.
		}
	}

	private static string CreateDeepDirectory(string rootPath, string rootName, int depth)
	{
		var current = Path.Combine(rootPath, rootName);
		for (var index = 0; index < depth; index++)
			current = Path.Combine(current, $"d{index:D2}");

		Directory.CreateDirectory(current);
		return current;
	}
}
