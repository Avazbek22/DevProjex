using DevProjex.Application.Models;
using DevProjex.Infrastructure.FileSystem;
using DevProjex.Tests.Shared.ProjectLoadWorkflow;

namespace DevProjex.Tests.Unit;

[Collection("AvaloniaUI")]
public sealed class SelectionSyncCoordinatorRootFolderStaleCacheRegressionTests
{
    [AvaloniaFact]
    public async Task PublicIgnoreToggle_PersistedHiddenRootStates_DoNotLeakIntoVisibleRootOptions()
    {
        using var workspace = CreateWorkspace();
        var services = ProjectLoadWorkflowRefreshHarness.CreateServices();
        var baseline = services.Engine.ComputeFullRefreshSnapshot(
            ProjectLoadWorkflowRefreshHarness.CreateDefaultContext(workspace.Path) with
            {
                CaptureTreeInventory = true
            },
            TestContext.Current.CancellationToken);
        var profile = CreatePollutedProfile(baseline);
        var viewModel = CreateViewModel();
        using var coordinator = CreateCoordinator(viewModel, workspace.Path);
        coordinator.ApplyProjectProfileSelections(workspace.Path, profile);

        await coordinator.RefreshRootAndDependentsAsync(
            workspace.Path,
            TestContext.Current.CancellationToken);
        coordinator.HookOptionListeners(viewModel.RootFolders);
        coordinator.HookOptionListeners(viewModel.Extensions);
        coordinator.HookIgnoreListeners(viewModel.IgnoreOptions);

        Assert.Equal(["src"], viewModel.RootFolders.Select(static option => option.Name));
        viewModel.RootFolders.Add(new SelectionOptionViewModel(".git", false));
        viewModel.RootFolders.Add(new SelectionOptionViewModel(".idea", false));
        viewModel.RootFolders.Add(new SelectionOptionViewModel(".tmp", false));
        var emptyFiles = Assert.Single(
            viewModel.IgnoreOptions,
            static option => option.Id == IgnoreOptionId.EmptyFiles);
        Assert.True(emptyFiles.IsChecked);

        emptyFiles.IsChecked = false;
        await coordinator.WaitForPendingRefreshesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["src"], viewModel.RootFolders.Select(static option => option.Name));
        Assert.DoesNotContain(viewModel.RootFolders, static option => option.Name is ".git" or ".idea" or ".tmp");
    }

    private static TemporaryDirectory CreateWorkspace()
    {
        var workspace = new TemporaryDirectory();
        workspace.CreateFile("DevProjex.sln", string.Empty);
        workspace.CreateFile(Path.Combine("src", "App.cs"), "class App {}\n");
        workspace.CreateFile(Path.Combine("src", "empty.cs"), string.Empty);
        workspace.CreateFile(Path.Combine(".git", "objects", "pack.dat"), "metadata\n");
        workspace.CreateFile(Path.Combine(".idea", "workspace.xml"), "<project />\n");
        workspace.CreateFile(Path.Combine(".tmp", "cache.bin"), "cache\n");
        return workspace;
    }

    private static ProjectSelectionProfile CreatePollutedProfile(SelectionRefreshSnapshot baseline)
    {
        var rootOptions = Assert.IsAssignableFrom<IReadOnlyList<SelectionOption>>(baseline.RootOptions);
        var rootStates = rootOptions.ToDictionary(
            static option => option.Name,
            static option => option.IsChecked,
            PathComparer.Default);
        rootStates[".git"] = false;
        rootStates[".idea"] = false;
        rootStates[".tmp"] = false;

        return new ProjectSelectionProfile(
            SelectedRootFolders: rootOptions
                .Where(static option => option.IsChecked)
                .Select(static option => option.Name)
                .ToArray(),
            SelectedExtensions: baseline.EffectiveExtensionOptions
                .Where(static option => option.IsChecked)
                .Select(static option => option.Name)
                .ToArray(),
            SelectedIgnoreOptions: baseline.IgnoreOptions
                .Where(static option => option.IsChecked)
                .Select(static option => option.Id)
                .ToArray(),
            RootFolderStates: rootStates,
            ExtensionStates: baseline.EffectiveExtensionOptions.ToDictionary(
                static option => option.Name,
                static option => option.IsChecked,
                StringComparer.OrdinalIgnoreCase),
            IgnoreOptionStates: new Dictionary<IgnoreOptionId, bool>(baseline.IgnoreOptionStateCache));
    }

    private static MainWindowViewModel CreateViewModel()
    {
        var localization = ProjectLoadWorkflowRuntime.CreateLocalizationService();
        return new MainWindowViewModel(localization, new HelpContentProvider());
    }

    private static SelectionSyncCoordinator CreateCoordinator(MainWindowViewModel viewModel, string rootPath)
    {
        var ignoreRulesService = ProjectLoadWorkflowRuntime.CreateIgnoreRulesService();
        return new SelectionSyncCoordinator(
            viewModel,
            new ScanOptionsUseCase(new FileSystemScanner()),
            new FilterOptionSelectionService(),
            ProjectLoadWorkflowRuntime.CreateIgnoreOptionsService(),
            (path, selectedIgnoreOptions, selectedRoots) =>
                ignoreRulesService.Build(path, selectedIgnoreOptions, selectedRoots),
            (path, selectedRoots) => ignoreRulesService.GetIgnoreOptionsAvailability(path, selectedRoots) with
            {
                ShowAdvancedCounts = true
            },
            _ => false,
            () => rootPath);
    }
}
