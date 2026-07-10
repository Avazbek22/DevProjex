using DevProjex.Application.UseCases;
using DevProjex.Infrastructure.FileSystem;
using DevProjex.Kernel.Abstractions;

namespace DevProjex.Tests.UI;

[Collection(UiWorkspaceCollection.Name)]
public sealed class MainWindowIgnoreOptionsUiTests
{
    [AvaloniaFact]
    public async Task NewWorkspace_WithDynamicIgnoreEntries_KeepsDynamicOptionsCheckedByDefault()
    {
        using var project = UiTestProject.CreateWithDynamicIgnoreEntries();
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

        try
        {
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.ExtensionlessFiles,
                visible: true,
                isChecked: true);

            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.EmptyFolders,
                visible: true,
                isChecked: true);

            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.EmptyFiles,
                visible: true,
                isChecked: true);

            Assert.True(UiTestDriver.GetViewModel(window).AllIgnoreChecked);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task RootSelectionRefresh_PreservesUncheckedExtensionlessStateWhenOptionReappears()
    {
        await AssertDynamicIgnoreOptionStateIsPreservedWhenRootSelectionRestoresIt(IgnoreOptionId.ExtensionlessFiles);
    }

    [AvaloniaFact]
    public async Task RootSelectionRefresh_PreservesUncheckedEmptyFoldersStateWhenOptionReappears()
    {
        await AssertDynamicIgnoreOptionStateIsPreservedWhenRootSelectionRestoresIt(IgnoreOptionId.EmptyFolders);
    }

    [AvaloniaFact]
    public async Task RootSelectionRefresh_PreservesUncheckedEmptyFilesStateWhenOptionReappears()
    {
        await AssertDynamicIgnoreOptionStateIsPreservedWhenRootSelectionRestoresIt(IgnoreOptionId.EmptyFiles);
    }

    [AvaloniaFact]
    public async Task RootSelectionRefresh_HidesAllDynamicOptions_WhenSelectedRootDoesNotContainThem()
    {
        using var project = UiTestProject.CreateWithDynamicIgnoreEntries();
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

        try
        {
            var srcRootCheckBox = UiTestDriver.GetRequiredRootFolderCheckBox(window, "src");
            await UiTestDriver.ClickAsync(window, srcRootCheckBox);

            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.ExtensionlessFiles,
                visible: false);

            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.EmptyFolders,
                visible: false);

            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.EmptyFiles,
                visible: false);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task ExtensionSelectionRefresh_ShowsAndHidesEmptyFoldersCounterBasedOnEffectiveTreeDelta()
    {
        using var project = UiTestProject.CreateWithExtensionSensitiveEmptyFolders();
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

        try
        {
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.EmptyFolders,
                visible: false);

            var markdownOption = UiTestDriver.GetViewModel(window).Extensions.Single(option => option.Name == ".md");
            markdownOption.IsChecked = false;
            await UiTestDriver.WaitForSettledFramesAsync(frameCount: 8);

            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.EmptyFolders,
                visible: true,
                isChecked: true);
            await UiTestDriver.WaitForIgnoreOptionLabelAsync(
                window,
                IgnoreOptionId.EmptyFolders,
                "Empty folders (2)");

            markdownOption = UiTestDriver.GetViewModel(window).Extensions.Single(option => option.Name == ".md");
            markdownOption.IsChecked = true;
            await UiTestDriver.WaitForSettledFramesAsync(frameCount: 8);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.EmptyFolders,
                visible: false);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task ExtensionsAllToggleRefresh_RecomputesEmptyFoldersCounterForBulkSelectionChanges()
    {
        using var project = UiTestProject.CreateWithExtensionSensitiveEmptyFolders();
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

        try
        {
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.EmptyFolders,
                visible: false);

            var allExtensionsCheckBox = UiTestDriver.GetRequiredControl<CheckBox>(window, "ExtensionsAllCheckBox");
            await UiTestDriver.ClickAsync(window, allExtensionsCheckBox);

            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.EmptyFolders,
                visible: true,
                isChecked: true);
            await UiTestDriver.WaitForIgnoreOptionLabelAsync(
                window,
                IgnoreOptionId.EmptyFolders,
                "Empty folders (4)");

            await UiTestDriver.ClickAsync(window, allExtensionsCheckBox);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.EmptyFolders,
                visible: false);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task GitIgnoredExtensionlessNoise_DoesNotInflateExtensionlessCounter()
    {
        using var project = UiTestProject.CreateWithGitIgnoredExtensionlessNoise();
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

        try
        {
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.UseGitIgnore,
                visible: true,
                isChecked: true);

            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.ExtensionlessFiles,
                visible: true,
                isChecked: true);
            await UiTestDriver.WaitForIgnoreOptionLabelAsync(
                window,
                IgnoreOptionId.ExtensionlessFiles,
                "Files without extension (1)");
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task CleanGitAndSmartControllers_RemainHiddenUntilTheyAffectVisibleContent()
    {
        using var project = UiTestProject.CreateWithCleanGitAndSmartWorkspace();
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

        try
        {
            await UiTestDriver.WaitForIgnoreOptionStateAsync(window, IgnoreOptionId.UseGitIgnore, visible: false);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(window, IgnoreOptionId.SmartIgnore, visible: false);

            var artifactPath = Path.Combine(project.RootPath, "bin", "Debug", "net10.0", "App.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(artifactPath)!);
            await File.WriteAllTextAsync(artifactPath, "binary");

            await UiTestDriver.RefreshProjectAsync(window);

            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.UseGitIgnore,
                visible: true,
                isChecked: true);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(window, IgnoreOptionId.SmartIgnore, visible: false);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task ExplicitUncheckedGitIgnoreController_RemainsVisibleWhenDotFilesMaskItsOnlyImpact()
    {
        using var project = UiTestProject.CreateWithGitIgnoreDotFileOnlyWorkspace();
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

        try
        {
            await UiTestDriver.WaitForIgnoreOptionStateAsync(window, IgnoreOptionId.DotFiles, visible: true, isChecked: true);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(window, IgnoreOptionId.UseGitIgnore, visible: false);

            await UiTestDriver.ClickAsync(window, UiTestDriver.GetRequiredControl<CheckBox>(window, "IgnoreAllCheckBox"));
            await UiTestDriver.WaitForIgnoreOptionStateAsync(window, IgnoreOptionId.UseGitIgnore, visible: true, isChecked: false);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(window, IgnoreOptionId.DotFiles, visible: true, isChecked: false);

            await SetIgnoreOptionCheckedAsync(window, IgnoreOptionId.DotFiles, isChecked: true);

            await UiTestDriver.WaitForIgnoreOptionStateAsync(window, IgnoreOptionId.DotFiles, visible: true, isChecked: true);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(window, IgnoreOptionId.UseGitIgnore, visible: true, isChecked: false);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task CleanPythonSmartController_RemainsHiddenUntilSmartArtifactAppears()
    {
        using var project = UiTestProject.CreateWithCleanPythonSmartWorkspace();
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

        try
        {
            await UiTestDriver.WaitForIgnoreOptionStateAsync(window, IgnoreOptionId.SmartIgnore, visible: false);

            var artifactPath = Path.Combine(project.RootPath, "src", "__pycache__", "app.cpython-310.pyc");
            Directory.CreateDirectory(Path.GetDirectoryName(artifactPath)!);
            await File.WriteAllTextAsync(artifactPath, "binary");

            await UiTestDriver.RefreshProjectAsync(window);

            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.SmartIgnore,
                visible: true,
                isChecked: true);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task DotFolderExtensionlessNoise_RecomputesExtensionlessCounterAfterDynamicIgnoreOptionsAppear()
    {
        using var project = UiTestProject.CreateWithDotFolderExtensionlessNoise();
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

        try
        {
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.ExtensionlessFiles,
                visible: true,
                isChecked: true);
            await UiTestDriver.WaitForIgnoreOptionLabelAsync(
                window,
                IgnoreOptionId.ExtensionlessFiles,
                "Files without extension (1)");

            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.DotFolders,
                visible: true,
                isChecked: true);

            var viewModel = UiTestDriver.GetViewModel(window);
            Assert.DoesNotContain(
                viewModel.RootFolders,
                option => string.Equals(option.Name, ".cache", StringComparison.Ordinal));
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task HiddenDotFolderOverlap_ShowsHiddenFoldersOnlyWhenDotFoldersNoLongerHidesSameFolder()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var project = UiTestProject.CreateWithHiddenDotFolderOverlapWorkspace();
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

        try
        {
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.DotFolders,
                visible: true,
                isChecked: true);
            await UiTestDriver.WaitForIgnoreOptionLabelAsync(
                window,
                IgnoreOptionId.DotFolders,
                "dot folders (2)");
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.HiddenFolders,
                visible: false);

            await SetIgnoreOptionCheckedAsync(window, IgnoreOptionId.DotFolders, isChecked: false);
            await ApplySettingsAndWaitForIgnoreRefreshAsync(window);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.HiddenFolders,
                visible: true,
                isChecked: true);
            await UiTestDriver.WaitForIgnoreOptionLabelAsync(
                window,
                IgnoreOptionId.HiddenFolders,
                "Hidden folders (1)");
            await WaitForProjectTreePathStateAsync(window, exists: false, ".git", "config.txt");

            await SetIgnoreOptionCheckedAsync(window, IgnoreOptionId.HiddenFolders, isChecked: false);
            await ApplySettingsAndWaitForIgnoreRefreshAsync(window);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.HiddenFolders,
                visible: true,
                isChecked: false);
            await WaitForProjectTreePathStateAsync(window, exists: true, ".git", "config.txt");

            await SetIgnoreOptionCheckedAsync(window, IgnoreOptionId.DotFolders, isChecked: true);
            await ApplySettingsAndWaitForIgnoreRefreshAsync(window);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.HiddenFolders,
                visible: false);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.DotFolders,
                visible: true,
                isChecked: true);
            await WaitForProjectTreePathStateAsync(window, exists: false, ".git", "config.txt");

            await SetIgnoreOptionCheckedAsync(window, IgnoreOptionId.DotFolders, isChecked: false);
            await ApplySettingsAndWaitForIgnoreRefreshAsync(window);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.HiddenFolders,
                visible: true,
                isChecked: false);
            await WaitForProjectTreePathStateAsync(window, exists: true, ".git", "config.txt");
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task RefreshProject_AfterExternalMutation_ChecksNewEntriesAcrossSectionsAndPreservesUncheckedState()
    {
        using var project = UiTestProject.CreateWithExternalRefreshMutationWorkspace();
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

        try
        {
            await WaitForRootFolderStateAsync(window, "docs", visible: true, isChecked: true);
            await WaitForExtensionStateAsync(window, ".csv", visible: true, isChecked: true);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.EmptyFiles,
                visible: true,
                isChecked: true);

            await UiTestDriver.ClickRootFolderCheckBoxAsync(window, "docs");
            await UiTestDriver.ClickExtensionCheckBoxAsync(window, ".csv");
            await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(window, IgnoreOptionId.EmptyFiles);
            await UiTestDriver.WaitForSelectionRefreshIdleAsync(window);
            await WaitForRootFolderStateAsync(window, "docs", visible: true, isChecked: false);
            await WaitForExtensionStateAsync(window, ".csv", visible: true, isChecked: false);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.EmptyFiles,
                visible: true,
                isChecked: false);
            await ApplySettingsAndWaitForIgnoreRefreshAsync(window);

            MutateExternalRefreshWorkspace(project.RootPath);
            await UiTestDriver.RefreshProjectAsync(window);

            await WaitForRootFolderStateAsync(window, "docs", visible: true, isChecked: false);
            await WaitForRootFolderStateAsync(window, "api", visible: true, isChecked: true);
            await WaitForRootFolderStateAsync(window, "web", visible: true, isChecked: true);
            await WaitForRootFolderStateAsync(window, "generated", visible: true, isChecked: true);

            await WaitForExtensionStateAsync(window, ".csv", visible: true, isChecked: false);
            await WaitForExtensionStateAsync(window, ".cs", visible: true, isChecked: true);
            await WaitForExtensionStateAsync(window, ".ts", visible: true, isChecked: true);
            await WaitForExtensionStateAsync(window, ".log", visible: true, isChecked: true);

            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.EmptyFiles,
                visible: true,
                isChecked: false);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.UseGitIgnore,
                visible: true,
                isChecked: true);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.SmartIgnore,
                visible: true,
                isChecked: true);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.DotFolders,
                visible: true,
                isChecked: true);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.DotFiles,
                visible: true,
                isChecked: true);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.EmptyFolders,
                visible: true,
                isChecked: true);

            await WaitForProjectTreePathStateAsync(window, exists: true, "src", "App.cs");
            await WaitForProjectTreePathStateAsync(window, exists: true, "api", "src", "Program.cs");
            await WaitForProjectTreePathStateAsync(window, exists: true, "web", "src", "app.ts");
            await WaitForProjectTreePathStateAsync(window, exists: true, "generated", "report.log");
            await WaitForProjectTreePathStateAsync(window, exists: false, "docs", "notes.md");
            await WaitForProjectTreePathStateAsync(window, exists: false, "data.csv");
            await WaitForProjectTreePathStateAsync(window, exists: false, "api", "logs", "runtime.log");
            await WaitForProjectTreePathStateAsync(window, exists: false, "web", "node_modules", "pkg", "index.js");
            await WaitForProjectTreePathStateAsync(window, exists: false, ".idea", "workspace.xml");
            await WaitForProjectTreePathStateAsync(window, exists: false, ".env");
            await WaitForProjectTreePathStateAsync(window, exists: false, "empty-root");
            await AssertIgnoreOptionsStayStableAsync(window);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task ReopenProject_WithPersistedFullState_PreservesUncheckedItemsAndChecksNewEntries()
    {
        using var project = UiTestProject.CreateWithExternalRefreshMutationWorkspace();
        var appDataPath = Path.Combine(project.AppDataPath, "persisted-full-state");
        MainWindow? firstWindow = null;
        MainWindow? secondWindow = null;

        try
        {
            firstWindow = await UiTestDriver.CreateLoadedMainWindowAsync(project, appDataPathOverride: appDataPath);

            await WaitForRootFolderStateAsync(firstWindow, "docs", visible: true, isChecked: true);
            await WaitForExtensionStateAsync(firstWindow, ".csv", visible: true, isChecked: true);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                firstWindow,
                IgnoreOptionId.EmptyFiles,
                visible: true,
                isChecked: true);

            await UiTestDriver.ClickRootFolderCheckBoxAsync(firstWindow, "docs");
            await UiTestDriver.ClickExtensionCheckBoxAsync(firstWindow, ".csv");
            await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(firstWindow, IgnoreOptionId.EmptyFiles);
            await ApplySettingsAndWaitForIgnoreRefreshAsync(firstWindow);

            await WaitForRootFolderStateAsync(firstWindow, "docs", visible: true, isChecked: false);
            await WaitForExtensionStateAsync(firstWindow, ".csv", visible: true, isChecked: false);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                firstWindow,
                IgnoreOptionId.EmptyFiles,
                visible: true,
                isChecked: false);

            await UiTestDriver.CloseWindowAsync(firstWindow, cleanupAppData: false);
            firstWindow = null;

            MutateExternalRefreshWorkspace(project.RootPath);
            secondWindow = await UiTestDriver.CreateLoadedMainWindowAsync(project, appDataPathOverride: appDataPath);

            // Persisted full-state must win for known entries, while entries first seen
            // after reopen keep the product default: checked and immediately useful.
            await WaitForRootFolderStateAsync(secondWindow, "docs", visible: true, isChecked: false);
            await WaitForRootFolderStateAsync(secondWindow, "api", visible: true, isChecked: true);
            await WaitForRootFolderStateAsync(secondWindow, "web", visible: true, isChecked: true);
            await WaitForRootFolderStateAsync(secondWindow, "generated", visible: true, isChecked: true);

            await WaitForExtensionStateAsync(secondWindow, ".csv", visible: true, isChecked: false);
            await WaitForExtensionStateAsync(secondWindow, ".cs", visible: true, isChecked: true);
            await WaitForExtensionStateAsync(secondWindow, ".ts", visible: true, isChecked: true);
            await WaitForExtensionStateAsync(secondWindow, ".log", visible: true, isChecked: true);

            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                secondWindow,
                IgnoreOptionId.EmptyFiles,
                visible: true,
                isChecked: false);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                secondWindow,
                IgnoreOptionId.UseGitIgnore,
                visible: true,
                isChecked: true);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                secondWindow,
                IgnoreOptionId.SmartIgnore,
                visible: true,
                isChecked: true);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                secondWindow,
                IgnoreOptionId.DotFolders,
                visible: true,
                isChecked: true);

            await WaitForProjectTreePathStateAsync(secondWindow, exists: true, "api", "src", "Program.cs");
            await WaitForProjectTreePathStateAsync(secondWindow, exists: true, "web", "src", "app.ts");
            await WaitForProjectTreePathStateAsync(secondWindow, exists: true, "generated", "report.log");
            await WaitForProjectTreePathStateAsync(secondWindow, exists: false, "docs", "notes.md");
            await WaitForProjectTreePathStateAsync(secondWindow, exists: false, "data.csv");
            await WaitForProjectTreePathStateAsync(secondWindow, exists: false, "new-data.csv");
            await WaitForProjectTreePathStateAsync(secondWindow, exists: true, "empty.txt");
            await WaitForProjectTreePathStateAsync(secondWindow, exists: false, "empty-root");
            await AssertIgnoreOptionsStayStableAsync(secondWindow);
        }
        finally
        {
            if (secondWindow is not null)
                await UiTestDriver.CloseWindowAsync(secondWindow);
            if (firstWindow is not null)
                await UiTestDriver.CloseWindowAsync(firstWindow, cleanupAppData: false);
        }
    }

    [AvaloniaFact]
    public async Task ReopenThenRefresh_WithPersistedFullState_ChecksOnlyEntriesFirstSeenAtEachStage()
    {
        using var project = UiTestProject.CreateWithExternalRefreshMutationWorkspace();
        var appDataPath = Path.Combine(project.AppDataPath, "persisted-full-state-refresh-stage");
        MainWindow? firstWindow = null;
        MainWindow? secondWindow = null;

        try
        {
            firstWindow = await UiTestDriver.CreateLoadedMainWindowAsync(project, appDataPathOverride: appDataPath);

            await UiTestDriver.ClickRootFolderCheckBoxAsync(firstWindow, "docs");
            await UiTestDriver.ClickExtensionCheckBoxAsync(firstWindow, ".csv");
            await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(firstWindow, IgnoreOptionId.EmptyFiles);
            await ApplySettingsAndWaitForIgnoreRefreshAsync(firstWindow);

            await UiTestDriver.CloseWindowAsync(firstWindow, cleanupAppData: false);
            firstWindow = null;

            MutateExternalRefreshWorkspace(project.RootPath);
            secondWindow = await UiTestDriver.CreateLoadedMainWindowAsync(project, appDataPathOverride: appDataPath);

            await WaitForRootFolderStateAsync(secondWindow, "docs", visible: true, isChecked: false);
            await WaitForRootFolderStateAsync(secondWindow, "generated", visible: true, isChecked: true);
            await WaitForExtensionStateAsync(secondWindow, ".csv", visible: true, isChecked: false);
            await WaitForExtensionStateAsync(secondWindow, ".log", visible: true, isChecked: true);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                secondWindow,
                IgnoreOptionId.EmptyFiles,
                visible: true,
                isChecked: false);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                secondWindow,
                IgnoreOptionId.EmptyFolders,
                visible: true,
                isChecked: true);

            // The second stage makes newly discovered entries "known" and then turns
            // some of them off. The next refresh must preserve those manual choices.
            UiTestDriver.GetViewModel(secondWindow).Extensions.Single(option => option.Name == ".log").IsChecked = false;
            await UiTestDriver.WaitForSelectionRefreshIdleAsync(secondWindow);
            await UiTestDriver.ClickRootFolderCheckBoxAsync(secondWindow, "generated");
            await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(secondWindow, IgnoreOptionId.EmptyFolders);
            await ApplySettingsAndWaitForIgnoreRefreshAsync(secondWindow);

            MutateExternalRefreshWorkspaceSecondWave(project.RootPath);
            await UiTestDriver.RefreshProjectAsync(secondWindow);

            await WaitForRootFolderStateAsync(secondWindow, "docs", visible: true, isChecked: false);
            await WaitForRootFolderStateAsync(secondWindow, "generated", visible: true, isChecked: false);
            await WaitForRootFolderStateAsync(secondWindow, "cli", visible: true, isChecked: true);
            await WaitForRootFolderStateAsync(secondWindow, "scripts", visible: true, isChecked: true);
            await WaitForRootFolderStateAsync(secondWindow, "second-empty-root", visible: true, isChecked: true);

            await WaitForExtensionStateAsync(secondWindow, ".csv", visible: true, isChecked: false);
            await WaitForExtensionStateAsync(secondWindow, ".log", visible: true, isChecked: false);
            await WaitForExtensionStateAsync(secondWindow, ".go", visible: true, isChecked: true);
            await WaitForExtensionStateAsync(secondWindow, ".py", visible: true, isChecked: true);

            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                secondWindow,
                IgnoreOptionId.EmptyFiles,
                visible: true,
                isChecked: false);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                secondWindow,
                IgnoreOptionId.EmptyFolders,
                visible: true,
                isChecked: false);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                secondWindow,
                IgnoreOptionId.ExtensionlessFiles,
                visible: true,
                isChecked: true);

            await WaitForProjectTreePathStateAsync(secondWindow, exists: true, "cli", "main.go");
            await WaitForProjectTreePathStateAsync(secondWindow, exists: true, "scripts", "run.py");
            await WaitForProjectTreePathStateAsync(secondWindow, exists: true, "second-empty-root");
            await WaitForProjectTreePathStateAsync(secondWindow, exists: false, "docs", "notes.md");
            await WaitForProjectTreePathStateAsync(secondWindow, exists: false, "generated", "report.log");
            await WaitForProjectTreePathStateAsync(secondWindow, exists: false, "new-data.csv");
            await WaitForProjectTreePathStateAsync(secondWindow, exists: false, "Dockerfile");
            await WaitForProjectTreePathStateAsync(secondWindow, exists: false, ".vscode", "settings.json");
            await AssertIgnoreOptionsStayStableAsync(secondWindow);
        }
        finally
        {
            if (secondWindow is not null)
                await UiTestDriver.CloseWindowAsync(secondWindow);
            if (firstWindow is not null)
                await UiTestDriver.CloseWindowAsync(firstWindow, cleanupAppData: false);
        }
    }

    [AvaloniaFact]
    public async Task ReopenProject_WithLegacySelectedOnlyProfile_ChecksEntriesFirstSeenAfterReopenAcrossSections()
    {
        using var project = UiTestProject.CreateWithExternalRefreshMutationWorkspace();
        var appDataPath = Path.Combine(project.AppDataPath, "legacy-selected-only");
        MainWindow? window = null;

        try
        {
            WriteLegacySelectedOnlyProjectProfile(appDataPath, project.RootPath);
            MutateExternalRefreshWorkspace(project.RootPath);

            window = await UiTestDriver.CreateLoadedMainWindowAsync(project, appDataPathOverride: appDataPath);

            await WaitForRootFolderStateAsync(window, "src", visible: true, isChecked: true);
            await WaitForRootFolderStateAsync(window, "docs", visible: true, isChecked: true);
            await WaitForRootFolderStateAsync(window, "api", visible: true, isChecked: true);
            await WaitForRootFolderStateAsync(window, "web", visible: true, isChecked: true);
            await WaitForRootFolderStateAsync(window, "generated", visible: true, isChecked: true);

            await WaitForExtensionStateAsync(window, ".cs", visible: true, isChecked: true);
            await WaitForExtensionStateAsync(window, ".csv", visible: true, isChecked: true);
            await WaitForExtensionStateAsync(window, ".ts", visible: true, isChecked: true);
            await WaitForExtensionStateAsync(window, ".log", visible: true, isChecked: true);

            await UiTestDriver.WaitForIgnoreOptionStateAsync(window, IgnoreOptionId.EmptyFiles, visible: true, isChecked: true);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(window, IgnoreOptionId.UseGitIgnore, visible: true, isChecked: true);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(window, IgnoreOptionId.SmartIgnore, visible: true, isChecked: true);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(window, IgnoreOptionId.DotFolders, visible: true, isChecked: true);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(window, IgnoreOptionId.DotFiles, visible: true, isChecked: true);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(window, IgnoreOptionId.EmptyFolders, visible: true, isChecked: true);
        }
        finally
        {
            if (window is not null)
                await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task ReopenProject_WithEmptyFullStateProfile_ChecksEntriesFirstSeenAfterReopenAcrossSections()
    {
        using var project = UiTestProject.CreateWithExternalRefreshMutationWorkspace();
        var appDataPath = Path.Combine(project.AppDataPath, "empty-full-state");
        MainWindow? window = null;

        try
        {
            WriteEmptyFullStateProjectProfile(appDataPath, project.RootPath);
            MutateExternalRefreshWorkspace(project.RootPath);

            window = await UiTestDriver.CreateLoadedMainWindowAsync(project, appDataPathOverride: appDataPath);

            // This shape is produced when selected-only profiles were rewritten before
            // the UI had observed every option that can appear after a later reopen.
            await WaitForRootFolderStateAsync(window, "src", visible: true, isChecked: true);
            await WaitForRootFolderStateAsync(window, "docs", visible: true, isChecked: true);
            await WaitForRootFolderStateAsync(window, "api", visible: true, isChecked: true);
            await WaitForRootFolderStateAsync(window, "web", visible: true, isChecked: true);
            await WaitForRootFolderStateAsync(window, "generated", visible: true, isChecked: true);

            await WaitForExtensionStateAsync(window, ".cs", visible: true, isChecked: true);
            await WaitForExtensionStateAsync(window, ".csv", visible: true, isChecked: true);
            await WaitForExtensionStateAsync(window, ".ts", visible: true, isChecked: true);
            await WaitForExtensionStateAsync(window, ".log", visible: true, isChecked: true);

            await UiTestDriver.WaitForIgnoreOptionStateAsync(window, IgnoreOptionId.EmptyFiles, visible: true, isChecked: true);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(window, IgnoreOptionId.UseGitIgnore, visible: true, isChecked: true);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(window, IgnoreOptionId.SmartIgnore, visible: true, isChecked: true);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(window, IgnoreOptionId.DotFolders, visible: true, isChecked: true);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(window, IgnoreOptionId.DotFiles, visible: true, isChecked: true);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(window, IgnoreOptionId.EmptyFolders, visible: true, isChecked: true);
        }
        finally
        {
            if (window is not null)
                await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task PythonProjectWithoutGitIgnore_ShowsSmartIgnoreAndHidesSmartArtifacts()
    {
        using var project = UiTestProject.CreateWithPythonSmartIgnoreWorkspace();
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

        try
        {
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.SmartIgnore,
                visible: true,
                isChecked: true);

            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.UseGitIgnore,
                visible: false);

            var viewModel = UiTestDriver.GetViewModel(window);
            Assert.Contains(viewModel.RootFolders, option => string.Equals(option.Name, "src", StringComparison.Ordinal));

            await WaitForProjectTreePathStateAsync(window, exists: true, "src", "app.py");
            await WaitForProjectTreePathStateAsync(window, exists: false, "src", "__pycache__");
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task PythonProjectWithGitIgnore_ShowsGitIgnoreOnlyAndUsesItAsSmartController()
    {
        using var project = UiTestProject.CreateWithPythonGitIgnoreWorkspace();
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

        try
        {
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.UseGitIgnore,
                visible: true,
                isChecked: true);

            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.SmartIgnore,
                visible: false);

            await WaitForProjectTreePathStateAsync(window, exists: true, "src", "app.py");
            await WaitForProjectTreePathStateAsync(window, exists: false, "src", "__pycache__");
            await WaitForProjectTreePathStateAsync(window, exists: false, "logs", "app.log");

            await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(window, IgnoreOptionId.UseGitIgnore);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.UseGitIgnore,
                visible: true,
                isChecked: false);

            await UiTestDriver.ClickApplySettingsAsync(window);
            await UiTestDriver.WaitForSelectionRefreshIdleAsync(window);

            await WaitForProjectTreePathStateAsync(window, exists: true, "src", "__pycache__");
            await WaitForProjectTreePathStateAsync(window, exists: true, "logs", "app.log");
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task PythonProjectWithIdeaFolder_KeepsDotFoldersToggleAvailableAfterSmartIgnoreChanges()
    {
        using var project = UiTestProject.CreateWithPythonSmartIgnoreAndIdeaWorkspace();
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

        try
        {
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.SmartIgnore,
                visible: true,
                isChecked: true);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.DotFolders,
                visible: true);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.UseGitIgnore,
                visible: false);

            if (UiTestDriver.GetViewModel(window).IgnoreOptions.Single(option => option.Id == IgnoreOptionId.DotFolders).IsChecked is false)
            {
                await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(window, IgnoreOptionId.DotFolders);
                await UiTestDriver.WaitForIgnoreOptionStateAsync(
                    window,
                    IgnoreOptionId.DotFolders,
                    visible: true,
                    isChecked: true);
                await UiTestDriver.ClickApplySettingsAsync(window);
                await UiTestDriver.WaitForSelectionRefreshIdleAsync(window);
            }

            await WaitForProjectTreePathStateAsync(window, exists: false, ".idea");

            await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(window, IgnoreOptionId.SmartIgnore);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.SmartIgnore,
                visible: true,
                isChecked: false);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.DotFolders,
                visible: true,
                isChecked: true);

            await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(window, IgnoreOptionId.DotFolders);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.DotFolders,
                visible: true,
                isChecked: false);

            await UiTestDriver.ClickApplySettingsAsync(window);
            await UiTestDriver.WaitForSelectionRefreshIdleAsync(window);

            await WaitForProjectTreePathStateAsync(window, exists: true, ".idea", "workspace.xml");
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task NestedPythonProjectWithIdeaFolder_SmartOnlyKeepsDotFolderVisible()
    {
        using var project = UiTestProject.CreateWithNestedPythonSmartIgnoreAndIdeaWorkspace();
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

        try
        {
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.SmartIgnore,
                visible: true,
                isChecked: true);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.DotFolders,
                visible: true);

            await SetIgnoreOptionCheckedAsync(window, IgnoreOptionId.DotFolders, isChecked: false);
            await ApplySettingsAndWaitForIgnoreRefreshAsync(window);

            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.SmartIgnore,
                visible: true,
                isChecked: true);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.DotFolders,
                visible: true,
                isChecked: false);
            await WaitForProjectTreePathStateAsync(window, exists: true, "lab2", ".idea", "workspace.xml");
            await WaitForProjectTreePathStateAsync(window, exists: false, "lab2", "__pycache__");
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task NestedPolyglotWorkspace_AllOffAndSingleIgnoreTogglesStayScoped()
    {
        using var project = UiTestProject.CreateWithNestedPolyglotIgnoreMatrixWorkspace();
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

        try
        {
            await UiTestDriver.WaitForIgnoreOptionStateAsync(window, IgnoreOptionId.SmartIgnore, visible: true, isChecked: true);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(window, IgnoreOptionId.UseGitIgnore, visible: true, isChecked: true);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(window, IgnoreOptionId.DotFolders, visible: true, isChecked: true);

            await SetVisibleIgnoreOptionsCheckedAsync(window, isChecked: false);
            await ApplySettingsAndWaitForIgnoreRefreshAsync(window);
            Assert.False(UiTestDriver.GetViewModel(window).AllIgnoreChecked);
            Assert.DoesNotContain(UiTestDriver.GetViewModel(window).IgnoreOptions, option => option.IsChecked);
            await AssertExtensionStatesAsync(
                window,
                visibleChecked: [".dll", ".log", ".js", ".pyc", ".xml", ".env"],
                hidden: []);
            await AssertNestedPolyglotTreeStateAsync(
                window,
                visiblePaths:
                [
                    ["api", "bin", "Debug", "app.dll"],
                    ["api", "logs", "runtime.log"],
                    ["web", "node_modules", "pkg", "index.js"],
                    ["python", "__pycache__", "app.pyc"],
                    [".idea", "workspace.xml"],
                    [".env"],
                    ["README"],
                    ["empty.txt"],
                    ["empty-root"]
                ],
                hiddenPaths: []);
            await AssertIgnoreOptionsStayStableAsync(window);

            await SetIgnoreOptionCheckedAsync(window, IgnoreOptionId.SmartIgnore, isChecked: true);
            await ApplySettingsAndWaitForIgnoreRefreshAsync(window);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(window, IgnoreOptionId.SmartIgnore, visible: true, isChecked: true);
            await AssertExtensionStatesAsync(
                window,
                visibleChecked: [".log", ".xml", ".env"],
                hidden: [".dll", ".js", ".pyc"]);
            await AssertNestedPolyglotTreeStateAsync(
                window,
                visiblePaths:
                [
                    ["api", "logs", "runtime.log"],
                    [".idea", "workspace.xml"],
                    [".env"],
                    ["README"],
                    ["empty.txt"],
                    ["empty-root"]
                ],
                hiddenPaths:
                [
                    ["api", "bin", "Debug", "app.dll"],
                    ["web", "node_modules", "pkg", "index.js"],
                    ["python", "__pycache__", "app.pyc"]
                ]);
            await AssertIgnoreOptionsStayStableAsync(window);

            await SetIgnoreOptionCheckedAsync(window, IgnoreOptionId.SmartIgnore, isChecked: false);
            await SetIgnoreOptionCheckedAsync(window, IgnoreOptionId.UseGitIgnore, isChecked: true);
            await ApplySettingsAndWaitForIgnoreRefreshAsync(window);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(window, IgnoreOptionId.UseGitIgnore, visible: true, isChecked: true);
            await AssertExtensionStatesAsync(
                window,
                visibleChecked: [".dll", ".js", ".pyc", ".xml", ".env"],
                hidden: [".log"]);
            await AssertNestedPolyglotTreeStateAsync(
                window,
                visiblePaths:
                [
                    ["api", "bin", "Debug", "app.dll"],
                    ["web", "node_modules", "pkg", "index.js"],
                    ["python", "__pycache__", "app.pyc"],
                    [".idea", "workspace.xml"],
                    [".env"],
                    ["README"],
                    ["empty.txt"],
                    ["empty-root"]
                ],
                hiddenPaths: [["api", "logs", "runtime.log"]]);
            await AssertIgnoreOptionsStayStableAsync(window);

            await SetIgnoreOptionCheckedAsync(window, IgnoreOptionId.UseGitIgnore, isChecked: false);
            await SetIgnoreOptionCheckedAsync(window, IgnoreOptionId.DotFolders, isChecked: true);
            await SetIgnoreOptionCheckedAsync(window, IgnoreOptionId.DotFiles, isChecked: true);
            await ApplySettingsAndWaitForIgnoreRefreshAsync(window);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(window, IgnoreOptionId.DotFolders, visible: true, isChecked: true);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(window, IgnoreOptionId.DotFiles, visible: true, isChecked: true);
            await AssertExtensionStatesAsync(
                window,
                visibleChecked: [".dll", ".log", ".js", ".pyc", ".env"],
                hidden: [".xml"]);
            await AssertNestedPolyglotTreeStateAsync(
                window,
                visiblePaths:
                [
                    ["api", "bin", "Debug", "app.dll"],
                    ["api", "logs", "runtime.log"],
                    ["web", "node_modules", "pkg", "index.js"],
                    ["python", "__pycache__", "app.pyc"],
                    ["README"],
                    ["empty.txt"],
                    ["empty-root"]
                ],
                hiddenPaths:
                [
                    [".idea", "workspace.xml"],
                    [".env"]
                ]);
            await AssertIgnoreOptionsStayStableAsync(window);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task PythonProjectWithIdeaGitIgnore_DoesNotExposeGitIgnoreOptionAcrossSmartAndDotToggles()
    {
        using var project = UiTestProject.CreateWithPythonSmartIgnoreAndIdeaWorkspace();
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

        try
        {
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.SmartIgnore,
                visible: true,
                isChecked: true);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.UseGitIgnore,
                visible: false);
            await SetIgnoreOptionCheckedAsync(window, IgnoreOptionId.DotFolders, isChecked: true);
            await ApplySettingsAndWaitForIgnoreRefreshAsync(window);

            await SetIgnoreOptionCheckedAsync(window, IgnoreOptionId.SmartIgnore, isChecked: false);
            await ApplySettingsAndWaitForIgnoreRefreshAsync(window);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.UseGitIgnore,
                visible: false);
            await AssertPythonIdeaWorkspaceStateAsync(
                window,
                smartChecked: false,
                dotChecked: true,
                ideaVisible: false,
                pycacheVisible: true);

            await SetIgnoreOptionCheckedAsync(window, IgnoreOptionId.DotFolders, isChecked: false);
            await ApplySettingsAndWaitForIgnoreRefreshAsync(window);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.UseGitIgnore,
                visible: false);
            await AssertPythonIdeaWorkspaceStateAsync(
                window,
                smartChecked: false,
                dotChecked: false,
                ideaVisible: true,
                pycacheVisible: true);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task PythonProjectWithIdeaFolder_IgnoreOptionsStayStableAcrossRepeatedRefreshes()
    {
        using var project = UiTestProject.CreateWithPythonSmartIgnoreAndIdeaWorkspace();
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

        try
        {
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.SmartIgnore,
                visible: true,
                isChecked: true);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.DotFolders,
                visible: true);

            if (UiTestDriver.GetViewModel(window).IgnoreOptions.Single(option => option.Id == IgnoreOptionId.DotFolders).IsChecked is false)
            {
                await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(window, IgnoreOptionId.DotFolders);
                await UiTestDriver.WaitForIgnoreOptionStateAsync(
                    window,
                    IgnoreOptionId.DotFolders,
                    visible: true,
                    isChecked: true);
                await UiTestDriver.ClickApplySettingsAsync(window);
                await UiTestDriver.WaitForSelectionRefreshIdleAsync(window);
            }

            await AssertIgnoreOptionsStayStableAsync(window);

            await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(window, IgnoreOptionId.SmartIgnore);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.SmartIgnore,
                visible: true,
                isChecked: false);
            await UiTestDriver.ClickApplySettingsAsync(window);
            await UiTestDriver.WaitForSelectionRefreshIdleAsync(window);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.DotFolders,
                visible: true,
                isChecked: true);

            await AssertIgnoreOptionsStayStableAsync(window);

            await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(window, IgnoreOptionId.DotFolders);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.DotFolders,
                visible: true,
                isChecked: false);
            await UiTestDriver.ClickApplySettingsAsync(window);
            await UiTestDriver.WaitForSelectionRefreshIdleAsync(window);
            await WaitForProjectTreePathStateAsync(window, exists: true, ".idea", "workspace.xml");

            await AssertIgnoreOptionsStayStableAsync(window);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task PythonProjectWithIdeaFolder_RepeatedSmartAndDotFolderCyclesKeepTreeAndTogglesAligned()
    {
        using var project = UiTestProject.CreateWithPythonSmartIgnoreAndIdeaWorkspace();
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

        try
        {
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.SmartIgnore,
                visible: true,
                isChecked: true);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.DotFolders,
                visible: true);
            await SetIgnoreOptionCheckedAsync(window, IgnoreOptionId.DotFolders, isChecked: true);
            await ApplySettingsAndWaitForIgnoreRefreshAsync(window);

            for (var cycle = 0; cycle < 2; cycle++)
            {
                await SetIgnoreOptionCheckedAsync(window, IgnoreOptionId.SmartIgnore, isChecked: false);
                await ApplySettingsAndWaitForIgnoreRefreshAsync(window);
                await UiTestDriver.WaitForIgnoreOptionStateAsync(
                    window,
                    IgnoreOptionId.SmartIgnore,
                    visible: true,
                    isChecked: false);
                await UiTestDriver.WaitForIgnoreOptionStateAsync(
                    window,
                    IgnoreOptionId.DotFolders,
                    visible: true,
                    isChecked: true);
                await WaitForProjectTreePathStateAsync(window, exists: false, ".idea", "workspace.xml");
                await WaitForProjectTreePathStateAsync(window, exists: true, "src", "__pycache__", "app.pyc");

                await SetIgnoreOptionCheckedAsync(window, IgnoreOptionId.DotFolders, isChecked: false);
                await ApplySettingsAndWaitForIgnoreRefreshAsync(window);
                await UiTestDriver.WaitForIgnoreOptionStateAsync(
                    window,
                    IgnoreOptionId.DotFolders,
                    visible: true,
                    isChecked: false);
                await WaitForProjectTreePathStateAsync(window, exists: true, ".idea", "workspace.xml");
                await WaitForProjectTreePathStateAsync(window, exists: true, "src", "__pycache__", "app.pyc");

                await SetIgnoreOptionCheckedAsync(window, IgnoreOptionId.SmartIgnore, isChecked: true);
                await ApplySettingsAndWaitForIgnoreRefreshAsync(window);
                await UiTestDriver.WaitForIgnoreOptionStateAsync(
                    window,
                    IgnoreOptionId.SmartIgnore,
                    visible: true,
                    isChecked: true);
                await UiTestDriver.WaitForIgnoreOptionStateAsync(
                    window,
                    IgnoreOptionId.DotFolders,
                    visible: true,
                    isChecked: false);
                await WaitForProjectTreePathStateAsync(window, exists: true, ".idea", "workspace.xml");
                await WaitForProjectTreePathStateAsync(window, exists: false, "src", "__pycache__");

                await SetIgnoreOptionCheckedAsync(window, IgnoreOptionId.DotFolders, isChecked: true);
                await ApplySettingsAndWaitForIgnoreRefreshAsync(window);
                await UiTestDriver.WaitForIgnoreOptionStateAsync(
                    window,
                    IgnoreOptionId.DotFolders,
                    visible: true,
                    isChecked: true);
                await WaitForProjectTreePathStateAsync(window, exists: false, ".idea", "workspace.xml");
                await WaitForProjectTreePathStateAsync(window, exists: false, "src", "__pycache__");
            }

            await AssertIgnoreOptionsStayStableAsync(window);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task PythonProjectWithIdeaFolder_RapidSmartAndDotFolderChangesConvergeToLastAppliedState()
    {
        using var project = UiTestProject.CreateWithPythonSmartIgnoreAndIdeaWorkspace();
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

        try
        {
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.SmartIgnore,
                visible: true,
                isChecked: true);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.DotFolders,
                visible: true);
            await SetIgnoreOptionCheckedAsync(window, IgnoreOptionId.DotFolders, isChecked: true);
            await ApplySettingsAndWaitForIgnoreRefreshAsync(window);

            for (var cycle = 0; cycle < 3; cycle++)
            {
                await SetIgnoreOptionCheckedAsync(window, IgnoreOptionId.SmartIgnore, isChecked: false);
                await SetIgnoreOptionCheckedAsync(window, IgnoreOptionId.DotFolders, isChecked: false);
                await SetIgnoreOptionCheckedAsync(window, IgnoreOptionId.SmartIgnore, isChecked: true);
                await ApplySettingsAndWaitForIgnoreRefreshAsync(window);
                await AssertPythonIdeaWorkspaceStateAsync(
                    window,
                    smartChecked: true,
                    dotChecked: false,
                    ideaVisible: true,
                    pycacheVisible: false);
                await AssertIgnoreOptionsStayStableAsync(window);

                await SetIgnoreOptionCheckedAsync(window, IgnoreOptionId.DotFolders, isChecked: true);
                await SetIgnoreOptionCheckedAsync(window, IgnoreOptionId.SmartIgnore, isChecked: false);
                await ApplySettingsAndWaitForIgnoreRefreshAsync(window);
                await AssertPythonIdeaWorkspaceStateAsync(
                    window,
                    smartChecked: false,
                    dotChecked: true,
                    ideaVisible: false,
                    pycacheVisible: true);
                await AssertIgnoreOptionsStayStableAsync(window);
            }

            await SetIgnoreOptionCheckedAsync(window, IgnoreOptionId.SmartIgnore, isChecked: true);
            await SetIgnoreOptionCheckedAsync(window, IgnoreOptionId.DotFolders, isChecked: true);
            await ApplySettingsAndWaitForIgnoreRefreshAsync(window);
            await AssertPythonIdeaWorkspaceStateAsync(
                window,
                smartChecked: true,
                dotChecked: true,
                ideaVisible: false,
                pycacheVisible: false);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task PythonProjectWithIdeaFolder_BlockedStaleSmartRefreshCannotRestoreOldIgnoreChecks()
    {
        using var project = UiTestProject.CreateWithPythonSmartIgnoreAndIdeaWorkspace();
        using var blockingScanner = new SwitchableBlockingFileSystemScanner(
            project.RootPath,
            ignoreCancellation: true);
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(
            project,
            configureServices: services => services with
            {
                ScanOptionsUseCase = new ScanOptionsUseCase(blockingScanner)
            });

        try
        {
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.SmartIgnore,
                visible: true,
                isChecked: true);
            await SetIgnoreOptionCheckedAsync(window, IgnoreOptionId.DotFolders, isChecked: true);
            await ApplySettingsAndWaitForIgnoreRefreshAsync(window);

            blockingScanner.EnableBlocking();
            await SetIgnoreOptionCheckedAsync(window, IgnoreOptionId.SmartIgnore, isChecked: false);
            Assert.True(
                blockingScanner.WaitForBlockedCall(TimeSpan.FromSeconds(10)),
                "The stale Python smart-ignore refresh did not reach the controlled scanner block.");

            await SetIgnoreOptionCheckedAsync(window, IgnoreOptionId.DotFolders, isChecked: false);
            blockingScanner.Release();
            await ApplySettingsAndWaitForIgnoreRefreshAsync(window);

            await AssertPythonIdeaWorkspaceStateAsync(
                window,
                smartChecked: false,
                dotChecked: false,
                ideaVisible: true,
                pycacheVisible: true);
            await AssertIgnoreOptionsStayStableAsync(window);
        }
        finally
        {
            blockingScanner.Release();
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task ProjectSwitch_BlockedStaleIgnoreRefreshDoesNotOverwriteNewProject()
    {
        using var projectA = UiTestProject.CreateWithPythonSmartIgnoreAndIdeaWorkspace();
        using var projectB = UiTestProject.CreateWithPythonSmartIgnoreWorkspace();
        using var blockingScanner = new SwitchableBlockingFileSystemScanner(projectA.RootPath);
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(
            projectA,
            configureServices: services => services with
            {
                ScanOptionsUseCase = new ScanOptionsUseCase(blockingScanner)
            });

        try
        {
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.SmartIgnore,
                visible: true,
                isChecked: true);

            blockingScanner.EnableBlocking();
            await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(window, IgnoreOptionId.SmartIgnore);
            Assert.True(
                blockingScanner.WaitForBlockedCall(TimeSpan.FromSeconds(10)),
                "The stale project refresh did not reach the controlled scanner block.");

            var openProjectB = UiTestDriver.OpenFolderAsync(window, projectB.RootPath);
            await UiTestDriver.WaitForSettledFramesAsync(frameCount: 8);
            blockingScanner.Release();
            await openProjectB.WaitAsync(TimeSpan.FromSeconds(40));

            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.SmartIgnore,
                visible: true,
                isChecked: true);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.UseGitIgnore,
                visible: false);
            await WaitForProjectTreePathStateAsync(window, exists: true, "src", "app.py");
            await WaitForProjectTreePathStateAsync(window, exists: false, ".idea", "workspace.xml");
            await WaitForProjectTreePathStateAsync(window, exists: false, "src", "__pycache__", "app.pyc");
            Assert.DoesNotContain(
                UiTestDriver.GetViewModel(window).RootFolders,
                option => string.Equals(option.Name, ".idea", StringComparison.Ordinal));
        }
        finally
        {
            blockingScanner.Release();
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task MixedRootExtensionAndIgnoreChanges_ConvergeToLastAppliedState()
    {
        using var project = UiTestProject.CreateWithRootExtensionIgnoreStressWorkspace();
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

        try
        {
            await WaitForRootFolderStateAsync(window, "api", visible: true, isChecked: true);
            await WaitForRootFolderStateAsync(window, "web", visible: true, isChecked: true);
            await WaitForRootFolderStateAsync(window, "docs", visible: true, isChecked: true);
            await WaitForExtensionStateAsync(window, ".log", visible: true, isChecked: true);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(window, IgnoreOptionId.UseGitIgnore, visible: true, isChecked: true);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(window, IgnoreOptionId.SmartIgnore, visible: true, isChecked: true);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(window, IgnoreOptionId.DotFolders, visible: true, isChecked: true);

            await UiTestDriver.ClickRootFolderCheckBoxAsync(window, "docs");
            await SetIgnoreOptionCheckedAsync(window, IgnoreOptionId.UseGitIgnore, isChecked: false);
            await SetIgnoreOptionCheckedAsync(window, IgnoreOptionId.SmartIgnore, isChecked: false);
            await SetIgnoreOptionCheckedAsync(window, IgnoreOptionId.DotFolders, isChecked: false);
            await UiTestDriver.ClickExtensionCheckBoxAsync(window, ".log");

            await WaitForRootFolderStateAsync(window, "docs", visible: true, isChecked: false);
            await WaitForExtensionStateAsync(window, ".log", visible: true, isChecked: false);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(window, IgnoreOptionId.UseGitIgnore, visible: true, isChecked: false);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(window, IgnoreOptionId.SmartIgnore, visible: true, isChecked: false);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(window, IgnoreOptionId.DotFolders, visible: true, isChecked: false);
            await ApplySettingsAndWaitForIgnoreRefreshAsync(window);
            await WaitForProjectTreePathStateAsync(window, exists: true, "api", "src", "Program.cs");
            await WaitForProjectTreePathStateAsync(window, exists: true, "api", ".visible-dot", "inside.cs");
            await WaitForProjectTreePathStateAsync(window, exists: false, "api", "src", "runtime.log");
            await WaitForProjectTreePathStateAsync(window, exists: false, "docs", "readme.md");

            await UiTestDriver.ClickExtensionCheckBoxAsync(window, ".log");
            await SetIgnoreOptionCheckedAsync(window, IgnoreOptionId.UseGitIgnore, isChecked: true);
            await SetIgnoreOptionCheckedAsync(window, IgnoreOptionId.SmartIgnore, isChecked: true);
            await SetIgnoreOptionCheckedAsync(window, IgnoreOptionId.DotFolders, isChecked: true);
            await UiTestDriver.ClickRootFolderCheckBoxAsync(window, "docs");

            await WaitForRootFolderStateAsync(window, "docs", visible: true, isChecked: true);
            await WaitForExtensionStateAsync(window, ".log", visible: true, isChecked: true);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(window, IgnoreOptionId.UseGitIgnore, visible: true, isChecked: true);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(window, IgnoreOptionId.SmartIgnore, visible: true, isChecked: true);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(window, IgnoreOptionId.DotFolders, visible: true, isChecked: true);
            await ApplySettingsAndWaitForIgnoreRefreshAsync(window);
            await WaitForProjectTreePathStateAsync(window, exists: true, "api", "src", "Program.cs");
            await WaitForProjectTreePathStateAsync(window, exists: true, "api", "src", "important.log");
            await WaitForProjectTreePathStateAsync(window, exists: true, "docs", "readme.md");
            await WaitForProjectTreePathStateAsync(window, exists: false, "api", "src", "runtime.log");
            await WaitForProjectTreePathStateAsync(window, exists: false, "api", ".visible-dot", "inside.cs");
            await AssertIgnoreOptionsStayStableAsync(window);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    private static async Task AssertDynamicIgnoreOptionStateIsPreservedWhenRootSelectionRestoresIt(
        IgnoreOptionId optionId)
    {
        using var project = UiTestProject.CreateWithDynamicIgnoreEntries();
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

        try
        {
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                optionId,
                visible: true,
                isChecked: true);

            var dynamicCheckBox = UiTestDriver.GetRequiredIgnoreOptionCheckBox(window, optionId);
            await UiTestDriver.ClickAsync(window, dynamicCheckBox);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                optionId,
                visible: true,
                isChecked: false);

            var srcRootCheckBox = UiTestDriver.GetRequiredRootFolderCheckBox(window, "src");
            await UiTestDriver.ClickAsync(window, srcRootCheckBox);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                optionId,
                visible: false);

            await UiTestDriver.ClickAsync(window, srcRootCheckBox);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                optionId,
                visible: true,
                isChecked: false);

            Assert.False(UiTestDriver.GetViewModel(window).AllIgnoreChecked);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    private static async Task WaitForRootFolderStateAsync(
        MainWindow window,
        string rootFolderName,
        bool visible,
        bool? isChecked = null)
    {
        await UiTestDriver.WaitForConditionAsync(
            window,
            () =>
            {
                var option = UiTestDriver.GetViewModel(window).RootFolders
                    .FirstOrDefault(candidate => string.Equals(candidate.Name, rootFolderName, StringComparison.Ordinal));
                if (!visible)
                    return option is null;

                return option is not null && (isChecked is null || option.IsChecked == isChecked);
            },
            $"root folder option '{rootFolderName}' to be visible={visible}, checked={isChecked?.ToString() ?? "<any>"}");
    }

    private static async Task WaitForExtensionStateAsync(
        MainWindow window,
        string extensionName,
        bool visible,
        bool? isChecked = null)
    {
        await UiTestDriver.WaitForConditionAsync(
            window,
            () =>
            {
                var option = UiTestDriver.GetViewModel(window).Extensions
                    .FirstOrDefault(candidate => string.Equals(candidate.Name, extensionName, StringComparison.OrdinalIgnoreCase));
                if (!visible)
                    return option is null;

                return option is not null && (isChecked is null || option.IsChecked == isChecked);
            },
            $"extension option '{extensionName}' to be visible={visible}, checked={isChecked?.ToString() ?? "<any>"}");
    }

    private static void MutateExternalRefreshWorkspace(string rootPath)
    {
        WriteTextFile(rootPath, Path.Combine("api", ".gitignore"), "logs/\n");
        WriteTextFile(rootPath, Path.Combine("api", "App.csproj"), "<Project />\n");
        WriteTextFile(rootPath, Path.Combine("api", "src", "Program.cs"), "class Program {}\n");
        WriteTextFile(rootPath, Path.Combine("api", "logs", "runtime.log"), "ignored by nested gitignore\n");
        WriteTextFile(rootPath, Path.Combine("web", "package.json"), "{}\n");
        WriteTextFile(rootPath, Path.Combine("web", "src", "app.ts"), "export const app = true;\n");
        WriteTextFile(rootPath, Path.Combine("web", "node_modules", "pkg", "index.js"), "module.exports = {};\n");
        WriteTextFile(rootPath, Path.Combine("generated", "report.log"), "new visible log\n");
        WriteTextFile(rootPath, "new-data.csv", "2,updated\n");
        WriteTextFile(rootPath, Path.Combine(".idea", "workspace.xml"), "<project />\n");
        WriteTextFile(rootPath, ".env", "APP_ENV=test\n");
        Directory.CreateDirectory(Path.Combine(rootPath, "empty-root"));
    }

    private static void MutateExternalRefreshWorkspaceSecondWave(string rootPath)
    {
        WriteTextFile(rootPath, Path.Combine("cli", "go.mod"), "module refreshstage\n");
        WriteTextFile(rootPath, Path.Combine("cli", "main.go"), "package main\nfunc main() {}\n");
        WriteTextFile(rootPath, Path.Combine("scripts", "run.py"), "print('refresh stage')\n");
        WriteTextFile(rootPath, Path.Combine("scripts", "debug.log"), "manual extension state must survive refresh\n");
        WriteTextFile(rootPath, Path.Combine(".vscode", "settings.json"), "{}\n");
        WriteTextFile(rootPath, "Dockerfile", "FROM scratch\n");
        Directory.CreateDirectory(Path.Combine(rootPath, "second-empty-root"));
    }

    private static void WriteLegacySelectedOnlyProjectProfile(string appDataPath, string projectPath)
    {
        var storePath = Path.Combine(appDataPath, "DevProjex", "project-profiles.json");
        Directory.CreateDirectory(Path.GetDirectoryName(storePath)!);
        var projectKey = System.Text.Json.JsonSerializer.Serialize(PathUtility.Normalize(projectPath));
        var json = $$"""
            {
              "schemaVersion": 1,
              "profiles": {
                {{projectKey}}: {
                  "selectedRootFolders": [ "src" ],
                  "selectedExtensions": [ ".cs" ],
                  "selectedIgnoreOptions": [ "emptyFiles" ],
                  "updatedUtc": "2026-05-01T00:00:00+00:00"
                }
              }
            }
            """;

        File.WriteAllText(storePath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static void WriteEmptyFullStateProjectProfile(string appDataPath, string projectPath)
    {
        var storePath = Path.Combine(appDataPath, "DevProjex", "project-profiles.json");
        Directory.CreateDirectory(Path.GetDirectoryName(storePath)!);
        var projectKey = System.Text.Json.JsonSerializer.Serialize(PathUtility.Normalize(projectPath));
        var json = $$"""
            {
              "schemaVersion": 2,
              "profiles": {
                {{projectKey}}: {
                  "selectedRootFolders": [ "src" ],
                  "selectedExtensions": [ ".cs" ],
                  "selectedIgnoreOptions": [ "emptyFiles" ],
                  "rootFolderStates": {},
                  "extensionStates": {},
                  "ignoreOptionStates": {},
                  "updatedUtc": "2026-05-01T00:00:00+00:00"
                }
              }
            }
            """;

        File.WriteAllText(storePath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static void WriteTextFile(string rootPath, string relativePath, string content)
    {
        var fullPath = Path.Combine(rootPath, relativePath);
        var directoryPath = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directoryPath))
            Directory.CreateDirectory(directoryPath);

        File.WriteAllText(fullPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static async Task AssertPythonIdeaWorkspaceStateAsync(
        MainWindow window,
        bool smartChecked,
        bool dotChecked,
        bool ideaVisible,
        bool pycacheVisible)
    {
        await UiTestDriver.WaitForIgnoreOptionStateAsync(
            window,
            IgnoreOptionId.SmartIgnore,
            visible: true,
            isChecked: smartChecked);
        await UiTestDriver.WaitForIgnoreOptionStateAsync(
            window,
            IgnoreOptionId.DotFolders,
            visible: true,
            isChecked: dotChecked);
        await WaitForProjectTreePathStateAsync(window, exists: ideaVisible, ".idea", "workspace.xml");
        await WaitForProjectTreePathStateAsync(window, exists: pycacheVisible, "src", "__pycache__", "app.pyc");
    }

    private static async Task AssertIgnoreOptionsStayStableAsync(MainWindow window)
    {
        var expected = CaptureIgnoreOptionState(window);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await UiTestDriver.WaitForSelectionRefreshIdleAsync(window);
            await UiTestDriver.WaitForSettledFramesAsync(frameCount: 8);
            Assert.Equal(expected, CaptureIgnoreOptionState(window));
        }
    }

    private static IReadOnlyList<(IgnoreOptionId Id, bool IsChecked)> CaptureIgnoreOptionState(MainWindow window)
    {
        return UiTestDriver.GetViewModel(window).IgnoreOptions
            .Select(option => (option.Id, option.IsChecked))
            .ToArray();
    }

    private static async Task SetIgnoreOptionCheckedAsync(
        MainWindow window,
        IgnoreOptionId optionId,
        bool isChecked)
    {
        await UiTestDriver.WaitForIgnoreOptionStateAsync(window, optionId, visible: true);

        var option = UiTestDriver.GetViewModel(window).IgnoreOptions.Single(candidate => candidate.Id == optionId);
        if (option.IsChecked == isChecked)
            return;

        await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(window, optionId);
        await UiTestDriver.WaitForIgnoreOptionStateAsync(
            window,
            optionId,
            visible: true,
            isChecked: isChecked);
    }

    private static async Task SetVisibleIgnoreOptionsCheckedAsync(
        MainWindow window,
        bool isChecked)
    {
        foreach (var optionId in UiTestDriver.GetViewModel(window).IgnoreOptions.Select(option => option.Id).ToArray())
            await SetIgnoreOptionCheckedAsync(window, optionId, isChecked);
    }

    private static async Task ApplySettingsAndWaitForIgnoreRefreshAsync(MainWindow window)
    {
        await UiTestDriver.ClickApplySettingsAsync(window);
        await UiTestDriver.WaitForSelectionRefreshIdleAsync(window);
    }

    private static async Task AssertNestedPolyglotTreeStateAsync(
        MainWindow window,
        IReadOnlyCollection<string[]> visiblePaths,
        IReadOnlyCollection<string[]> hiddenPaths)
    {
        foreach (var visiblePath in visiblePaths)
            await WaitForProjectTreePathStateAsync(window, exists: true, visiblePath);
        foreach (var hiddenPath in hiddenPaths)
            await WaitForProjectTreePathStateAsync(window, exists: false, hiddenPath);
    }

    private static async Task AssertExtensionStatesAsync(
        MainWindow window,
        IReadOnlyCollection<string> visibleChecked,
        IReadOnlyCollection<string> hidden)
    {
        // These assertions bind the user-visible extension list to the currently applied
        // ignore controllers. A path can be correct while the extension checklist is stale.
        foreach (var extension in visibleChecked)
            await WaitForExtensionStateAsync(window, extension, visible: true, isChecked: true);
        foreach (var extension in hidden)
            await WaitForExtensionStateAsync(window, extension, visible: false);
    }

    private static async Task WaitForProjectTreePathStateAsync(
        MainWindow window,
        bool exists,
        params string[] relativeDisplayPath)
    {
        await UiTestDriver.WaitForConditionAsync(
            window,
            () => ProjectTreeContainsPath(window, relativeDisplayPath) == exists,
            $"project tree path '{string.Join("/", relativeDisplayPath)}' to exist={exists}");

        await UiTestDriver.WaitForSettledFramesAsync(frameCount: 6);
    }

    private static bool ProjectTreeContainsPath(MainWindow window, IReadOnlyList<string> relativeDisplayPath)
    {
        var roots = UiTestDriver.GetViewModel(window).TreeNodes;
        if (roots.Count != 1)
            return false;

        return ContainsTreePath(roots[0].Children, relativeDisplayPath);
    }

    private static bool ContainsTreePath(IEnumerable<TreeNodeViewModel> candidates, IReadOnlyList<string> displayPath)
    {
        var current = candidates;
        foreach (var segment in displayPath)
        {
            var match = current.FirstOrDefault(node => string.Equals(node.DisplayName, segment, StringComparison.Ordinal));
            if (match is null)
                return false;

            current = match.Children;
        }

        return true;
    }

    private sealed class SwitchableBlockingFileSystemScanner(
        string blockedRootPath,
        bool ignoreCancellation = false)
        : IFileSystemScanner,
            IFileSystemScannerAdvanced,
            IFileSystemScannerEffectiveEmptyFolderCounter,
            IFileSystemScannerEffectiveIgnoreCountsProvider,
            IFileSystemScannerIgnoreSectionSnapshotProvider,
            IFileSystemScannerExtensionPolicySnapshotProvider,
            IDisposable
    {
        private readonly FileSystemScanner _inner = new();
        private readonly ManualResetEventSlim _blocked = new(initialState: false);
        private readonly ManualResetEventSlim _release = new(initialState: false);
        private readonly bool _ignoreCancellation = ignoreCancellation;
        private int _enabled;

        public void EnableBlocking() => Volatile.Write(ref _enabled, 1);

        public bool WaitForBlockedCall(TimeSpan timeout) => _blocked.Wait(timeout);

        public void Release() => _release.Set();

        public bool CanReadRoot(string rootPath) => _inner.CanReadRoot(rootPath);

        public ScanResult<HashSet<string>> GetExtensions(string rootPath, IgnoreRules rules, CancellationToken cancellationToken = default)
        {
            MaybeBlock(rootPath, cancellationToken);
            return _inner.GetExtensions(rootPath, rules, EffectiveCancellationToken(cancellationToken));
        }

        public ScanResult<HashSet<string>> GetRootFileExtensions(string rootPath, IgnoreRules rules, CancellationToken cancellationToken = default)
        {
            MaybeBlock(rootPath, cancellationToken);
            return _inner.GetRootFileExtensions(rootPath, rules, EffectiveCancellationToken(cancellationToken));
        }

        public ScanResult<List<string>> GetRootFolderNames(string rootPath, IgnoreRules rules, CancellationToken cancellationToken = default)
        {
            MaybeBlock(rootPath, cancellationToken);
            return _inner.GetRootFolderNames(rootPath, rules, EffectiveCancellationToken(cancellationToken));
        }

        public ScanResult<ExtensionsScanData> GetExtensionsWithIgnoreOptionCounts(
            string rootPath,
            IgnoreRules rules,
            CancellationToken cancellationToken = default)
        {
            MaybeBlock(rootPath, cancellationToken);
            return _inner.GetExtensionsWithIgnoreOptionCounts(rootPath, rules, EffectiveCancellationToken(cancellationToken));
        }

        public ScanResult<ExtensionsScanData> GetRootFileExtensionsWithIgnoreOptionCounts(
            string rootPath,
            IgnoreRules rules,
            CancellationToken cancellationToken = default)
        {
            MaybeBlock(rootPath, cancellationToken);
            return _inner.GetRootFileExtensionsWithIgnoreOptionCounts(rootPath, rules, EffectiveCancellationToken(cancellationToken));
        }

        public ScanResult<int> GetEffectiveEmptyFolderCount(
            string rootPath,
            IReadOnlySet<string> allowedExtensions,
            IgnoreRules rules,
            CancellationToken cancellationToken = default)
        {
            MaybeBlock(rootPath, cancellationToken);
            return _inner.GetEffectiveEmptyFolderCount(rootPath, allowedExtensions, rules, EffectiveCancellationToken(cancellationToken));
        }

        public ScanResult<IgnoreOptionCounts> GetEffectiveIgnoreOptionCounts(
            string rootPath,
            IReadOnlySet<string> allowedExtensions,
            IgnoreRules rules,
            CancellationToken cancellationToken = default)
        {
            MaybeBlock(rootPath, cancellationToken);
            return _inner.GetEffectiveIgnoreOptionCounts(rootPath, allowedExtensions, rules, EffectiveCancellationToken(cancellationToken));
        }

        public ScanResult<IgnoreOptionCounts> GetEffectiveRootFileIgnoreOptionCounts(
            string rootPath,
            IReadOnlySet<string> allowedExtensions,
            IgnoreRules rules,
            CancellationToken cancellationToken = default)
        {
            MaybeBlock(rootPath, cancellationToken);
            return _inner.GetEffectiveRootFileIgnoreOptionCounts(rootPath, allowedExtensions, rules, EffectiveCancellationToken(cancellationToken));
        }

        public ScanResult<IgnoreSectionScanData> GetIgnoreSectionSnapshot(
            string rootPath,
            IgnoreRules extensionDiscoveryRules,
            IgnoreRules effectiveRules,
            IReadOnlySet<string>? effectiveAllowedExtensions,
            CancellationToken cancellationToken = default)
        {
            MaybeBlock(rootPath, cancellationToken);
            return _inner.GetIgnoreSectionSnapshot(
                rootPath,
                extensionDiscoveryRules,
                effectiveRules,
                effectiveAllowedExtensions,
                EffectiveCancellationToken(cancellationToken));
        }

        public ScanResult<IgnoreSectionScanData> GetRootFileIgnoreSectionSnapshot(
            string rootPath,
            IgnoreRules extensionDiscoveryRules,
            IgnoreRules effectiveRules,
            IReadOnlySet<string>? effectiveAllowedExtensions,
            CancellationToken cancellationToken = default)
        {
            MaybeBlock(rootPath, cancellationToken);
            return _inner.GetRootFileIgnoreSectionSnapshot(
                rootPath,
                extensionDiscoveryRules,
                effectiveRules,
                effectiveAllowedExtensions,
                EffectiveCancellationToken(cancellationToken));
        }

        public ScanResult<IgnoreSectionScanData> GetIgnoreSectionSnapshot(
            string rootPath,
            IgnoreRules extensionDiscoveryRules,
            IgnoreRules effectiveRules,
            IExtensionInclusionPolicy? effectiveExtensionPolicy,
            CancellationToken cancellationToken = default)
        {
            MaybeBlock(rootPath, cancellationToken);
            return _inner.GetIgnoreSectionSnapshot(
                rootPath,
                extensionDiscoveryRules,
                effectiveRules,
                effectiveExtensionPolicy,
                EffectiveCancellationToken(cancellationToken));
        }

        public ScanResult<IgnoreSectionScanData> GetRootFileIgnoreSectionSnapshot(
            string rootPath,
            IgnoreRules extensionDiscoveryRules,
            IgnoreRules effectiveRules,
            IExtensionInclusionPolicy? effectiveExtensionPolicy,
            CancellationToken cancellationToken = default)
        {
            MaybeBlock(rootPath, cancellationToken);
            return _inner.GetRootFileIgnoreSectionSnapshot(
                rootPath,
                extensionDiscoveryRules,
                effectiveRules,
                effectiveExtensionPolicy,
                EffectiveCancellationToken(cancellationToken));
        }

        public void Dispose()
        {
            _blocked.Dispose();
            _release.Dispose();
        }

        private void MaybeBlock(string rootPath, CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref _enabled) == 0 || !IsInsideBlockedRoot(rootPath))
                return;

            _blocked.Set();
            if (_ignoreCancellation)
            {
                if (!_release.Wait(TimeSpan.FromSeconds(30)))
                    throw new TimeoutException("Timed out waiting to release the controlled stale refresh.");
                return;
            }

            var signaled = WaitHandle.WaitAny(
                [_release.WaitHandle, cancellationToken.WaitHandle],
                TimeSpan.FromSeconds(30));
            if (signaled == WaitHandle.WaitTimeout)
                throw new TimeoutException("Timed out waiting to release the controlled stale refresh.");

            cancellationToken.ThrowIfCancellationRequested();
        }

        private CancellationToken EffectiveCancellationToken(CancellationToken cancellationToken) =>
            _ignoreCancellation ? CancellationToken.None : cancellationToken;

        private bool IsInsideBlockedRoot(string path)
        {
            var fullPath = Path.GetFullPath(path);
            var rootPath = Path.GetFullPath(blockedRootPath).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);

            if (string.Equals(fullPath, rootPath, PathComparison))
                return true;
            if (!fullPath.StartsWith(rootPath, PathComparison))
                return false;

            var next = fullPath[rootPath.Length];
            return next == Path.DirectorySeparatorChar || next == Path.AltDirectorySeparatorChar;
        }

        private static StringComparison PathComparison => OperatingSystem.IsLinux()
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;
    }
}
