namespace DevProjex.Tests.UI;

[Collection(UiWorkspaceCollection.Name)]
public sealed class MainWindowKeyboardAndSettingsUiTests(UiWorkspaceFixture workspace)
{
    [AvaloniaFact]
    public async Task CtrlB_TogglesPreviewWorkspace()
    {
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);

        try
        {
            await UiTestDriver.PressKeyAsync(window, Key.B, RawInputModifiers.Control);
            await UiTestDriver.WaitForPreviewReadyAsync(window);
            Assert.True(UiTestDriver.GetViewModel(window).IsPreviewMode);

            await UiTestDriver.PressKeyAsync(window, Key.B, RawInputModifiers.Control);
            await UiTestDriver.WaitForPreviewClosedAsync(window);
            Assert.False(UiTestDriver.GetViewModel(window).IsPreviewMode);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task CtrlP_TogglesSettingsWhilePreviewStaysVisible()
    {
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);

        try
        {
            await UiTestDriver.OpenPreviewAsync(window);

            await UiTestDriver.PressKeyAsync(window, Key.P, RawInputModifiers.Control);
            await UiTestDriver.WaitForSettingsVisibilityAsync(window, visible: false);

            var viewModel = UiTestDriver.GetViewModel(window);
            Assert.True(viewModel.IsPreviewMode);
            Assert.False(viewModel.SettingsVisible);
            Assert.True(UiTestDriver.GetRequiredControl<Border>(window, "PreviewIsland").IsVisible);

            await UiTestDriver.PressKeyAsync(window, Key.P, RawInputModifiers.Control);
            await UiTestDriver.WaitForSettingsVisibilityAsync(window, visible: true);

            Assert.True(UiTestDriver.GetViewModel(window).SettingsVisible);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task InitialSettingsReveal_AfterProjectLoad_DoesNotStartFromCollapsedTreeWidth()
    {
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(
            workspace.Project,
            waitForInitialSettingsPane: false);

        try
        {
            var treePaneContainer = UiTestDriver.GetRequiredControl<Border>(window, "TreePaneContainer");
            var settingsContainer = UiTestDriver.GetRequiredControl<Border>(window, "SettingsContainer");

            await UiTestDriver.WaitForConditionAsync(
                window,
                () => UiTestDriver.GetActualWidth(settingsContainer) > 0.5,
                "initial settings animation to begin");

            var minimumObservedTreeWidth = double.PositiveInfinity;
            for (var frame = 0; frame < 18; frame++)
            {
                minimumObservedTreeWidth = Math.Min(
                    minimumObservedTreeWidth,
                    UiTestDriver.GetActualWidth(treePaneContainer));
                await UiTestDriver.WaitForSettledFramesAsync(frameCount: 1);
            }

            await UiTestDriver.WaitForConditionAsync(
                window,
                () => UiTestDriver.GetActualWidth(settingsContainer) >= 200,
                "initial settings pane to become visually available");

            var finalTreeWidth = UiTestDriver.GetActualWidth(treePaneContainer);
            Assert.True(
                minimumObservedTreeWidth >= finalTreeWidth - 2.0,
                $"Initial settings reveal started from an undersized tree pane. Minimum observed tree width {minimumObservedTreeWidth:F2}, final tree width {finalTreeWidth:F2}.");
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task SettingsOpen_InTreeMode_KeepsTreePaneAnchoredToLeftEdge()
    {
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);

        try
        {
            var treePaneContainer = UiTestDriver.GetRequiredControl<Border>(window, "TreePaneContainer");

            await UiTestDriver.PressKeyAsync(window, Key.P, RawInputModifiers.Control);
            await UiTestDriver.WaitForSettingsVisibilityAsync(window, visible: false);

            var anchoredLeft = UiTestDriver.GetBoundsInWindow(treePaneContainer, window).Left;

            await UiTestDriver.PressKeyAsync(window, Key.P, RawInputModifiers.Control);

            var minimumObservedLeft = double.PositiveInfinity;
            for (var frame = 0; frame < 18; frame++)
            {
                minimumObservedLeft = Math.Min(
                    minimumObservedLeft,
                    UiTestDriver.GetBoundsInWindow(treePaneContainer, window).Left);
                await UiTestDriver.WaitForSettledFramesAsync(frameCount: 1);
            }

            await UiTestDriver.WaitForSettingsVisibilityAsync(window, visible: true);

            Assert.True(
                minimumObservedLeft >= anchoredLeft - 0.75,
                $"Tree pane shifted left during settings open. Expected left >= {anchoredLeft - 0.75:F2}, actual minimum {minimumObservedLeft:F2}.");
            Assert.True(double.IsNaN(treePaneContainer.Width));
            Assert.Equal(global::Avalonia.Layout.HorizontalAlignment.Stretch, treePaneContainer.HorizontalAlignment);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task PreviewOpen_WhenSettingsAreHidden_DoesNotReopenSettings()
    {
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);

        try
        {
            await UiTestDriver.PressKeyAsync(window, Key.P, RawInputModifiers.Control);
            await UiTestDriver.WaitForSettingsVisibilityAsync(window, visible: false);

            await UiTestDriver.OpenPreviewAsync(window);

            var viewModel = UiTestDriver.GetViewModel(window);
            Assert.True(viewModel.IsPreviewMode);
            Assert.False(viewModel.SettingsVisible);
            Assert.False(UiTestDriver.IsActuallyVisibleHorizontally(
                UiTestDriver.GetRequiredControl<Border>(window, "SettingsContainer")));
            Assert.True(UiTestDriver.GetRequiredControl<Border>(window, "PreviewIsland").IsVisible);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task PreviewClose_PreservesCollapsedSettingsState()
    {
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);

        try
        {
            await UiTestDriver.PressKeyAsync(window, Key.P, RawInputModifiers.Control);
            await UiTestDriver.WaitForSettingsVisibilityAsync(window, visible: false);

            await UiTestDriver.OpenPreviewAsync(window);
            await UiTestDriver.ClosePreviewAsync(window);

            var viewModel = UiTestDriver.GetViewModel(window);
            Assert.False(viewModel.IsPreviewMode);
            Assert.False(viewModel.SettingsVisible);
            Assert.False(UiTestDriver.IsActuallyVisibleHorizontally(
                UiTestDriver.GetRequiredControl<Border>(window, "SettingsContainer")));
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task PreviewOnlyClose_PreservesCollapsedSettingsState()
    {
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);

        try
        {
            await UiTestDriver.PressKeyAsync(window, Key.P, RawInputModifiers.Control);
            await UiTestDriver.WaitForSettingsVisibilityAsync(window, visible: false);

            await UiTestDriver.OpenPreviewAsync(window);
            await UiTestDriver.HidePreviewTreeAsync(window);
            await UiTestDriver.ClosePreviewAsync(window);

            var viewModel = UiTestDriver.GetViewModel(window);
            Assert.False(viewModel.IsPreviewMode);
            Assert.False(viewModel.SettingsVisible);
            Assert.False(UiTestDriver.IsActuallyVisibleHorizontally(
                UiTestDriver.GetRequiredControl<Border>(window, "SettingsContainer")));
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task CtrlShiftN_OpensFilterHotkeyPath()
    {
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);

        try
        {
            await UiTestDriver.PressKeyAsync(window, Key.N, RawInputModifiers.Control | RawInputModifiers.Shift);
            await UiTestDriver.WaitForConditionAsync(
                window,
                () => UiTestDriver.GetViewModel(window).FilterVisible,
                "filter bar to open via Ctrl+Shift+N");

            Assert.True(UiTestDriver.GetRequiredControl<Border>(window, "FilterBarContainer").IsVisible);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }
}
