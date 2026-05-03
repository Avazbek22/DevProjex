namespace DevProjex.Tests.UI;

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
                visible: false);

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

    private static async Task ApplySettingsAndWaitForIgnoreRefreshAsync(MainWindow window)
    {
        await UiTestDriver.ClickApplySettingsAsync(window);
        await UiTestDriver.WaitForSelectionRefreshIdleAsync(window);
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
}
