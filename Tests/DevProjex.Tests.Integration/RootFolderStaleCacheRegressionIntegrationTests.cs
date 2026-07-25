using DevProjex.Application.Models;
using DevProjex.Tests.Shared.ProjectLoadWorkflow;

namespace DevProjex.Tests.Integration;

public sealed class RootFolderStaleCacheRegressionIntegrationTests
{
    public static TheoryData<IgnoreOptionId> FileVisibilityToggles => new()
    {
        IgnoreOptionId.EmptyFiles,
        IgnoreOptionId.DotFiles,
        IgnoreOptionId.HiddenFiles,
        IgnoreOptionId.ExtensionlessFiles
    };

    [Fact]
    public void DevProjexRegression_DisablingEmptyFiles_DoesNotRestoreIgnoredDotRootsFromSessionCache()
    {
        using var workspace = CreateWorkspace();
        var services = CreateServices();
        var baseline = ComputeBaseline(workspace.Path, services);

        Assert.Equal(["src"], baseline.RootOptions!.Select(static option => option.Name));

        var refreshed = ToggleFileVisibilityOption(
            workspace.Path,
            services,
            baseline,
            IgnoreOptionId.EmptyFiles,
            CreatePollutedRootStateCache());

        var rootOptions = Assert.IsAssignableFrom<IReadOnlyList<SelectionOption>>(refreshed.RootOptions);
        Assert.Equal(["src"], rootOptions.Select(static option => option.Name));
        Assert.DoesNotContain(rootOptions, static option => IsUnavailableRoot(option.Name));
        AssertPublishedRootsMatchFinalTree(workspace.Path, services, refreshed);
    }

    [Theory]
    [MemberData(nameof(FileVisibilityToggles))]
    public void FileVisibilityToggleMatrix_NeverPublishesControllerHiddenRootsFromPollutedCache(
        IgnoreOptionId changedOptionId)
    {
        using var workspace = CreateWorkspace();
        var services = CreateServices();
        var baseline = ComputeBaseline(workspace.Path, services);
        var pollutedRootStates = CreatePollutedRootStateCache();

        var disabled = ToggleFileVisibilityOption(
            workspace.Path,
            services,
            baseline,
            changedOptionId,
            pollutedRootStates);

        Assert.DoesNotContain(disabled.RootOptions!, static option => IsUnavailableRoot(option.Name));
        AssertPublishedRootsMatchFinalTree(workspace.Path, services, disabled);

        var restored = ToggleFileVisibilityOption(
            workspace.Path,
            services,
            disabled,
            changedOptionId,
            pollutedRootStates,
            isChecked: true);

        Assert.DoesNotContain(restored.RootOptions!, static option => IsUnavailableRoot(option.Name));
        AssertPublishedRootsMatchFinalTree(workspace.Path, services, restored);
    }

    private static TemporaryDirectory CreateWorkspace()
    {
        var workspace = new TemporaryDirectory();
        workspace.CreateFile(".gitignore", "ignored-root/\n");
        workspace.CreateFile("DevProjex.sln", string.Empty);
        workspace.CreateFile(Path.Combine("src", "App.cs"), "class App {}\n");
        workspace.CreateFile(Path.Combine("src", "App.csproj"), "<Project />\n");
        workspace.CreateFile(Path.Combine("src", "empty.cs"), string.Empty);
        workspace.CreateFile(Path.Combine("src", ".generated.cs"), "class Generated {}\n");
        workspace.CreateFile(Path.Combine("src", "LICENSE"), "license\n");
        workspace.CreateFile(Path.Combine(".git", "objects", "pack.dat"), "metadata\n");
        workspace.CreateFile(Path.Combine(".idea", "workspace.xml"), "<project />\n");
        workspace.CreateFile(Path.Combine(".tmp", "cache.bin"), "cache\n");
        workspace.CreateFile(Path.Combine("ignored-root", "ignored.cs"), "class Ignored {}\n");
        workspace.CreateFile(Path.Combine("obj", "Debug", "Generated.g.cs"), "class Generated {}\n");
        return workspace;
    }

    private static ProjectLoadWorkflowRefreshHarness.WorkflowServices CreateServices() =>
        ProjectLoadWorkflowRefreshHarness.CreateServices(
            transformRules: static rules => rules with { ExcludedRootFolderName = ".git" });

    private static SelectionRefreshSnapshot ComputeBaseline(
        string rootPath,
        ProjectLoadWorkflowRefreshHarness.WorkflowServices services) =>
        services.Engine.ComputeFullRefreshSnapshot(
            ProjectLoadWorkflowRefreshHarness.CreateDefaultContext(rootPath) with
            {
                CaptureTreeInventory = true
            },
            TestContext.Current.CancellationToken);

    private static SelectionRefreshSnapshot ToggleFileVisibilityOption(
        string rootPath,
        ProjectLoadWorkflowRefreshHarness.WorkflowServices services,
        SelectionRefreshSnapshot current,
        IgnoreOptionId changedOptionId,
        IReadOnlyDictionary<string, bool> rootStateCache,
        bool isChecked = false)
    {
        var selectedIgnoreOptions = ProjectLoadWorkflowRefreshHarness.CollectCheckedIgnoreOptionIds(current);
        if (isChecked)
            selectedIgnoreOptions.Add(changedOptionId);
        else
            selectedIgnoreOptions.Remove(changedOptionId);

        var ignoreStates = new Dictionary<IgnoreOptionId, bool>(current.IgnoreOptionStateCache)
        {
            [changedOptionId] = isChecked
        };
        var currentRootOptions = CreatePollutedCurrentRootOptions(current);
        var context = ProjectLoadWorkflowRefreshHarness.CreateContextFromSnapshot(rootPath, current) with
        {
            // The coordinator's master state becomes false when an early raw root scan
            // retained unchecked entries that are no longer visible in the settings UI.
            AllRootFoldersChecked = false,
            IgnoreSelectionInitialized = true,
            IgnoreSelectionCache = selectedIgnoreOptions,
            IgnoreOptionStateCache = ignoreStates,
            IgnoreOptionStateCacheIsComplete = true,
            IgnoreAllPreference = null,
            RootOptionStateCache = new Dictionary<string, bool>(rootStateCache, PathComparer.Default),
            CurrentRootOptions = currentRootOptions,
            CaptureTreeInventory = false
        };

        var snapshot = services.Engine.ComputeLiveRefreshSnapshot(
            context,
            ProjectLoadWorkflowRefreshHarness.CollectCheckedRootNames(current),
            TestContext.Current.CancellationToken);
        return snapshot.RootOptions is null
            ? snapshot with { RootOptions = currentRootOptions }
            : snapshot;
    }

    private static Dictionary<string, bool> CreatePollutedRootStateCache() =>
        new(PathComparer.Default)
        {
            [".git"] = false,
            [".idea"] = false,
            [".tmp"] = false,
            ["ignored-root"] = false,
            ["obj"] = false,
            ["src"] = true
        };

    private static IReadOnlyList<SelectionOption> CreatePollutedCurrentRootOptions(
        SelectionRefreshSnapshot snapshot) =>
        snapshot.RootOptions!
            .Concat(
            [
                new SelectionOption(".git", false),
                new SelectionOption(".idea", false),
                new SelectionOption(".tmp", false),
                new SelectionOption("ignored-root", false),
                new SelectionOption("obj", false)
            ])
            .OrderBy(static option => option.Name, PathComparer.Default)
            .ToArray();

    private static bool IsUnavailableRoot(string name) =>
        name is ".git" or ".idea" or ".tmp" or "ignored-root" or "obj";

    private static void AssertPublishedRootsMatchFinalTree(
        string rootPath,
        ProjectLoadWorkflowRefreshHarness.WorkflowServices services,
        SelectionRefreshSnapshot snapshot)
    {
        var selectedRoots = snapshot.RootOptions!
            .Where(static option => option.IsChecked)
            .Select(static option => option.Name)
            .ToHashSet(PathComparer.Default);
        var selectedExtensions = snapshot.EffectiveExtensionOptions
            .Where(static option => option.IsChecked)
            .Select(static option => option.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectedIgnoreOptions = ProjectLoadWorkflowRefreshHarness.CollectCheckedIgnoreOptionIds(snapshot);
        var rules = services.IgnoreRulesService.Build(rootPath, selectedIgnoreOptions, selectedRoots) with
        {
            ExcludedRootFolderName = ".git"
        };
        var tree = new TreeBuilder().Build(
            rootPath,
            new TreeFilterOptions(selectedExtensions, selectedRoots, rules),
            TestContext.Current.CancellationToken);
        var treeRoots = tree.Root.Children
            .Where(static node => node.IsDirectory)
            .Select(static node => node.Name)
            .ToHashSet(PathComparer.Default);

        Assert.True(
            selectedRoots.SetEquals(treeRoots),
            $"Selected roots=[{string.Join(", ", selectedRoots.Order(PathComparer.Default))}], " +
            $"tree roots=[{string.Join(", ", treeRoots.Order(PathComparer.Default))}].");
    }
}
