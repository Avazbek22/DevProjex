using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using DevProjex.Avalonia.Controls;

namespace DevProjex.Tests.UI;

[Collection(UiWorkspaceCollection.Name)]
public sealed class MainWindowHelpSearchUiTests
{
    [AvaloniaFact]
    public async Task SearchButton_ExpandsLeftWithTransitionsAndFadesTheMagnifier()
    {
        using var project = UiTestProject.CreateDefault();
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

        try
        {
            var help = await OpenHelpAsync(window);
            var searchButton = GetRequiredControl<Button>(help, "HelpSearchButton");
            var searchPanel = GetRequiredControl<Border>(help, "HelpSearchPanel");
            var searchContent = GetRequiredControl<Grid>(help, "HelpSearchContent");
            var previousButton = GetRequiredControl<Button>(help, "HelpSearchPreviousButton");
            var nextButton = GetRequiredControl<Button>(help, "HelpSearchNextButton");
            var closeSearchButton = GetRequiredControl<Button>(help, "HelpSearchCloseButton");
            var closeHelpButton = GetRequiredControl<Button>(help, "HelpCloseButton");

            Assert.False(help.IsSearchOpen);
            Assert.Equal(0, searchPanel.Width);
            Assert.Equal(1, searchButton.Opacity);
            AssertNavigationButtonPresentation(previousButton);
            AssertNavigationButtonPresentation(nextButton);
            AssertNavigationButtonPresentation(closeSearchButton);
            AssertNavigationButtonPresentation(closeHelpButton);
            AssertTooltipOpensBelowAndInward(previousButton);
            AssertTooltipOpensBelowAndInward(nextButton);
            AssertTooltipOpensBelowAndInward(closeSearchButton);
            AssertTooltipOpensBelowAndInward(searchButton);
            Assert.Equal(UiTestDriver.GetViewModel(window).HelpHelpCloseSearch, ToolTip.GetTip(closeSearchButton));
            Assert.NotNull(searchPanel.Transitions);
            Assert.Equal(3, searchPanel.Transitions.Count);
            Assert.NotNull(searchButton.Transitions);
            Assert.Equal(3, searchButton.Transitions.Count);
            var contentTransform = Assert.IsType<TranslateTransform>(searchContent.RenderTransform);
            Assert.NotNull(contentTransform.Transitions);
            Assert.Single(contentTransform.Transitions);

            await UiTestDriver.RaiseButtonClickAsync(searchButton);
            await UiTestDriver.WaitForConditionAsync(
                window,
                () => help.IsSearchOpen &&
                      Math.Abs(searchPanel.Width - 390) < 0.1 &&
                      help.SearchBoxControl.IsFocused,
                "help search expansion and focus");

            Assert.True(searchPanel.IsHitTestVisible);
            Assert.Equal(1, searchPanel.Opacity);
            Assert.Equal(0, contentTransform.X);
            Assert.Equal(0, searchButton.Width);
            Assert.Equal(0, searchButton.Opacity);
            Assert.False(searchButton.IsHitTestVisible);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task Search_DebouncesTwoCharacterQueriesNavigatesWithWrapAroundAndClosesCleanly()
    {
        using var project = UiTestProject.CreateDefault();
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

        try
        {
            var help = await OpenHelpAsync(window);
            var searchButton = GetRequiredControl<Button>(help, "HelpSearchButton");
            await UiTestDriver.RaiseButtonClickAsync(searchButton);
            await UiTestDriver.WaitForConditionAsync(
                window,
                () => help.IsSearchOpen && help.SearchBoxControl.IsFocused,
                "help search input focus");

            var previousButton = GetRequiredControl<Button>(help, "HelpSearchPreviousButton");
            var nextButton = GetRequiredControl<Button>(help, "HelpSearchNextButton");
            var matchSummary = GetRequiredControl<TextBlock>(help, "HelpSearchMatchSummary");
            var markerBar = GetRequiredControl<PreviewMarkerBar>(help, "HelpSearchMarkerBar");
            var application = Assert.IsType<App>(global::Avalonia.Application.Current);
            var theme = application.ActualThemeVariant ?? ThemeVariant.Light;
            Assert.True(application.TryFindResource("TreeSearchHighlightBrush", theme, out var searchBrush));
            help.SearchBoxControl.Text = "p";
            await Task.Delay(50);
            await UiTestDriver.WaitForSettledFramesAsync(frameCount: 4);
            Assert.Equal(0, help.SearchMatchCount);
            Assert.False(matchSummary.IsVisible);
            Assert.Empty(GetHighlightedRuns(help));
            Assert.Equal(0, help.SearchMarkerCount);
            Assert.Empty(markerBar.MarkerTicks);

            help.SearchBoxControl.Text = "no-such-help-search-result-4f22";
            await UiTestDriver.WaitForConditionAsync(
                window,
                () => help.SearchMatchCount == 0 && matchSummary.IsVisible,
                "empty help search result");
            Assert.True(previousButton.IsEnabled);
            Assert.True(nextButton.IsEnabled);
            Assert.Equal(1, previousButton.Opacity);
            Assert.Equal(1, nextButton.Opacity);

            help.SearchBoxControl.Text = "project";
            Assert.Equal(0, help.SearchMatchCount);
            Assert.Empty(GetHighlightedRuns(help));
            await UiTestDriver.WaitForConditionAsync(
                window,
                () => help.SearchMatchCount > 3 &&
                      help.CurrentSearchMatchIndex == 1 &&
                      help.SearchMarkerCount > 0 &&
                      markerBar.MarkerTicks.Count > 0 &&
                      markerBar.IsVisible &&
                      ReferenceEquals(markerBar.SearchBrush, searchBrush),
                "help search matches");

            var initialMatchCount = help.SearchMatchCount;
            Assert.Equal($"(1 / {initialMatchCount:N0})", matchSummary.Text);
            AssertHighlightBrushes(help, initialMatchCount);
            Assert.All(
                markerBar.MarkerTicks,
                static tick => Assert.Equal(PreviewMarkerCategory.Search, tick.Target.Category));
            Assert.True(markerBar.IsVisible);
            AssertSearchMarkerBrush(markerBar);

            var popupRoot = Assert.IsAssignableFrom<TopLevel>(TopLevel.GetTopLevel(help.SearchBoxControl));
            await UiTestDriver.RaiseButtonClickAsync(nextButton);
            Assert.Equal(2, help.CurrentSearchMatchIndex);
            AssertHighlightBrushes(help, initialMatchCount);

            await UiTestDriver.RaiseButtonClickAsync(previousButton);
            Assert.Equal(1, help.CurrentSearchMatchIndex);
            await UiTestDriver.PressKeyAsync(popupRoot, Key.F3);
            Assert.Equal(2, help.CurrentSearchMatchIndex);
            await UiTestDriver.PressKeyAsync(popupRoot, Key.F3, RawInputModifiers.Shift);
            Assert.Equal(1, help.CurrentSearchMatchIndex);
            await UiTestDriver.PressKeyAsync(popupRoot, Key.Enter, RawInputModifiers.Shift);
            Assert.Equal(initialMatchCount, help.CurrentSearchMatchIndex);
            await UiTestDriver.PressKeyAsync(popupRoot, Key.Enter);
            Assert.Equal(1, help.CurrentSearchMatchIndex);

            var bodyPanel = GetRequiredControl<StackPanel>(help, "BodyPanel");
            var bodyScrollViewer = GetRequiredControl<ScrollViewer>(help, "HelpBodyScrollViewer");
            var verticalScrollBar = Assert.Single(
                bodyScrollViewer.GetVisualDescendants().OfType<ScrollBar>(),
                static scrollBar => scrollBar.Orientation == Orientation.Vertical);
            var targetTick = markerBar.MarkerTicks
                .Where(static tick => tick.Target.Category == PreviewMarkerCategory.Search)
                .OrderByDescending(static tick => tick.Y)
                .First();
            var expectedMarkerMatch = help.ResolveSearchMarkerMatchIndex(targetTick.Target);
            Assert.InRange(expectedMarkerMatch, 2, initialMatchCount);
            var markerPoint = Assert.IsType<Point>(markerBar.TranslatePoint(
                new Point(markerBar.Bounds.Width - 1, targetTick.Y),
                popupRoot));
            var originalAllowAutoHide = verticalScrollBar.AllowAutoHide;

            popupRoot.MouseMove(markerPoint, RawInputModifiers.None);
            await UiTestDriver.WaitForSettledFramesAsync(frameCount: 3);
            var markerHit = Assert.IsAssignableFrom<InputElement>(popupRoot.InputHitTest(markerPoint));
            Assert.Equal("Hand", markerHit.Cursor?.ToString());

            popupRoot.MouseDown(
                markerPoint,
                MouseButton.Left,
                RawInputModifiers.LeftMouseButton);
            popupRoot.MouseUp(markerPoint, MouseButton.Left, RawInputModifiers.None);
            await UiTestDriver.WaitForConditionAsync(
                window,
                () => help.CurrentSearchMatchIndex == expectedMarkerMatch,
                "help search marker click to activate its exact match");
            var currentMatchControl = Assert.IsType<TextBlock>(help.CurrentSearchMatchControl);
            var currentMatchOrigin = Assert.IsType<Point>(
                currentMatchControl.TranslatePoint(default, bodyPanel));
            var expectedOffset = Math.Clamp(
                currentMatchOrigin.Y + (currentMatchControl.Bounds.Height / 2) -
                (bodyScrollViewer.Viewport.Height / 2),
                0,
                Math.Max(0, bodyScrollViewer.Extent.Height - bodyScrollViewer.Viewport.Height));
            Assert.InRange(Math.Abs(bodyScrollViewer.Offset.Y - expectedOffset), 0, 1);

            var offsetBeforeDrag = bodyScrollViewer.Offset.Y;
            var dragDelta = offsetBeforeDrag > 20 ? -36 : 36;
            var dragTarget = new Point(markerPoint.X, markerPoint.Y + dragDelta);
            popupRoot.MouseMove(markerPoint, RawInputModifiers.None);
            popupRoot.MouseDown(
                markerPoint,
                MouseButton.Left,
                RawInputModifiers.LeftMouseButton);
            popupRoot.MouseMove(dragTarget, RawInputModifiers.LeftMouseButton);
            Assert.Equal("Arrow", bodyScrollViewer.Cursor?.ToString());
            Assert.False(verticalScrollBar.AllowAutoHide);
            popupRoot.MouseUp(dragTarget, MouseButton.Left, RawInputModifiers.None);
            await UiTestDriver.WaitForConditionAsync(
                window,
                () => dragDelta < 0
                    ? bodyScrollViewer.Offset.Y < offsetBeforeDrag - 1
                    : bodyScrollViewer.Offset.Y > offsetBeforeDrag + 1,
                "help search marker drag to move the scrollbar continuously");

            var bodyPoint = Assert.IsType<Point>(bodyScrollViewer.TranslatePoint(
                new Point(12, bodyScrollViewer.Bounds.Height / 2),
                popupRoot));
            popupRoot.MouseMove(bodyPoint, RawInputModifiers.None);
            await UiTestDriver.WaitForConditionAsync(
                window,
                () => verticalScrollBar.AllowAutoHide == originalAllowAutoHide,
                "help search marker interaction to restore scrollbar auto-hide");

            await UiTestDriver.RaiseButtonClickAsync(GetRequiredControl<Button>(
                help,
                "HelpSearchCloseButton"));
            await UiTestDriver.WaitForConditionAsync(
                window,
                () => !help.IsSearchOpen &&
                      Math.Abs(GetRequiredControl<Border>(help, "HelpSearchPanel").Width) < 0.1,
                "help search to close");

            Assert.Equal(0, help.SearchMatchCount);
            Assert.Empty(GetHighlightedRuns(help));
            Assert.Equal(0, help.SearchMarkerCount);
            Assert.Empty(markerBar.MarkerTicks);
            Assert.False(markerBar.IsVisible);
            Assert.Equal(1, searchButton.Opacity);
            Assert.True(searchButton.IsHitTestVisible);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    private static async Task<HelpPopoverView> OpenHelpAsync(MainWindow window)
    {
        var viewModel = UiTestDriver.GetViewModel(window);
        var popup = UiTestDriver.GetRequiredTopMenuControl<Popup>(window, "HelpDocsPopup");
        var help = UiTestDriver.GetRequiredTopMenuControl<HelpPopoverView>(window, "HelpDocsPopover");

        viewModel.HelpDocsPopoverOpen = true;
        await UiTestDriver.WaitForConditionAsync(
            window,
            () => popup.IsOpen &&
                  TopLevel.GetTopLevel(help) is not null &&
                  GetRequiredControl<StackPanel>(help, "BodyPanel").Children.Count > 0,
            "help popup content");
        return help;
    }

    private static T GetRequiredControl<T>(Control root, string name)
        where T : Control
        => Assert.IsType<T>(root.FindControl<T>(name));

    private static void AssertNavigationButtonPresentation(Button button)
    {
        Assert.True(button.IsEnabled);
        Assert.Equal(1, button.Opacity);
        Assert.Equal(HorizontalAlignment.Center, button.HorizontalContentAlignment);
        Assert.Equal(VerticalAlignment.Center, button.VerticalContentAlignment);
        Assert.IsType<Viewbox>(button.Content);
    }

    private static void AssertTooltipOpensBelowAndInward(Button button)
    {
        Assert.Equal(PlacementMode.BottomEdgeAlignedRight, ToolTip.GetPlacement(button));
        Assert.Equal(5, ToolTip.GetVerticalOffset(button));
    }

    private static void AssertHighlightBrushes(HelpPopoverView help, int expectedMatchCount)
    {
        var application = Assert.IsType<App>(global::Avalonia.Application.Current);
        var theme = application.ActualThemeVariant ?? ThemeVariant.Light;
        Assert.True(application.TryFindResource("TreeSearchHighlightBrush", theme, out var highlight));
        Assert.True(application.TryFindResource("TreeSearchCurrentBrush", theme, out var current));
        Assert.True(application.TryFindResource("TreeSearchHighlightTextBrush", theme, out var text));

        var runs = GetHighlightedRuns(help);
        Assert.Equal(expectedMatchCount, runs.Count);
        Assert.Single(runs, run => ReferenceEquals(run.Background, current));
        Assert.Equal(expectedMatchCount - 1, runs.Count(run => ReferenceEquals(run.Background, highlight)));
        Assert.All(runs, run => Assert.Same(text, run.Foreground));
    }

    private static void AssertSearchMarkerBrush(PreviewMarkerBar markerBar)
    {
        var application = Assert.IsType<App>(global::Avalonia.Application.Current);
        var theme = application.ActualThemeVariant ?? ThemeVariant.Light;
        Assert.True(application.TryFindResource("TreeSearchHighlightBrush", theme, out var highlight));
        Assert.Same(highlight, markerBar.SearchBrush);
    }

    private static List<Run> GetHighlightedRuns(HelpPopoverView help)
        => help
            .GetVisualDescendants()
            .OfType<TextBlock>()
            .SelectMany(static textBlock => textBlock.Inlines?.OfType<Run>() ?? [])
            .Where(static run => run.Background is not null)
            .ToList();
}
