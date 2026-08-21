using System.Reflection;
using DevProjex.Avalonia.Controls;
using DevProjex.Avalonia.Services;

namespace DevProjex.Tests.UI;

[Collection(UiWorkspaceCollection.Name)]
public sealed class MainWindowKeyboardAndSettingsUiTests(UiWorkspaceFixture workspace)
{
	[AvaloniaFact]
	public async Task NativePrimaryModifier_ControlsPreviewSearchSettingsZoomAndPreviewClipboard()
	{
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);
		var primary = ResolvePrimaryRawModifier();

		try
		{
			await UiTestDriver.PressKeyAsync(window, Key.F, primary);
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => UiTestDriver.GetViewModel(window).SearchVisible,
				"tree search to open through the native primary modifier");
			await UiTestDriver.PressKeyAsync(window, Key.Escape);

			await UiTestDriver.PressKeyAsync(window, Key.B, primary);
			await UiTestDriver.WaitForPreviewReadyAsync(window);
			await UiTestDriver.PressKeyAsync(window, Key.P, primary);
			await UiTestDriver.WaitForSettingsVisibilityAsync(window, visible: false);
			await UiTestDriver.PressKeyAsync(window, Key.P, primary);
			await UiTestDriver.WaitForSettingsVisibilityAsync(window, visible: true);

			await UiTestDriver.SwitchPreviewModeAsync(window, PreviewContentMode.Content);
			await UiTestDriver.PressKeyAsync(window, Key.F, primary | RawInputModifiers.Shift);
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => UiTestDriver.GetViewModel(window).PreviewSearchVisible,
				"preview search to open through the native primary modifier");
			await UiTestDriver.PressKeyAsync(window, Key.Escape);

			var viewModel = UiTestDriver.GetViewModel(window);
			var initialTreeFontSize = viewModel.TreeFontSize;
			await UiTestDriver.PressKeyAsync(window, Key.OemPlus, primary);
			Assert.True(viewModel.TreeFontSize > initialTreeFontSize);
			await UiTestDriver.PressKeyAsync(window, Key.D0, primary);
			Assert.Equal(MainWindowViewModel.DefaultTreeFontSize, viewModel.TreeFontSize);

			var preview = UiTestDriver.GetRequiredControl<VirtualizedPreviewTextControl>(
				window,
				"PreviewTextControl");
			preview.Focus();
			await UiTestDriver.WaitForSettledFramesAsync(frameCount: 2);
			await UiTestDriver.PressKeyAsync(window, Key.A, primary);
			var selectedText = preview.GetSelectedText();
			Assert.False(string.IsNullOrEmpty(selectedText));
			var expectedClipboard = UiTestDriver.ComputeCurrentPreviewCopyPayload(window);

			await UiTestDriver.SetClipboardTextAsync(window, $"primary-copy-{Guid.NewGuid():N}");
			await UiTestDriver.PressKeyAsync(window, Key.C, primary);
			await UiTestDriver.WaitForClipboardTextAsync(window, expectedClipboard);

			await UiTestDriver.PressKeyAsync(window, Key.O, primary);
			Assert.True(window.IsVisible);
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	[AvaloniaFact]
	public async Task MacOS_CollapseAllUsesCommandShiftE_CommandWIsUnbound_AndControlBAliasWorks()
	{
		if (!OperatingSystem.IsMacOS())
			return;

		var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);
		try
		{
			await UiTestDriver.PressKeyAsync(window, Key.B, RawInputModifiers.Control);
			await UiTestDriver.WaitForPreviewReadyAsync(window);
			await UiTestDriver.PressKeyAsync(window, Key.B, RawInputModifiers.Control);
			await UiTestDriver.WaitForPreviewClosedAsync(window);

			await UiTestDriver.PressKeyAsync(window, Key.E, RawInputModifiers.Meta);
			var expandableNode = FindExpandableDescendant(
				Assert.Single(UiTestDriver.GetViewModel(window).TreeNodes));
			Assert.True(expandableNode.IsExpanded);

			await UiTestDriver.PressKeyAsync(window, Key.W, RawInputModifiers.Meta);
			Assert.True(expandableNode.IsExpanded);

			await UiTestDriver.PressKeyAsync(
				window,
				Key.E,
				RawInputModifiers.Meta | RawInputModifiers.Shift);
			Assert.False(expandableNode.IsExpanded);
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	private static RawInputModifiers ResolvePrimaryRawModifier() =>
		DesktopShortcutModifiers.Current.PrimaryModifier == KeyModifiers.Meta
			? RawInputModifiers.Meta
			: RawInputModifiers.Control;

	private static TreeNodeViewModel FindExpandableDescendant(TreeNodeViewModel node)
	{
		foreach (var child in node.Children)
		{
			if (child.Children.Count > 0)
				return child;

			var descendant = FindExpandableDescendantOrDefault(child);
			if (descendant is not null)
				return descendant;
		}

		throw new InvalidOperationException("The UI test project has no expandable descendant.");
	}

	private static TreeNodeViewModel? FindExpandableDescendantOrDefault(TreeNodeViewModel node)
	{
		if (node.Children.Count > 0)
			return node;

		foreach (var child in node.Children)
		{
			var descendant = FindExpandableDescendantOrDefault(child);
			if (descendant is not null)
				return descendant;
		}

		return null;
	}

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

            var finalizationField = typeof(MainWindow).GetField(
                "_projectLoadFinalizationTask",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var projectLoadFinalization = Assert.IsAssignableFrom<Task>(
                finalizationField?.GetValue(window));
            Assert.True(
                projectLoadFinalization.IsCompletedSuccessfully,
                "Initial settings reveal started before project-load finalization completed.");

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
