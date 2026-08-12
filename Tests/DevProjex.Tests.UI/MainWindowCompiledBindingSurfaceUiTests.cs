using System.Collections.ObjectModel;
using Avalonia.Controls.Presenters;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using DevProjex.Avalonia.Controls;
using DevProjex.Avalonia.Coordinators;

namespace DevProjex.Tests.UI;

[Collection(UiWorkspaceCollection.Name)]
public sealed class MainWindowCompiledBindingSurfaceUiTests(UiWorkspaceFixture workspace)
{
	[AvaloniaFact]
	public async Task LoadedWindow_PreservesThreeIslandMinimumWidthAcrossPreviewLayout()
	{
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);

		try
		{
			AssertWindowMinimumWidth(window);
			await UiTestDriver.OpenPreviewAsync(window);
			AssertWindowMinimumWidth(window);
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	[AvaloniaFact]
	public async Task CompactSettingsAllCheckBox_KeepsLabelAtNativeRasterScale()
	{
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);

		try
		{
			window.Classes.Add("compact-mode");
			await UiTestDriver.WaitForSettledFramesAsync(frameCount: 2);

			var checkBox = UiTestDriver.GetRequiredControl<CheckBox>(window, "IgnoreAllCheckBox");
			var contentPresenter = Assert.Single(
				checkBox.GetVisualDescendants().OfType<ContentPresenter>(),
				presenter => presenter.Name == "PART_ContentPresenter");
			var box = Assert.Single(
				checkBox.GetVisualDescendants().OfType<Border>(),
				border => border.Name == "NormalRectangle");

			Assert.Null(checkBox.RenderTransform);
			Assert.Null(contentPresenter.RenderTransform);
			Assert.Equal(18, box.Bounds.Width, precision: 3);
			Assert.Equal(18, box.Bounds.Height, precision: 3);
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

    [AvaloniaFact]
    public async Task LoadedProject_BindsWindowMenuTreeStatusAndSettingsSurfaces()
    {
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);

        try
        {
            var viewModel = UiTestDriver.GetViewModel(window);
            var mainMenu = UiTestDriver.GetRequiredTopMenuControl<Menu>(window, "MainMenu");
            var projectTree = UiTestDriver.GetRequiredControl<ProjectTreeView>(window, "ProjectTree");
            var applyButton = UiTestDriver.GetRequiredControl<Button>(window, "ApplySettingsButton");
            var ignoreAllCheckBox = UiTestDriver.GetRequiredControl<CheckBox>(window, "IgnoreAllCheckBox");
            var treeFontMenu = UiTestDriver.GetRequiredTopMenuControl<MenuItem>(window, "TreeFontMenuItem");
            var themeMenu = UiTestDriver.GetRequiredTopMenuControl<MenuItem>(window, "ThemeMenuItem");
            var helpMenu = UiTestDriver.GetRequiredTopMenuControl<MenuItem>(window, "HelpMenuItem");
            var previewToggleButton = UiTestDriver.GetRequiredTopMenuControl<Button>(window, "PreviewToggleButton");
            var filterToggleButton = UiTestDriver.GetRequiredTopMenuControl<Button>(window, "FilterToggleButton");

            Assert.Equal(viewModel.Title, window.Title);
            Assert.Same(
                viewModel.TreeNodes,
                projectTree.ItemsSource);
            Assert.Equal(viewModel.SelectedFontFamily, projectTree.FontFamily);
            Assert.Equal(viewModel.TreeFontSize, projectTree.FontSize);

            Assert.Contains(mainMenu.Items.OfType<MenuItem>(), item => HeaderEquals(item, viewModel.MenuFile));
            Assert.Contains(mainMenu.Items.OfType<MenuItem>(), item => HeaderEquals(item, viewModel.MenuCopy));
            Assert.Contains(mainMenu.Items.OfType<MenuItem>(), item => HeaderEquals(item, viewModel.MenuView));
            Assert.Equal(viewModel.MenuViewTreeFont, treeFontMenu.Header);
            Assert.Equal(viewModel.MenuTheme, themeMenu.Header);
            Assert.Equal(viewModel.MenuHelp, helpMenu.Header);
            Assert.Equal(viewModel.IsProjectLoaded, previewToggleButton.IsVisible);
            Assert.Equal(viewModel.IsProjectLoaded, filterToggleButton.IsEnabled);

            Assert.Equal(viewModel.SettingsApply, applyButton.Content);
            Assert.Equal(viewModel.SettingsAllIgnore, ignoreAllCheckBox.Content);
            AssertVisibleText(window, viewModel.StatusTreeLabel);
            AssertVisibleText(window, viewModel.StatusContentLabel);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task ApplySettingsButton_DisablesWhileStatusOperationIsActiveAndRecoversAfterCompletion()
    {
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);

        try
        {
            var viewModel = UiTestDriver.GetViewModel(window);
            var applyButton = UiTestDriver.GetRequiredControl<Button>(window, "ApplySettingsButton");
            await UiTestDriver.WaitForInitialMetricsBaselineAsync(window);
            Assert.True(applyButton.IsEnabled);

            viewModel.StatusBusy = true;
            await UiTestDriver.WaitForConditionAsync(
                window,
                () => !applyButton.IsEnabled,
                "Apply settings button to disable while a status operation is active");

            viewModel.StatusBusy = false;
            await UiTestDriver.WaitForConditionAsync(
                window,
                () => applyButton.IsEnabled,
                "Apply settings button to recover after the status operation completes");
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task ApplySettingsButton_AttentionClassTracksPendingStateAndDetachesWhenClean()
    {
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);

        try
        {
            var viewModel = UiTestDriver.GetViewModel(window);
            var applyButton = UiTestDriver.GetRequiredControl<Button>(window, "ApplySettingsButton");
            Assert.True(applyButton.IsEnabled);
            Assert.DoesNotContain("apply-attention", applyButton.Classes);

            viewModel.SetPendingFilterSettingsChanges(true);
            await UiTestDriver.WaitForConditionAsync(
                window,
                () => applyButton.IsEnabled && applyButton.Classes.Contains("apply-attention"),
                "Apply settings attention class to attach for pending filter changes");

            viewModel.SetPendingFilterSettingsChanges(false);
            await UiTestDriver.WaitForConditionAsync(
                window,
                () => applyButton.IsEnabled &&
                      !applyButton.Classes.Contains("apply-attention") &&
                      Math.Abs(applyButton.Opacity - 1) < 0.01,
                "Apply settings attention class and animated opacity to reset when selections are clean");
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task ApplySettingsButton_BackendGateRejectsCleanAndRapidDuplicateRequestsWithoutDisablingUi()
    {
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);

        try
        {
            var viewModel = UiTestDriver.GetViewModel(window);
            var applyButton = UiTestDriver.GetRequiredControl<Button>(window, "ApplySettingsButton");
            var previousApplyTask = window.LatestApplySettingsTask;

            Assert.True(applyButton.IsEnabled);
            await UiTestDriver.RaiseButtonClickAsync(applyButton);
            Assert.Same(previousApplyTask, window.LatestApplySettingsTask);

            viewModel.SetPendingFilterSettingsChanges(true);
            Task? firstApplyTask = null;
            await window.Dispatcher.InvokeAsync(() =>
            {
                applyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                firstApplyTask = window.LatestApplySettingsTask;

                Assert.NotSame(previousApplyTask, firstApplyTask);
                Assert.True(applyButton.IsEnabled);

                applyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Same(firstApplyTask, window.LatestApplySettingsTask);
            });

            Assert.NotNull(firstApplyTask);
            await firstApplyTask.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task PreviewAndToastTemplates_BindTypedViewModels()
    {
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);

        try
        {
            await UiTestDriver.OpenPreviewAsync(window);

            var viewModel = UiTestDriver.GetViewModel(window);
            var lineNumbers = UiTestDriver.GetRequiredControl<VirtualizedLineNumbersControl>(window, "PreviewLineNumbersControl");
            var previewText = UiTestDriver.GetRequiredControl<VirtualizedPreviewTextControl>(window, "PreviewTextControl");

            Assert.Equal(viewModel.PreviewLineCount, lineNumbers.LineCount);
            Assert.Equal(viewModel.SelectedFontFamily, lineNumbers.NumberFontFamily);
            Assert.Equal(viewModel.PreviewFontSize, lineNumbers.NumberFontSize);
            Assert.Same(viewModel.PreviewDocument, previewText.Document);
            Assert.Equal(viewModel.PreviewSelectionCopy, previewText.CopyMenuHeader);
            Assert.Equal(viewModel.PreviewSelectionSelectAll, previewText.SelectAllMenuHeader);
            Assert.Equal(viewModel.SelectedFontFamily, previewText.TextFontFamily);
            Assert.Equal(viewModel.PreviewFontSize, previewText.TextFontSize);

            var toast = new ToastMessageViewModel("Compiled binding smoke toast")
            {
                Opacity = 0.42,
                OffsetY = 7
            };
            var toastItems = new ObservableCollection<ToastMessageViewModel> { toast };

            viewModel.SetToastItems(toastItems);
            await WaitForToastBindingAsync(window, toast);

            var toastHost = UiTestDriver.GetRequiredControl<ItemsControl>(window, "ToastHost");
            var toastBorder = GetRequiredToastBorder(window, toast);
            var toastText = GetRequiredToastText(window, toast);
            var transform = Assert.IsType<TranslateTransform>(toastBorder.RenderTransform);

            Assert.Same(toastItems, toastHost.ItemsSource);
            Assert.Equal(toast.Message, toastText.Text);
            AssertClose(toast.Opacity, toastBorder.Opacity);
            AssertClose(toast.OffsetY, transform.Y);

            toast.Message = "Updated compiled binding smoke toast";
            toast.Opacity = 0.84;
            toast.OffsetY = 2;

            await WaitForToastBindingAsync(window, toast);

            Assert.Equal(toast.Message, toastText.Text);
            AssertClose(toast.Opacity, toastBorder.Opacity);
            AssertClose(toast.OffsetY, transform.Y);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    private static bool HeaderEquals(MenuItem menuItem, string expected)
        => string.Equals(menuItem.Header?.ToString(), expected, StringComparison.Ordinal);

	private static void AssertWindowMinimumWidth(MainWindow window)
	{
		var alignedMinimum = WorkspacePresentationController.AlignWindowConstraintToPhysicalPixels(
			WorkspacePresentationController.MinimumWindowWidth,
			window.RenderScaling);

		Assert.Equal(alignedMinimum, window.MinWidth, precision: 6);
	}

    private static void AssertVisibleText(MainWindow window, string expected)
    {
        Assert.Contains(
            window.GetVisualDescendants().OfType<TextBlock>(),
            textBlock => textBlock.IsVisible &&
                         string.Equals(textBlock.Text, expected, StringComparison.Ordinal));
    }

    private static async Task WaitForToastBindingAsync(MainWindow window, ToastMessageViewModel toast)
    {
        await UiTestDriver.WaitForConditionAsync(
            window,
            () =>
            {
                var text = window
                    .GetVisualDescendants()
                    .OfType<TextBlock>()
                    .FirstOrDefault(candidate => ReferenceEquals(candidate.DataContext, toast));
                var border = window
                    .GetVisualDescendants()
                    .OfType<Border>()
                    .FirstOrDefault(candidate => ReferenceEquals(candidate.DataContext, toast));
                var transform = border?.RenderTransform as TranslateTransform;

                return text is not null &&
                       border is not null &&
                       transform is not null &&
                       string.Equals(text.Text, toast.Message, StringComparison.Ordinal) &&
                       IsClose(border.Opacity, toast.Opacity) &&
                       IsClose(transform.Y, toast.OffsetY);
            },
            "toast template bindings to reflect the toast view model");
    }

    private static Border GetRequiredToastBorder(MainWindow window, ToastMessageViewModel toast)
        => Assert.IsType<Border>(window
            .GetVisualDescendants()
            .OfType<Border>()
            .FirstOrDefault(candidate => ReferenceEquals(candidate.DataContext, toast)));

    private static TextBlock GetRequiredToastText(MainWindow window, ToastMessageViewModel toast)
        => Assert.IsType<TextBlock>(window
            .GetVisualDescendants()
            .OfType<TextBlock>()
            .FirstOrDefault(candidate => ReferenceEquals(candidate.DataContext, toast)));

    private static void AssertClose(double expected, double actual)
        => Assert.True(
            IsClose(actual, expected),
            $"Expected {expected:F3}, actual {actual:F3}.");

    private static bool IsClose(double actual, double expected)
        => Math.Abs(actual - expected) <= 0.01;
}
