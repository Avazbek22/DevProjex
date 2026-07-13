using DevProjex.Application.Models;
using DevProjex.Tests.Shared.ProjectLoadWorkflow;

namespace DevProjex.Tests.Integration;

public sealed class SelectionRefreshRootTreeParityIntegrationTests
{
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

        var markdownDisabled = RefreshWithExtensionState(temp.Path, services, defaults, ".md", isChecked: false);

        Assert.Equal(["src"], markdownDisabled.RootOptions!.Select(static option => option.Name));
        AssertRootOptionsMatchProjectedTree(temp.Path, services, markdownDisabled);

        var markdownEnabled = RefreshWithExtensionState(
            temp.Path,
            services,
            markdownDisabled,
            ".md",
            isChecked: true);

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
}
