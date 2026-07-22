using DevProjex.Application.Models;
using DevProjex.Tests.Shared.ProjectLoadWorkflow;

namespace DevProjex.Tests.Integration;

public sealed class RootFolderFinalTreeParityMatrixIntegrationTests
{
    public static TheoryData<string[], string[]> ExtensionProjectionCases => new()
    {
        { [".py"], ["app", "mixed", "tests"] },
        { [".sh"], ["mixed", "scripts"] },
        { [".md"], ["app", "docs"] },
        { [".py", ".sh"], ["app", "mixed", "scripts", "tests"] },
        { [".json"], [] }
    };

    [Fact]
    public void LinkDownloaderBotForGroupsRegression_PythonOnly_RemovesCheckedScriptsFromOptionsAndTree()
    {
        using var workspace = CreateLinkDownloaderBotForGroupsWorkspace();
        var services = ProjectLoadWorkflowRefreshHarness.CreateServices();
        var baseline = ComputeBaseline(workspace.Path, services);

        AssertRootOptions(baseline, ("app", true), ("scripts", true), ("tests", true));

        var pythonOnly = ComputeLive(
            workspace.Path,
            baseline,
            services,
            [".py"],
            BuildRootStateCache(baseline));

        AssertRootOptions(pythonOnly, ("app", true), ("tests", true));
        AssertCheckedRootOptionsMatchFinalTree(workspace.Path, pythonOnly, services.IgnoreRulesService);
        Assert.Equal(2, pythonOnly.RootOptions!.Count);
        Assert.All(pythonOnly.RootOptions, static option => Assert.True(option.IsChecked));
        Assert.Contains(
            pythonOnly.EffectiveExtensionOptions,
            static option => option.Name == ".sh" && !option.IsChecked);
    }

    [Theory]
    [MemberData(nameof(ExtensionProjectionCases))]
    public void ExtensionSelectionMatrix_RootOptionsExactlyTrackEligibleFinalTree(
        string[] selectedExtensions,
        string[] expectedCheckedRoots)
    {
        using var workspace = CreateProjectionMatrixWorkspace();
        var services = ProjectLoadWorkflowRefreshHarness.CreateServices();
        var baseline = ComputeBaseline(workspace.Path, services);
        var rootStateCache = BuildRootStateCache(baseline);

        var snapshot = ComputeLive(
            workspace.Path,
            baseline,
            services,
            selectedExtensions,
            rootStateCache);

        Assert.Equal(expectedCheckedRoots, CollectCheckedRootNames(snapshot).Order(PathComparer.Default));
        Assert.Equal(expectedCheckedRoots.Length, snapshot.RootOptions!.Count);
        Assert.All(snapshot.RootOptions, static option => Assert.True(option.IsChecked));
        AssertCheckedRootOptionsMatchFinalTree(workspace.Path, snapshot, services.IgnoreRulesService);

        var repeated = ComputeLive(
            workspace.Path,
            snapshot,
            services,
            selectedExtensions,
            rootStateCache,
            baseline);
        AssertRootOptionsEqual(snapshot, repeated);
        AssertCheckedRootOptionsMatchFinalTree(workspace.Path, repeated, services.IgnoreRulesService);
    }

    [Fact]
    public void ExtensionJourney_SwitchingAwayAndBack_RestoresRootsAndNeverLeaksStaleSelection()
    {
        using var workspace = CreateProjectionMatrixWorkspace();
        var services = ProjectLoadWorkflowRefreshHarness.CreateServices();
        var baseline = ComputeBaseline(workspace.Path, services);
        var rootStateCache = BuildRootStateCache(baseline);

        var pythonOnly = ComputeLive(workspace.Path, baseline, services, [".py"], rootStateCache);
        Assert.Equal(["app", "mixed", "tests"], CollectCheckedRootNames(pythonOnly).Order(PathComparer.Default));

        var shellOnly = ComputeLive(workspace.Path, pythonOnly, services, [".sh"], rootStateCache, baseline);
        Assert.Equal(["mixed", "scripts"], CollectCheckedRootNames(shellOnly).Order(PathComparer.Default));
        AssertCheckedRootOptionsMatchFinalTree(workspace.Path, shellOnly, services.IgnoreRulesService);

        var restored = ComputeLive(
            workspace.Path,
            shellOnly,
            services,
            baseline.EffectiveExtensionOptions.Select(static option => option.Name).ToArray(),
            rootStateCache,
            baseline);
        AssertRootOptionsEqual(baseline, restored);
        AssertCheckedRootOptionsMatchFinalTree(workspace.Path, restored, services.IgnoreRulesService);

        var repeated = ComputeLive(
            workspace.Path,
            restored,
            services,
            baseline.EffectiveExtensionOptions.Select(static option => option.Name).ToArray(),
            rootStateCache,
            baseline);
        AssertRootOptionsEqual(restored, repeated);
    }

    [Fact]
    public void PartialRootSelection_UncheckedCandidateRemainsAvailableButCheckedRootsEqualTree()
    {
        using var workspace = CreateLinkDownloaderBotForGroupsWorkspace();
        var services = ProjectLoadWorkflowRefreshHarness.CreateServices();
        var baseline = ComputeBaseline(workspace.Path, services);
        var partialOptions = baseline.RootOptions!
            .Select(static option => option.Name == "scripts" ? option with { IsChecked = false } : option)
            .ToArray();
        var rootStateCache = partialOptions.ToDictionary(
            static option => option.Name,
            static option => option.IsChecked,
            PathComparer.Default);
        var partialBaseline = baseline with { RootOptions = partialOptions };

        var pythonOnly = ComputeLive(workspace.Path, partialBaseline, services, [".py"], rootStateCache);

        AssertRootOptions(pythonOnly, ("app", true), ("scripts", false), ("tests", true));
        Assert.False(pythonOnly.RootOptions!.All(static option => option.IsChecked));
        AssertCheckedRootOptionsMatchFinalTree(workspace.Path, pythonOnly, services.IgnoreRulesService);
    }

    [Fact]
    public void RootFilesOnlyAndEmptyResult_ExposeNoPhantomRootFolders()
    {
        using var workspace = new TemporaryDirectory();
        workspace.CreateFile("main.py", "print('root only')");
        workspace.CreateFile("README.md", "root documentation");
        workspace.CreateFile("scripts/install.sh", "echo setup");
        var services = ProjectLoadWorkflowRefreshHarness.CreateServices();
        var baseline = ComputeBaseline(workspace.Path, services);
        var rootStateCache = BuildRootStateCache(baseline);

        var pythonOnly = ComputeLive(workspace.Path, baseline, services, [".py"], rootStateCache);
        Assert.Empty(pythonOnly.RootOptions!);
        AssertCheckedRootOptionsMatchFinalTree(workspace.Path, pythonOnly, services.IgnoreRulesService);
        Assert.Contains(
            BuildFinalTree(workspace.Path, pythonOnly, services.IgnoreRulesService).Root.Children,
            static node => !node.IsDirectory && node.Name == "main.py");

        var noMatches = ComputeLive(workspace.Path, pythonOnly, services, [".json"], rootStateCache, baseline);
        Assert.Empty(noMatches.RootOptions!);
        Assert.Empty(BuildFinalTree(workspace.Path, noMatches, services.IgnoreRulesService).Root.Children);
    }

    [Fact]
    public void GitIgnoreAndSmartIgnoreControllers_OnlyPublishRootsOwnedByActiveRules()
    {
        using var workspace = CreateProjectionMatrixWorkspace();
        var services = ProjectLoadWorkflowRefreshHarness.CreateServices();
        var baseline = ComputeBaseline(workspace.Path, services);

        Assert.DoesNotContain(baseline.RootOptions!, static option => option.Name == "generated");

        var gitIgnoreOff = ComputeFullWithIgnoreOverride(
            workspace.Path,
            baseline,
            services,
            IgnoreOptionId.UseGitIgnore,
            isChecked: false);
        Assert.Contains(gitIgnoreOff.RootOptions!, static option => option.Name == "generated" && option.IsChecked);
        AssertCheckedRootOptionsMatchFinalTree(workspace.Path, gitIgnoreOff, services.IgnoreRulesService);

        using var smartWorkspace = CreateSmartIgnoreWorkspace();
        var smartBaseline = ComputeBaseline(smartWorkspace.Path, services);
        Assert.DoesNotContain(smartBaseline.RootOptions!, static option => option.Name == "obj");

        var smartIgnoreOff = ComputeFullWithIgnoreOverride(
            smartWorkspace.Path,
            smartBaseline,
            services,
            IgnoreOptionId.SmartIgnore,
            isChecked: false);
        Assert.False(Assert.Single(
            smartIgnoreOff.IgnoreOptions,
            static option => option.Id == IgnoreOptionId.SmartIgnore).IsChecked);
        Assert.Contains(smartIgnoreOff.RootOptions!, static option => option.Name == "obj" && option.IsChecked);
        AssertCheckedRootOptionsMatchFinalTree(smartWorkspace.Path, smartIgnoreOff, services.IgnoreRulesService);
    }

    [Fact]
    public void ProfileRestoreAndProjectSwitch_DoNotReuseRootNamesOrCheckboxesAcrossProjects()
    {
        using var first = CreateLinkDownloaderBotForGroupsWorkspace();
        using var second = new TemporaryDirectory();
        second.CreateFile("alpha/main.py", "print('alpha')");
        second.CreateFile("beta/readme.md", "beta");
        var services = ProjectLoadWorkflowRefreshHarness.CreateServices();
        var firstBaseline = ComputeBaseline(first.Path, services);
        var profileRootStates = new Dictionary<string, bool>(PathComparer.Default)
        {
            ["app"] = true,
            ["scripts"] = false,
            ["tests"] = true
        };
        var profileContext = ProjectLoadWorkflowRefreshHarness.CreateContextFromSnapshot(first.Path, firstBaseline) with
        {
            PreparedSelectionMode = PreparedSelectionMode.Profile,
            AllRootFoldersChecked = false,
            RootSelectionInitialized = true,
            RootSelectionCache = new HashSet<string>(["app", "tests"], PathComparer.Default),
            RootOptionStateCache = profileRootStates,
            AllExtensionsChecked = false,
            ExtensionsSelectionInitialized = true,
            ExtensionsSelectionCache = new HashSet<string>([".py"], StringComparer.OrdinalIgnoreCase),
            ExtensionOptionStateCache = firstBaseline.ExtensionOptions.ToDictionary(
                static option => option.Name,
                static option => option.Name == ".py",
                StringComparer.OrdinalIgnoreCase),
            CaptureTreeInventory = true,
            CurrentRootOptions = firstBaseline.RootOptions
        };

        var restoredProfile = services.Engine.ComputeFullRefreshSnapshot(
            profileContext,
            TestContext.Current.CancellationToken);
        AssertRootOptions(restoredProfile, ("app", true), ("scripts", false), ("tests", true));
        AssertCheckedRootOptionsMatchFinalTree(first.Path, restoredProfile, services.IgnoreRulesService);

        var secondSnapshot = ComputeBaseline(second.Path, services);
        AssertRootOptions(secondSnapshot, ("alpha", true), ("beta", true));
        Assert.DoesNotContain(secondSnapshot.RootOptions!, static option => option.Name is "app" or "scripts" or "tests");

        var restoredAgain = services.Engine.ComputeFullRefreshSnapshot(
            profileContext,
            TestContext.Current.CancellationToken);
        AssertRootOptionsEqual(restoredProfile, restoredAgain);
    }

    private static SelectionRefreshSnapshot ComputeBaseline(
        string rootPath,
        ProjectLoadWorkflowRefreshHarness.WorkflowServices services) =>
        services.Engine.ComputeFullRefreshSnapshot(
            ProjectLoadWorkflowRefreshHarness.CreateDefaultContext(rootPath) with
            {
                CaptureTreeInventory = true
            },
            TestContext.Current.CancellationToken);

    private static SelectionRefreshSnapshot ComputeLive(
        string rootPath,
        SelectionRefreshSnapshot current,
        ProjectLoadWorkflowRefreshHarness.WorkflowServices services,
        IReadOnlyCollection<string> selectedExtensions,
        IReadOnlyDictionary<string, bool> rootStateCache,
        SelectionRefreshSnapshot? retainedOptionState = null)
    {
        var extensionSet = new HashSet<string>(selectedExtensions, StringComparer.OrdinalIgnoreCase);
        var extensionStateSource = retainedOptionState ?? current;
        var context = ProjectLoadWorkflowRefreshHarness.CreateContextFromSnapshot(rootPath, current) with
        {
            AllExtensionsChecked = false,
            ExtensionsSelectionInitialized = true,
            ExtensionsSelectionCache = extensionSet,
            ExtensionOptionStateCache = extensionStateSource.ExtensionOptions.ToDictionary(
                static option => option.Name,
                option => extensionSet.Contains(option.Name),
                StringComparer.OrdinalIgnoreCase),
            RootOptionStateCache = new Dictionary<string, bool>(rootStateCache, PathComparer.Default),
            CurrentRootOptions = current.RootOptions,
            CaptureTreeInventory = false
        };

        var snapshot = services.Engine.ComputeLiveRefreshSnapshot(
            context,
            CollectCheckedRootNames(current),
            TestContext.Current.CancellationToken);
        return snapshot.RootOptions is null
            ? snapshot with { RootOptions = current.RootOptions }
            : snapshot;
    }

    private static SelectionRefreshSnapshot ComputeFullWithIgnoreOverride(
        string rootPath,
        SelectionRefreshSnapshot baseline,
        ProjectLoadWorkflowRefreshHarness.WorkflowServices services,
        IgnoreOptionId optionId,
        bool isChecked)
    {
        var selectedIgnore = baseline.IgnoreOptions
            .Where(static option => option.IsChecked)
            .Select(static option => option.Id)
            .ToHashSet();
        if (isChecked)
            selectedIgnore.Add(optionId);
        else
            selectedIgnore.Remove(optionId);
        var ignoreStates = new Dictionary<IgnoreOptionId, bool>(baseline.IgnoreOptionStateCache)
        {
            [optionId] = isChecked
        };
        var context = ProjectLoadWorkflowRefreshHarness.CreateContextFromSnapshot(rootPath, baseline) with
        {
            AllExtensionsChecked = true,
            ExtensionsSelectionInitialized = false,
            ExtensionsSelectionCache = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            ExtensionOptionStateCache = null,
            IgnoreSelectionInitialized = true,
            IgnoreSelectionCache = selectedIgnore,
            IgnoreOptionStateCache = ignoreStates,
            IgnoreAllPreference = null,
            IgnoreOptionStateCacheIsComplete = true,
            CaptureTreeInventory = true,
            CurrentRootOptions = baseline.RootOptions
        };

        return services.Engine.ComputeFullRefreshSnapshot(context, TestContext.Current.CancellationToken);
    }

    private static TreeBuildResult BuildFinalTree(
        string rootPath,
        SelectionRefreshSnapshot snapshot,
        IgnoreRulesService ignoreRulesService)
    {
        var selectedRoots = CollectCheckedRootNames(snapshot);
        var selectedExtensions = snapshot.EffectiveExtensionOptions
            .Where(static option => option.IsChecked)
            .Select(static option => option.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectedIgnore = snapshot.IgnoreOptions
            .Where(static option => option.IsChecked)
            .Select(static option => option.Id)
            .ToHashSet();
        var rules = ignoreRulesService.Build(rootPath, selectedIgnore, selectedRoots);
        return new TreeBuilder().Build(
            rootPath,
            new TreeFilterOptions(selectedExtensions, selectedRoots, rules),
            TestContext.Current.CancellationToken);
    }

    private static void AssertCheckedRootOptionsMatchFinalTree(
        string rootPath,
        SelectionRefreshSnapshot snapshot,
        IgnoreRulesService ignoreRulesService)
    {
        var expected = CollectCheckedRootNames(snapshot);
        var actual = BuildFinalTree(rootPath, snapshot, ignoreRulesService).Root.Children
            .Where(static node => node.IsDirectory)
            .Select(static node => node.Name)
            .ToHashSet(PathComparer.Default);

        Assert.True(
            expected.SetEquals(actual),
            $"Checked roots=[{string.Join(", ", expected.Order(PathComparer.Default))}], " +
            $"tree roots=[{string.Join(", ", actual.Order(PathComparer.Default))}].");
    }

    private static void AssertRootOptions(
        SelectionRefreshSnapshot snapshot,
        params (string Name, bool IsChecked)[] expected)
    {
        Assert.Equal(
            expected,
            snapshot.RootOptions!.Select(static option => (option.Name, option.IsChecked)));
    }

    private static void AssertRootOptionsEqual(
        SelectionRefreshSnapshot expected,
        SelectionRefreshSnapshot actual) =>
        Assert.Equal(
            expected.RootOptions!.Select(static option => (option.Name, option.IsChecked)),
            actual.RootOptions!.Select(static option => (option.Name, option.IsChecked)));

    private static HashSet<string> CollectCheckedRootNames(SelectionRefreshSnapshot snapshot) =>
        snapshot.RootOptions!
            .Where(static option => option.IsChecked)
            .Select(static option => option.Name)
            .ToHashSet(PathComparer.Default);

    private static Dictionary<string, bool> BuildRootStateCache(SelectionRefreshSnapshot snapshot) =>
        snapshot.RootOptions!.ToDictionary(
            static option => option.Name,
            static option => option.IsChecked,
            PathComparer.Default);

    private static TemporaryDirectory CreateLinkDownloaderBotForGroupsWorkspace()
    {
        var workspace = new TemporaryDirectory();
        workspace.CreateFile(".gitignore", "__pycache__/\n.pytest_cache/\nlogs/\ndata/\n");
        workspace.CreateFile("pyproject.toml", "[project]\nname = 'link-downloader'\n");
        workspace.CreateFile("main.py", "from app.bot import run\n");
        workspace.CreateFile("README.md", "Link downloader\n");
        workspace.CreateFile("app/__init__.py", string.Empty);
        workspace.CreateFile("app/bot.py", "def run(): pass\n");
        workspace.CreateFile("app/deep/downloader.py", "def download(): pass\n");
        workspace.CreateFile("scripts/install.sh", "#!/bin/sh\necho install\n");
        workspace.CreateFile("scripts/systemd/link-downloader.service", "[Service]\n");
        workspace.CreateFile("tests/test_bot.py", "def test_bot(): pass\n");
        workspace.CreateFile("logs/runtime.log", "ignored\n");
        workspace.CreateFile("data/cache.json", "{}\n");
        return workspace;
    }

    private static TemporaryDirectory CreateProjectionMatrixWorkspace()
    {
        var workspace = new TemporaryDirectory();
        workspace.CreateFile(".gitignore", "generated/\napp/ignored/\n");
        workspace.CreateFile("pyproject.toml", "[project]\nname = 'matrix'\n");
        workspace.CreateFile("App.csproj", "<Project />\n");
        workspace.CreateFile("App.csproj.user", "local state\n");
        workspace.CreateFile("main.py", "print('root')\n");
        workspace.CreateFile("README.md", "root docs\n");
        workspace.CreateFile("app/main.py", "print('app')\n");
        workspace.CreateFile("app/deep/helper.py", "pass\n");
        workspace.CreateFile("app/readme.md", "app docs\n");
        workspace.CreateFile("app/ignored/drop.py", "ignored\n");
        workspace.CreateFile("tests/deep/test_main.py", "def test_main(): pass\n");
        workspace.CreateFile("scripts/install.sh", "echo install\n");
        workspace.CreateFile("scripts/deep/tool.ps1", "Write-Output tool\n");
        workspace.CreateFile("docs/nested/guide.md", "guide\n");
        workspace.CreateFile("mixed/source.py", "pass\n");
        workspace.CreateFile("mixed/run.sh", "echo run\n");
        workspace.CreateFile("generated/generated.py", "ignored\n");
        workspace.CreateFile("obj/project.assets.json", "{}\n");
        workspace.CreateDirectory("empty/deep");
        return workspace;
    }

    private static TemporaryDirectory CreateSmartIgnoreWorkspace()
    {
        var workspace = new TemporaryDirectory();
        workspace.CreateFile("App.csproj", "<Project />\n");
        workspace.CreateFile("src/main.cs", "internal static class Program { }\n");
        workspace.CreateFile("obj/project.assets.json", "{}\n");
        return workspace;
    }
}
