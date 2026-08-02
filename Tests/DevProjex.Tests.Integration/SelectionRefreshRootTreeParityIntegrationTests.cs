using DevProjex.Tests.Shared.ProjectLoadWorkflow;

namespace DevProjex.Tests.Integration;

public sealed class SelectionRefreshRootTreeParityIntegrationTests
{
	[Fact]
	public void FullRefresh_ManagedGitMetadataRoot_RemainsIsolatedWithoutChangingHybridSmartIgnore()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("App.csproj", "<Project />\n");
		temp.CreateFile(Path.Combine("src", "App.cs"), "class App {}\n");
		temp.CreateFile(Path.Combine(".git", "objects", "pack.dat"), "git metadata\n");
		temp.CreateFile(
			Path.Combine("generated-temp", "temp-build", "obj", "Release", "net10.0", "Generated.g.cs"),
			"generated\n");
		var services = ProjectLoadWorkflowRefreshHarness.CreateServices(
			transformRules: static rules => rules with { ExcludedRootFolderName = ".git" });

		var smartEnabled = services.Engine.ComputeFullRefreshSnapshot(
			ProjectLoadWorkflowRefreshHarness.CreateDefaultContext(temp.Path) with
			{
				CaptureTreeInventory = true
			},
			TestContext.Current.CancellationToken);

		Assert.Contains(smartEnabled.IgnoreOptions, option =>
			option.Id == IgnoreOptionId.SmartIgnore && option.IsChecked);
		Assert.Equal(["src"], smartEnabled.RootOptions!.Select(static option => option.Name));
		AssertRootOptionsMatchProjectedTree(temp.Path, services, smartEnabled);

		var allIgnoreDisabled = RefreshWithAllIgnoreOptionsDisabled(temp.Path, services, smartEnabled);

		Assert.DoesNotContain(allIgnoreDisabled.RootOptions!, option => option.Name == ".git");
		Assert.Contains(allIgnoreDisabled.RootOptions!, option =>
			option.Name == "generated-temp" && option.IsChecked);
		Assert.Contains(allIgnoreDisabled.RootOptions!, option =>
			option.Name == "src" && option.IsChecked);
		AssertRootOptionsMatchProjectedTree(temp.Path, services, allIgnoreDisabled);
	}

	[Fact]
	public void FullRefresh_RootProjection_ReusesPerRootScanWithoutSecondFilesystemTraversal()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("App.csproj", "<Project />\n");
		temp.CreateFile(Path.Combine("src", "App.cs"), "class App {}\n");
		Directory.CreateDirectory(Path.Combine(temp.Path, "empty-root"));
		var scanner = new CountingWorkspaceScanner();
		var services = ProjectLoadWorkflowRefreshHarness.CreateServices(scanner);

		var snapshot = services.Engine.ComputeFullRefreshSnapshot(
			ProjectLoadWorkflowRefreshHarness.CreateDefaultContext(temp.Path) with
			{
				CaptureTreeInventory = true
			},
			TestContext.Current.CancellationToken);

		Assert.Equal(1, scanner.WorkspaceScanCount);
		Assert.DoesNotContain(snapshot.RootOptions!, option => option.Name == "empty-root");
		Assert.Contains(snapshot.RootOptions!, option => option.Name == "src");
		AssertRootOptionsMatchProjectedTree(temp.Path, services, snapshot);
	}

    [Fact]
    public void ScopedControllerProjection_MultipleIgnoredRoots_DoesNotDuplicateVisibleRoots()
    {
        using var temp = new TemporaryDirectory();
        var candidates = new[] { "keep-a", "temp-a", "keep-b", "temp-b", "temp-c", "keep-c" };
        var rules = new IgnoreRules(
            IgnoreHiddenFolders: false,
            IgnoreHiddenFiles: false,
            IgnoreDotFolders: false,
            IgnoreDotFiles: false,
            SmartIgnoredFolders: new HashSet<string>(["temp-a", "temp-b", "temp-c"], PathComparer.Default),
            SmartIgnoredFiles: new HashSet<string>(PathComparer.Default))
        {
            UseSmartIgnore = true
        };

        var projected = RootFolderVisibilityProjection.ApplyScopedControllerRules(
            temp.Path,
            candidates,
            rules,
            TestContext.Current.CancellationToken);

        Assert.Equal(["keep-a", "keep-b", "keep-c"], projected);
        Assert.Equal(projected.Count, projected.Distinct(PathComparer.Default).Count());
    }

    [Fact]
    public void FullRefresh_PhysicallyEmptyRoot_FollowsEmptyFoldersAcrossToggleCycle()
    {
        using var temp = new TemporaryDirectory();
        temp.CreateFile("App.csproj", "<Project />\n");
        Directory.CreateDirectory(Path.Combine(temp.Path, "empty-root"));
        var services = ProjectLoadWorkflowRefreshHarness.CreateServices();

        var snapshot = services.Engine.ComputeFullRefreshSnapshot(
            ProjectLoadWorkflowRefreshHarness.CreateDefaultContext(temp.Path) with
            {
                CaptureTreeInventory = true
            },
            TestContext.Current.CancellationToken);

        Assert.Contains(snapshot.IgnoreOptions, option =>
            option.Id == IgnoreOptionId.EmptyFolders && option.IsChecked);
        Assert.DoesNotContain(snapshot.RootOptions!, option => option.Name == "empty-root");
        AssertRootOptionsMatchProjectedTree(temp.Path, services, snapshot);

        var emptyFoldersDisabled = RefreshWithAllIgnoreOptionsDisabled(temp.Path, services, snapshot);

        Assert.Contains(emptyFoldersDisabled.IgnoreOptions, option =>
            option.Id == IgnoreOptionId.EmptyFolders && !option.IsChecked);
        Assert.Contains(emptyFoldersDisabled.RootOptions!, option =>
            option.Name == "empty-root" && option.IsChecked);
        AssertRootOptionsMatchProjectedTree(temp.Path, services, emptyFoldersDisabled);
    }

    [Fact]
    public void FullRefresh_DisablingOnlyEmptyFolders_KeepsControllerOwnedRootsOutOfRootOptionsAndTree()
    {
        using var temp = new TemporaryDirectory();
        temp.CreateFile(
            ".gitignore",
            "artifacts/\ncodex/\npublish/\nTestResult*/\n!**/[Pp]ackages/build/\n");
        temp.CreateFile("App.csproj", "<Project />\n");
        temp.CreateFile(Path.Combine("src", "App.cs"), "class App {}\n");
        temp.CreateFile(Path.Combine("docs", "readme.md"), "docs\n");
        Directory.CreateDirectory(Path.Combine(temp.Path, "src", "empty-nested"));
        temp.CreateFile(Path.Combine("artifacts", "publish", "App.dll"), "artifact\n");
        temp.CreateFile(Path.Combine("codex", "rules", "default.rules"), "rules\n");
        temp.CreateFile(Path.Combine("publish", "App.dll"), "publish\n");
        temp.CreateFile(Path.Combine("TestResults", "results.trx"), "results\n");
        Directory.CreateDirectory(Path.Combine(temp.Path, "temp"));
        var scanner = new CountingWorkspaceScanner();
        var services = ProjectLoadWorkflowRefreshHarness.CreateServices(scanner);

        var defaults = services.Engine.ComputeFullRefreshSnapshot(
            ProjectLoadWorkflowRefreshHarness.CreateDefaultContext(temp.Path) with
            {
                CaptureTreeInventory = true
            },
            TestContext.Current.CancellationToken);

        AssertEmptyFoldersState(defaults, expectedChecked: true, expectedCount: 2);
        Assert.DoesNotContain(defaults.RootOptions!, option => option.Name == "temp");
        Assert.Equal(
            ["docs", "src"],
            defaults.RootOptions!.Select(static option => option.Name).Order(PathComparer.Default));
        AssertRootOptionsMatchProjectedTree(temp.Path, services, defaults);

        var scansBeforeToggle = scanner.WorkspaceScanCount;
        var ignoreStates = new Dictionary<IgnoreOptionId, bool>(defaults.IgnoreOptionStateCache)
        {
            [IgnoreOptionId.EmptyFolders] = false
        };
        var selectedIgnoreOptions = ProjectLoadWorkflowRefreshHarness.CollectCheckedIgnoreOptionIds(defaults);
        selectedIgnoreOptions.Remove(IgnoreOptionId.EmptyFolders);

        var emptyFoldersDisabled = services.Engine.ComputeFullRefreshSnapshot(
            ProjectLoadWorkflowRefreshHarness.CreateContextFromSnapshot(temp.Path, defaults) with
            {
                IgnoreSelectionInitialized = true,
                IgnoreSelectionCache = selectedIgnoreOptions,
                IgnoreOptionStateCache = ignoreStates,
                IgnoreOptionStateCacheIsComplete = true,
                IgnoreAllPreference = null,
                CaptureTreeInventory = true
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(1, scanner.WorkspaceScanCount - scansBeforeToggle);
        AssertEmptyFoldersState(emptyFoldersDisabled, expectedChecked: false, expectedCount: 2);
        Assert.Contains(emptyFoldersDisabled.IgnoreOptions, option =>
            option.Id == IgnoreOptionId.UseGitIgnore && option.IsChecked);
        Assert.True(emptyFoldersDisabled.ControllerImpactCounts.GitIgnore > 0);
        Assert.Contains(emptyFoldersDisabled.RootOptions!, option =>
            option.Name == "temp" && option.IsChecked);
        Assert.DoesNotContain(emptyFoldersDisabled.RootOptions!, option =>
            option.Name is "artifacts" or "codex" or "publish" or "TestResults");
        Assert.Equal(
            ["docs", "src", "temp"],
            emptyFoldersDisabled.RootOptions!.Select(static option => option.Name).Order(PathComparer.Default));
        AssertRootOptionsMatchProjectedTree(temp.Path, services, emptyFoldersDisabled);

        var scansBeforeReenable = scanner.WorkspaceScanCount;
        var reenabledIgnoreStates = new Dictionary<IgnoreOptionId, bool>(
            emptyFoldersDisabled.IgnoreOptionStateCache)
        {
            [IgnoreOptionId.EmptyFolders] = true
        };
        var reenabledIgnoreOptions = ProjectLoadWorkflowRefreshHarness
            .CollectCheckedIgnoreOptionIds(emptyFoldersDisabled);
        reenabledIgnoreOptions.Add(IgnoreOptionId.EmptyFolders);
        var emptyFoldersReenabled = services.Engine.ComputeFullRefreshSnapshot(
            ProjectLoadWorkflowRefreshHarness.CreateContextFromSnapshot(temp.Path, emptyFoldersDisabled) with
            {
                IgnoreSelectionInitialized = true,
                IgnoreSelectionCache = reenabledIgnoreOptions,
                IgnoreOptionStateCache = reenabledIgnoreStates,
                IgnoreOptionStateCacheIsComplete = true,
                IgnoreAllPreference = null,
                CaptureTreeInventory = true
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(1, scanner.WorkspaceScanCount - scansBeforeReenable);
        AssertEmptyFoldersState(emptyFoldersReenabled, expectedChecked: true, expectedCount: 2);
        Assert.DoesNotContain(emptyFoldersReenabled.RootOptions!, option => option.Name == "temp");
        Assert.Equal(
            ["docs", "src"],
            emptyFoldersReenabled.RootOptions!.Select(static option => option.Name).Order(PathComparer.Default));
        AssertRootOptionsMatchProjectedTree(temp.Path, services, emptyFoldersReenabled);
    }

    [Fact]
    public void FullRefresh_MultiLevelIgnoredAndVisibleRoots_MatchTreeAcrossAllIgnoreCycle()
    {
        using var temp = new TemporaryDirectory();
        temp.CreateFile("App.csproj", "<Project />\n");
        temp.CreateFile("root-noise.tmp", "not a directory\n");
        temp.CreateFile(Path.Combine("src", "level-1", "level-2", "App.cs"), "class App {}\n");
        temp.CreateFile(Path.Combine("docs", "level-1", "level-2", "readme.md"), "visible\n");
        Directory.CreateDirectory(Path.Combine(temp.Path, "ansel"));
        temp.CreateFile(Path.Combine("empty-file-root", "level-1", "level-2", "empty.cs"), string.Empty);
        temp.CreateFile(Path.Combine("extensionless-root", "level-1", "level-2", "LICENSE"), "license\n");
        temp.CreateFile(
            Path.Combine("artifact-root", "level-1", "temp-build", "obj", "Release", "net10.0", "Generated.g.cs"),
            "generated\n");
        var services = ProjectLoadWorkflowRefreshHarness.CreateServices();

        var defaults = services.Engine.ComputeFullRefreshSnapshot(
            ProjectLoadWorkflowRefreshHarness.CreateDefaultContext(temp.Path) with
            {
                CaptureTreeInventory = true
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(
            ["docs", "src"],
            defaults.RootOptions!.Select(static option => option.Name).Order(PathComparer.Default));
        AssertRootOptionsMatchProjectedTree(temp.Path, services, defaults);

        var allIgnoreDisabled = RefreshWithAllIgnoreOptionsDisabled(temp.Path, services, defaults);
        var expectedAllRoots = new[]
        {
            "ansel",
            "artifact-root",
            "docs",
            "empty-file-root",
            "extensionless-root",
            "src"
        };

        Assert.Equal(
            expectedAllRoots,
            allIgnoreDisabled.RootOptions!.Select(static option => option.Name).Order(PathComparer.Default));
        AssertRootOptionsMatchProjectedTree(temp.Path, services, allIgnoreDisabled);
    }

    [Fact]
    public void FullRefresh_ExtensionOnlyRoot_MatchesTreeAcrossExtensionToggleCycle()
    {
        using var temp = new TemporaryDirectory();
        temp.CreateFile("App.csproj", "<Project />\n");
        temp.CreateFile(Path.Combine("src", "level-1", "level-2", "App.cs"), "class App {}\n");
        temp.CreateFile(Path.Combine("docs", "level-1", "level-2", "readme.md"), "docs\n");
        var scanner = new CountingWorkspaceScanner();
        var services = ProjectLoadWorkflowRefreshHarness.CreateServices(scanner);

        var defaults = services.Engine.ComputeFullRefreshSnapshot(
            ProjectLoadWorkflowRefreshHarness.CreateDefaultContext(temp.Path) with
            {
                CaptureTreeInventory = true
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(
            ["docs", "src"],
            defaults.RootOptions!.Select(static option => option.Name).Order(PathComparer.Default));
        AssertRootOptionsMatchProjectedTree(temp.Path, services, defaults);

        var scansBeforeDisable = scanner.WorkspaceScanCount;
        var markdownDisabled = RefreshWithExtensionState(temp.Path, services, defaults, ".md", isChecked: false);

        Assert.Equal(1, scanner.WorkspaceScanCount - scansBeforeDisable);
        AssertEmptyFoldersState(markdownDisabled, expectedChecked: true, expectedCount: 3);
        Assert.Equal(["src"], markdownDisabled.RootOptions!.Select(static option => option.Name));
        AssertRootOptionsMatchProjectedTree(temp.Path, services, markdownDisabled);

        var scansBeforeEnable = scanner.WorkspaceScanCount;
        var markdownEnabled = RefreshWithExtensionState(
            temp.Path,
            services,
            markdownDisabled,
            ".md",
            isChecked: true);

        Assert.Equal(1, scanner.WorkspaceScanCount - scansBeforeEnable);
        Assert.Equal(
            ["docs", "src"],
            markdownEnabled.RootOptions!.Select(static option => option.Name).Order(PathComparer.Default));
        AssertRootOptionsMatchProjectedTree(temp.Path, services, markdownEnabled);
    }

    [Fact]
    public void FullRefresh_ArtifactWrapperRoot_FollowsProjectedTreeAcrossSmartIgnoreCycle()
    {
        using var temp = new TemporaryDirectory();
        temp.CreateFile("App.csproj", "<Project />\n");
        temp.CreateFile(Path.Combine("src", "App.cs"), "class App {}\n");
        var artifactRoots = new[]
        {
            "Infrastructure_artifacts_temp",
            "avaloniaTemp",
            "build-cache-temp",
            "generated-temp"
        };
        foreach (var artifactRoot in artifactRoots)
        {
            temp.CreateFile(
                Path.Combine(artifactRoot, "temp-build", "obj", "Release", "net10.0", "Generated.g.cs"),
                "generated\n");
        }
        var services = ProjectLoadWorkflowRefreshHarness.CreateServices();

        var smartEnabled = services.Engine.ComputeFullRefreshSnapshot(
            ProjectLoadWorkflowRefreshHarness.CreateDefaultContext(temp.Path) with
            {
                CaptureTreeInventory = true
            },
            TestContext.Current.CancellationToken);

        Assert.Contains(smartEnabled.IgnoreOptions, option =>
            option.Id == IgnoreOptionId.SmartIgnore && option.IsChecked);
        Assert.Equal(["src"], smartEnabled.RootOptions!.Select(static option => option.Name));
        Assert.Equal(
            smartEnabled.RootOptions!.Count,
            smartEnabled.RootOptions!.Select(static option => option.Name).Distinct(PathComparer.Default).Count());
        AssertRootOptionsMatchProjectedTree(temp.Path, services, smartEnabled);

        var ignoreStates = new Dictionary<IgnoreOptionId, bool>(smartEnabled.IgnoreOptionStateCache)
        {
            [IgnoreOptionId.SmartIgnore] = false
        };
        var selectedIgnoreOptions = ProjectLoadWorkflowRefreshHarness
            .CollectCheckedIgnoreOptionIds(smartEnabled);
        selectedIgnoreOptions.Remove(IgnoreOptionId.SmartIgnore);
        var smartDisabled = services.Engine.ComputeFullRefreshSnapshot(
            ProjectLoadWorkflowRefreshHarness.CreateContextFromSnapshot(temp.Path, smartEnabled) with
            {
                IgnoreSelectionInitialized = true,
                IgnoreSelectionCache = selectedIgnoreOptions,
                IgnoreOptionStateCache = ignoreStates,
                IgnoreOptionStateCacheIsComplete = true,
                IgnoreAllPreference = null,
                CaptureTreeInventory = true
            },
            TestContext.Current.CancellationToken);

        Assert.Contains(smartDisabled.IgnoreOptions, option =>
            option.Id == IgnoreOptionId.SmartIgnore && !option.IsChecked);
        Assert.Equal(
            artifactRoots.Append("src").Order(PathComparer.Default),
            smartDisabled.RootOptions!.Select(static option => option.Name).Order(PathComparer.Default));
        Assert.All(smartDisabled.RootOptions!, static option => Assert.True(option.IsChecked));
        Assert.Equal(
            smartDisabled.RootOptions!.Count,
            smartDisabled.RootOptions!.Select(static option => option.Name).Distinct(PathComparer.Default).Count());
        AssertRootOptionsMatchProjectedTree(temp.Path, services, smartDisabled);
    }

    private static void AssertRootOptionsMatchProjectedTree(
        string rootPath,
        ProjectLoadWorkflowRefreshHarness.WorkflowServices services,
        SelectionRefreshSnapshot snapshot)
    {
        SelectionSnapshotContractAssertions.AssertAllSectionsConsistent(
            rootPath,
            services.IgnoreRulesService,
            snapshot);
    }

    private static void AssertEmptyFoldersState(
        SelectionRefreshSnapshot snapshot,
        bool expectedChecked,
        int expectedCount)
    {
        Assert.Equal(expectedCount, snapshot.IgnoreOptionCounts.EmptyFolders);
        var option = Assert.Single(
            snapshot.IgnoreOptions,
            option => option.Id == IgnoreOptionId.EmptyFolders);
        Assert.Equal(expectedChecked, option.IsChecked);
        Assert.EndsWith($"({expectedCount})", option.Label);
    }

    private static SelectionRefreshSnapshot RefreshWithAllIgnoreOptionsDisabled(
        string rootPath,
        ProjectLoadWorkflowRefreshHarness.WorkflowServices services,
        SelectionRefreshSnapshot snapshot)
    {
        var ignoreStates = snapshot.IgnoreOptionStateCache.ToDictionary(
            static pair => pair.Key,
            static _ => false);
        return services.Engine.ComputeFullRefreshSnapshot(
            ProjectLoadWorkflowRefreshHarness.CreateContextFromSnapshot(rootPath, snapshot) with
            {
                IgnoreSelectionInitialized = true,
                IgnoreSelectionCache = new HashSet<IgnoreOptionId>(),
                IgnoreOptionStateCache = ignoreStates,
                IgnoreOptionStateCacheIsComplete = true,
                IgnoreAllPreference = false,
                CaptureTreeInventory = true
            },
            TestContext.Current.CancellationToken);
    }

    private static SelectionRefreshSnapshot RefreshWithExtensionState(
        string rootPath,
        ProjectLoadWorkflowRefreshHarness.WorkflowServices services,
        SelectionRefreshSnapshot snapshot,
        string extension,
        bool isChecked)
    {
        var selectedExtensions = ProjectLoadWorkflowRefreshHarness.CollectCheckedExtensionNames(snapshot);
        if (isChecked)
            selectedExtensions.Add(extension);
        else
            selectedExtensions.Remove(extension);

        var extensionStates = ProjectLoadWorkflowRefreshHarness.BuildExtensionOptionStateCache(snapshot);
        extensionStates[extension] = isChecked;
        return services.Engine.ComputeFullRefreshSnapshot(
            ProjectLoadWorkflowRefreshHarness.CreateContextFromSnapshot(rootPath, snapshot) with
            {
                AllExtensionsChecked = snapshot.ExtensionOptions.All(option =>
                    string.Equals(option.Name, extension, StringComparison.OrdinalIgnoreCase)
                        ? isChecked
                        : option.IsChecked),
                ExtensionsSelectionInitialized = true,
                ExtensionsSelectionCache = selectedExtensions,
                ExtensionOptionStateCache = extensionStates,
                CaptureTreeInventory = true
            },
            TestContext.Current.CancellationToken);
    }

	private sealed class CountingWorkspaceScanner :
		IFileSystemScanner,
		IFileSystemScannerProjectWorkspaceScanner
	{
		private readonly FileSystemScanner _inner = new();

		public int WorkspaceScanCount { get; private set; }

		public bool CanReadRoot(string rootPath) => _inner.CanReadRoot(rootPath);

		public ScanResult<HashSet<string>> GetExtensions(
			string rootPath,
			IgnoreRules rules,
			CancellationToken cancellationToken = default) =>
			_inner.GetExtensions(rootPath, rules, cancellationToken);

		public ScanResult<HashSet<string>> GetRootFileExtensions(
			string rootPath,
			IgnoreRules rules,
			CancellationToken cancellationToken = default) =>
			_inner.GetRootFileExtensions(rootPath, rules, cancellationToken);

		public ScanResult<List<string>> GetRootFolderNames(
			string rootPath,
			IgnoreRules rules,
			CancellationToken cancellationToken = default) =>
			_inner.GetRootFolderNames(rootPath, rules, cancellationToken);

		public ScanResult<ProjectWorkspaceScanSnapshot> ScanProjectWorkspace(
			ProjectWorkspaceScanRequest request,
			CancellationToken cancellationToken = default)
		{
			WorkspaceScanCount++;
			return _inner.ScanProjectWorkspace(request, cancellationToken);
		}
	}
}
