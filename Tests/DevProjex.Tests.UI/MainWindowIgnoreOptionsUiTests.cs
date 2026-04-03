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
}
