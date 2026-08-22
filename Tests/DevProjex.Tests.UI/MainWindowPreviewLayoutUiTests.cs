using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.VisualTree;
using DevProjex.Avalonia.Coordinators;
using DevProjex.Avalonia.Controls;

namespace DevProjex.Tests.UI;

[Collection(UiWorkspaceCollection.Name)]
public sealed class MainWindowPreviewLayoutUiTests(UiWorkspaceFixture workspace)
{
    [AvaloniaFact]
    public async Task ProjectTreeVerticalScrollBar_SpansFluentHorizontalScrollBarCorner()
    {
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);

        try
        {
            var projectTree = UiTestDriver.GetRequiredControl<ProjectTreeView>(window, "ProjectTree");
            var treeScrollViewer = Assert.Single(
                projectTree.GetVisualDescendants().OfType<ScrollViewer>());
            var verticalScrollBar = Assert.Single(
                treeScrollViewer.GetVisualDescendants()
                    .OfType<ScrollBar>(),
                scrollBar => scrollBar.Orientation == Orientation.Vertical);
            var scrollBarsSeparator = Assert.Single(
                treeScrollViewer.GetVisualDescendants()
                    .OfType<Panel>(),
                panel => panel.Name == "PART_ScrollBarsSeparator");

            Assert.Equal(2, Grid.GetRowSpan(verticalScrollBar));
            Assert.False(scrollBarsSeparator.IsVisible);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task SettingsListScrollBars_ReachChecklistEdges()
    {
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);

        try
        {
            foreach (var listName in new[]
                     {
                         "IgnoreOptionsList",
                         "ExtensionsList"
                     })
            {
                var listBox = UiTestDriver.GetRequiredControl<ListBox>(window, listName);
                var checklistBorder = Assert.IsType<Border>(listBox.Parent);
                var scrollViewer = Assert.Single(
                    listBox.GetVisualDescendants().OfType<ScrollViewer>());
                var verticalScrollBar = Assert.Single(
                    scrollViewer.GetVisualDescendants()
                        .OfType<ScrollBar>(),
                    scrollBar => scrollBar.Orientation == Orientation.Vertical);
                var scrollBarsSeparator = Assert.Single(
                    scrollViewer.GetVisualDescendants()
                        .OfType<Panel>(),
                    panel => panel.Name == "PART_ScrollBarsSeparator");
                var checklistBounds =
                    UiTestDriver.GetBoundsInWindow(checklistBorder, window);
                var scrollViewerBounds =
                    UiTestDriver.GetBoundsInWindow(scrollViewer, window);

                Assert.Equal(default, checklistBorder.Padding);
                Assert.Equal(new Thickness(5), scrollViewer.Padding);
                Assert.InRange(
                    Math.Abs(scrollViewerBounds.Left - checklistBounds.Left),
                    0,
                    1);
                Assert.InRange(
                    Math.Abs(scrollViewerBounds.Top - checklistBounds.Top),
                    0,
                    1);
                Assert.InRange(
                    Math.Abs(scrollViewerBounds.Right - checklistBounds.Right),
                    0,
                    1);
                Assert.InRange(
                    Math.Abs(scrollViewerBounds.Bottom - checklistBounds.Bottom),
                    0,
                    1);
                Assert.Equal(default, verticalScrollBar.Margin);
                Assert.Equal(HorizontalAlignment.Right, verticalScrollBar.HorizontalAlignment);
                Assert.Equal(VerticalAlignment.Stretch, verticalScrollBar.VerticalAlignment);
                Assert.Equal(2, Grid.GetRowSpan(verticalScrollBar));
                Assert.False(scrollBarsSeparator.IsVisible);
            }
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task LoadedProject_SettingsIslandLeftEdgeAlignsWithFormatSwitcher()
    {
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);

        try
        {
            var settingsIsland = UiTestDriver.GetRequiredControl<Border>(window, "SettingsIsland");
            var formatSwitcher = UiTestDriver.GetRequiredTopMenuControl<Border>(window, "FormatSegmentedControl");

            var settingsBounds = UiTestDriver.GetBoundsInWindow(settingsIsland, window);
            var formatBounds = UiTestDriver.GetBoundsInWindow(formatSwitcher, window);

            var delta = Math.Abs(settingsBounds.Left - formatBounds.Left);
            Assert.True(
                delta <= 2.5,
                $"SettingsLeft={settingsBounds.Left:F2}, SettingsWidth={settingsBounds.Width:F2}, " +
                $"FormatLeft={formatBounds.Left:F2}, FormatWidth={formatBounds.Width:F2}, Delta={delta:F2}");
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task PreviewOpenClose_KeepsSettingsIslandHorizontallyStationary()
    {
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);

        try
        {
            window.Width = 1499.2;
            await UiTestDriver.WaitForSettledFramesAsync(frameCount: 8);

            var settingsIsland =
                UiTestDriver.GetRequiredControl<Border>(
                    window,
                    "SettingsIsland");

            var openLeftEdges = await ObserveHorizontalPositionsAsync(
                window,
                settingsIsland,
                () => UiTestDriver.OpenPreviewAsync(window));
            var closeLeftEdges = await ObserveHorizontalPositionsAsync(
                window,
                settingsIsland,
                () => UiTestDriver.ClosePreviewAsync(window));

            AssertHorizontalPositionIsStable(openLeftEdges, "open");
            AssertHorizontalPositionIsStable(closeLeftEdges, "close");
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task PreviewOpenClose_KeepsDividerAndIslandBoundarySynchronized()
    {
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);

        try
        {
            var splitter =
                UiTestDriver.GetRequiredControl<Border>(window, "TreePreviewSplitter");

            var openErrors = await ObservePreviewBoundaryErrorsAsync(
                window,
                () => UiTestDriver.OpenPreviewAsync(window));
            AssertPreviewBoundaryIsSynchronized(openErrors, "open");
            Assert.InRange(
                UiTestDriver.GetBoundsInWindow(splitter, window).Width,
                WorkspacePresentationController.TreePreviewSplitterWidth - 0.01,
                WorkspacePresentationController.TreePreviewSplitterWidth + 0.01);

            var closeErrors = await ObservePreviewBoundaryErrorsAsync(
                window,
                () => UiTestDriver.ClosePreviewAsync(window));
            AssertPreviewBoundaryIsSynchronized(closeErrors, "close");
            Assert.False(splitter.IsVisible);
            Assert.InRange(splitter.Width, 0, 0.01);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task PreviewOpen_KeepsFinalLiveSurfaceWidthFixedWhileCarrierReveals()
    {
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);

        try
        {
            var previewContainer =
                UiTestDriver.GetRequiredControl<Border>(window, "PreviewPaneContainer");
            var previewSurface =
                UiTestDriver.GetRequiredControl<Grid>(window, "PreviewPaneSurface");
            var frozenFrames = new List<(double SurfaceWidth, double CarrierWidth)>();

            void CaptureFrozenSurface(object? sender, EventArgs args)
            {
                _ = sender;
                _ = args;
                if (double.IsNaN(previewSurface.Width) ||
                    previewSurface.Bounds.Width <= 0.5)
                {
                    return;
                }

                frozenFrames.Add(
                    (previewSurface.Bounds.Width, previewContainer.Bounds.Width));
            }

            window.LayoutUpdated += CaptureFrozenSurface;
            try
            {
                await UiTestDriver.OpenPreviewAsync(window);
            }
            finally
            {
                window.LayoutUpdated -= CaptureFrozenSurface;
            }

            Assert.NotEmpty(frozenFrames);
            var frozenWidth = frozenFrames[0].SurfaceWidth;
            Assert.All(
                frozenFrames,
                frame =>
                {
                    Assert.InRange(Math.Abs(frame.SurfaceWidth - frozenWidth), 0, 0.01);
                    Assert.True(frame.CarrierWidth <= frozenWidth + 0.01);
                });
            Assert.InRange(
                Math.Abs(frozenFrames[^1].CarrierWidth - frozenWidth),
                0,
                1);
            Assert.True(previewContainer.ClipToBounds);
            Assert.True(double.IsNaN(previewSurface.Width));
            Assert.Equal(HorizontalAlignment.Stretch, previewSurface.HorizontalAlignment);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task PreviewClose_KeepsLiveSurfaceWidthFixedWhileCarrierClips()
    {
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);

        try
        {
            await UiTestDriver.OpenPreviewAsync(window);

            var previewContainer =
                UiTestDriver.GetRequiredControl<Border>(window, "PreviewPaneContainer");
            var previewSurface =
                UiTestDriver.GetRequiredControl<Grid>(window, "PreviewPaneSurface");
            var renderedSurfaceWidthBeforeClose = previewSurface.Bounds.Width;
            var frozenFrames = new List<(double SurfaceWidth, double CarrierWidth)>();

            void CaptureFrozenSurface(object? sender, EventArgs args)
            {
                _ = sender;
                _ = args;
                if (double.IsNaN(previewSurface.Width))
                    return;

                frozenFrames.Add(
                    (previewSurface.Bounds.Width, previewContainer.Bounds.Width));
            }

            window.LayoutUpdated += CaptureFrozenSurface;
            try
            {
                await UiTestDriver.ClosePreviewAsync(window);
            }
            finally
            {
                window.LayoutUpdated -= CaptureFrozenSurface;
            }

            Assert.NotEmpty(frozenFrames);
            var frozenWidth = frozenFrames[0].SurfaceWidth;
            Assert.InRange(
                Math.Abs(frozenWidth - renderedSurfaceWidthBeforeClose),
                0,
                0.01);
            Assert.All(
                frozenFrames,
                frame =>
                {
                    Assert.InRange(Math.Abs(frame.SurfaceWidth - frozenWidth), 0, 0.01);
                    Assert.True(frame.CarrierWidth <= frozenWidth + 0.01);
                });
            Assert.True(previewContainer.ClipToBounds);
            Assert.True(double.IsNaN(previewSurface.Width));
            Assert.Equal(HorizontalAlignment.Stretch, previewSurface.HorizontalAlignment);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SettingsOpenClose_KeepsNeighborBoundarySynchronized(
        bool previewVisible,
        bool previewOnly)
    {
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);

        try
        {
            if (previewVisible)
                await UiTestDriver.OpenPreviewAsync(window);
            if (previewOnly)
                await UiTestDriver.HidePreviewTreeAsync(window);

            var leadingPane = UiTestDriver.GetRequiredControl<Border>(
                window,
                previewVisible ? "PreviewPaneContainer" : "TreePaneContainer");

            var closeErrors = await ObserveSettingsBoundaryErrorsAsync(
                window,
                leadingPane,
                async () =>
                {
                    await UiTestDriver.PressKeyAsync(window, Key.P, RawInputModifiers.Control);
                    await UiTestDriver.WaitForSettingsVisibilityAsync(window, visible: false);
                });
            var openErrors = await ObserveSettingsBoundaryErrorsAsync(
                window,
                leadingPane,
                async () =>
                {
                    await UiTestDriver.PressKeyAsync(window, Key.P, RawInputModifiers.Control);
                    await UiTestDriver.WaitForSettingsVisibilityAsync(window, visible: true);
                });

            AssertSettingsBoundaryIsSynchronized(closeErrors, "close", previewVisible);
            AssertSettingsBoundaryIsSynchronized(openErrors, "open", previewVisible);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task PreviewTreePane_DefaultsToMinimumWidth_PreservesManualResizeUntilNextOpen()
    {
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);

        try
        {
            await UiTestDriver.OpenPreviewAsync(window);

            var treePaneContainer = UiTestDriver.GetRequiredControl<Border>(window, "TreePaneContainer");
            var splitter = UiTestDriver.GetRequiredControl<Border>(window, "TreePreviewSplitter");
            var initialWidth = UiTestDriver.GetBoundsInWindow(treePaneContainer, window).Width;
            Assert.InRange(initialWidth, 417, 419);

            window.Width += 240;
            await UiTestDriver.WaitForSettledFramesAsync(frameCount: 8);
            var widthAfterWindowExpansion = UiTestDriver.GetBoundsInWindow(treePaneContainer, window).Width;
            Assert.InRange(Math.Abs(widthAfterWindowExpansion - initialWidth), 0, 1.5);

            await UiTestDriver.DragAsync(window, splitter, deltaX: 100);
            var manuallyExpandedWidth = UiTestDriver.GetBoundsInWindow(treePaneContainer, window).Width;
            Assert.True(manuallyExpandedWidth > initialWidth + 20);

            await UiTestDriver.ClosePreviewAsync(window);
            await UiTestDriver.OpenPreviewAsync(window);
            var reopenedWidth = UiTestDriver.GetBoundsInWindow(treePaneContainer, window).Width;
            Assert.InRange(reopenedWidth, 417, 419);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task PreviewTreeTools_RightButtonsUseDesignedInsets()
    {
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);

        try
        {
            await UiTestDriver.OpenPreviewAsync(window);
            var treeCloseButton = UiTestDriver.GetRequiredControl<Button>(window, "PreviewTreeHideButton");
            var treeCloseBounds = UiTestDriver.GetBoundsInWindow(treeCloseButton, window);

            await UiTestDriver.OpenFilterAsync(window);
            var filterBar = UiTestDriver.GetRequiredControl<FilterBarView>(window, "FilterBar");
            var filterCloseButton = Assert.IsType<Button>(filterBar.FindControl<Button>("FilterCloseButton"));
            var filterCloseBounds = UiTestDriver.GetBoundsInWindow(filterCloseButton, window);
            Assert.InRange(Math.Abs(filterCloseBounds.Center.X - treeCloseBounds.Center.X), 0, 1.5);

            await UiTestDriver.RaiseButtonClickAsync(filterCloseButton);
            await UiTestDriver.WaitForConditionAsync(
                window,
                () => !UiTestDriver.GetViewModel(window).FilterVisible,
                "filter toolbar to close before opening search");

            await UiTestDriver.OpenSearchAsync(window);
            var searchBar = UiTestDriver.GetRequiredControl<SearchBarView>(window, "SearchBar");
            var previousButton = Assert.IsType<Button>(searchBar.FindControl<Button>("SearchPreviousButton"));
            var nextButton = Assert.IsType<Button>(searchBar.FindControl<Button>("SearchNextButton"));
            var searchCloseButton = Assert.IsType<Button>(searchBar.FindControl<Button>("SearchCloseButton"));
            var previousBounds = UiTestDriver.GetBoundsInWindow(previousButton, window);
            var nextBounds = UiTestDriver.GetBoundsInWindow(nextButton, window);
            var searchCloseBounds = UiTestDriver.GetBoundsInWindow(searchCloseButton, window);

            Assert.InRange(treeCloseBounds.Center.X - searchCloseBounds.Center.X, 3, 5);
            Assert.InRange(nextBounds.Left - previousBounds.Right, 3, 5);
            Assert.InRange(searchCloseBounds.Left - nextBounds.Right, 3, 5);
            Assert.InRange(Math.Abs(previousBounds.Center.Y - nextBounds.Center.Y), 0, 1);
            Assert.InRange(Math.Abs(nextBounds.Center.Y - searchCloseBounds.Center.Y), 0, 1);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task PreviewOnlyClose_RestoresActiveFilterAfterToolbarCycle()
    {
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);

        try
        {
            await UiTestDriver.OpenFilterAsync(window);

            var filterBar = UiTestDriver.GetRequiredControl<FilterBarView>(window, "FilterBar");
            await UiTestDriver.EnterTextAsync(window, Assert.IsType<TextBox>(filterBar.FilterBoxControl), "preview");
            await UiTestDriver.WaitForFilterAppliedAsync(window, "preview");

            await UiTestDriver.OpenPreviewAsync(window);
            await UiTestDriver.HidePreviewTreeAsync(window);
            await UiTestDriver.ClosePreviewAsync(window);

            await UiTestDriver.WaitForConditionAsync(
                window,
                () =>
                {
                    var viewModel = UiTestDriver.GetViewModel(window);
                    return viewModel.FilterVisible &&
                           viewModel.NameFilter == "preview" &&
                           UiTestDriver.GetRequiredControl<Border>(window, "FilterBarContainer").IsVisible;
                },
                "filter state to be restored after preview-only close");
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task PreviewOnlyClose_RestoresActiveSearchAfterToolbarCycle()
    {
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);

        try
        {
            await UiTestDriver.OpenSearchAsync(window);

            var searchBar = UiTestDriver.GetRequiredControl<SearchBarView>(window, "SearchBar");
            await UiTestDriver.EnterTextAsync(window, Assert.IsType<TextBox>(searchBar.SearchBoxControl), "preview");
            await UiTestDriver.WaitForSearchAppliedAsync(window, "preview");

            await UiTestDriver.OpenPreviewAsync(window);
            await UiTestDriver.HidePreviewTreeAsync(window);
            await UiTestDriver.ClosePreviewAsync(window);

            await UiTestDriver.WaitForConditionAsync(
                window,
                () =>
                {
                    var viewModel = UiTestDriver.GetViewModel(window);
                    return viewModel.SearchVisible &&
                           viewModel.SearchQuery == "preview" &&
                           UiTestDriver.GetRequiredControl<Border>(window, "SearchBarContainer").IsVisible;
                },
                "search state to be restored after preview-only close");
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    private static async Task<IReadOnlyList<double>> ObserveHorizontalPositionsAsync(
        MainWindow window,
        Control control,
        Func<Task> transition)
    {
        var positions = new List<double>();

        void CapturePosition(object? sender, EventArgs args)
        {
            _ = sender;
            _ = args;
            positions.Add(
                UiTestDriver.GetBoundsInWindow(control, window).Left);
        }

        CapturePosition(null, EventArgs.Empty);
        window.LayoutUpdated += CapturePosition;
        try
        {
            await transition();
            await UiTestDriver.WaitForSettledFramesAsync(frameCount: 8);
            CapturePosition(null, EventArgs.Empty);
        }
        finally
        {
            window.LayoutUpdated -= CapturePosition;
        }

        return positions;
    }

    private static async Task<IReadOnlyList<double>> ObserveSettingsBoundaryErrorsAsync(
        MainWindow window,
        Control leadingPane,
        Func<Task> transition)
    {
        var settingsContainer =
            UiTestDriver.GetRequiredControl<Border>(window, "SettingsContainer");
        var settingsIsland =
            UiTestDriver.GetRequiredControl<Border>(window, "SettingsIsland");
        var errors = new List<double>();

        void CaptureBoundary(object? sender, EventArgs args)
        {
            _ = sender;
            _ = args;
            var leadingBounds =
                UiTestDriver.GetBoundsInWindow(leadingPane, window);
            var settingsBounds =
                UiTestDriver.GetBoundsInWindow(settingsContainer, window);
            var settingsIslandBounds =
                UiTestDriver.GetBoundsInWindow(settingsIsland, window);
            var layoutBoundaryError =
                Math.Abs(settingsBounds.Left - leadingBounds.Right);
            var visibleIslandBoundaryError = settingsBounds.Width > 0.5
                ? Math.Abs(
                    settingsIslandBounds.Left -
                    settingsBounds.Left -
                    WorkspacePresentationController.PreviewSettingsSplitterWidth)
                : 0.0;
            errors.Add(Math.Max(layoutBoundaryError, visibleIslandBoundaryError));
        }

        CaptureBoundary(null, EventArgs.Empty);
        window.LayoutUpdated += CaptureBoundary;
        try
        {
            await transition();
            CaptureBoundary(null, EventArgs.Empty);
        }
        finally
        {
            window.LayoutUpdated -= CaptureBoundary;
        }

        return errors;
    }

    private static async Task<IReadOnlyList<double>> ObservePreviewBoundaryErrorsAsync(
        MainWindow window,
        Func<Task> transition)
    {
        var treeContainer =
            UiTestDriver.GetRequiredControl<Border>(window, "TreePaneContainer");
        var previewContainer =
            UiTestDriver.GetRequiredControl<Border>(window, "PreviewPaneContainer");
        var previewIsland =
            UiTestDriver.GetRequiredControl<Border>(window, "PreviewIsland");
        var splitter =
            UiTestDriver.GetRequiredControl<Border>(window, "TreePreviewSplitter");
        var errors = new List<double>();

        void CaptureBoundary(object? sender, EventArgs args)
        {
            _ = sender;
            _ = args;
            var treeBounds = UiTestDriver.GetBoundsInWindow(treeContainer, window);
            var previewBounds = UiTestDriver.GetBoundsInWindow(previewContainer, window);
            var previewIslandBounds = UiTestDriver.GetBoundsInWindow(previewIsland, window);
            var splitterWidth = splitter.IsVisible
                ? UiTestDriver.GetBoundsInWindow(splitter, window).Width
                : 0.0;
            var carrierBoundaryError = Math.Abs(previewBounds.Left - treeBounds.Right);
            var islandBoundaryError = previewBounds.Width > 0.5
                ? Math.Abs(
                    previewIslandBounds.Left -
                    previewBounds.Left -
                    splitterWidth)
                : 0.0;
            errors.Add(Math.Max(carrierBoundaryError, islandBoundaryError));
        }

        CaptureBoundary(null, EventArgs.Empty);
        window.LayoutUpdated += CaptureBoundary;
        try
        {
            await transition();
            CaptureBoundary(null, EventArgs.Empty);
        }
        finally
        {
            window.LayoutUpdated -= CaptureBoundary;
        }

        return errors;
    }

    private static void AssertSettingsBoundaryIsSynchronized(
        IReadOnlyList<double> errors,
        string transitionName,
        bool previewVisible)
    {
        Assert.NotEmpty(errors);
        var maximumError = errors.Max();
        Assert.True(
            maximumError <= 0.01,
            $"Settings {transitionName} lost synchronization with the " +
            $"{(previewVisible ? "preview" : "tree")} pane: " +
            $"maximum boundary error={maximumError:F2}.");
        Assert.True(
            errors[^1] <= 0.01,
            $"Settings {transitionName} finished with a boundary error of " +
            $"{errors[^1]:F2}.");
    }

    private static void AssertPreviewBoundaryIsSynchronized(
        IReadOnlyList<double> errors,
        string transitionName)
    {
        Assert.NotEmpty(errors);
        var maximumError = errors.Max();
        Assert.True(
            maximumError <= 0.01,
            $"Preview {transitionName} lost synchronization between its " +
            $"carrier, divider, and island: maximum boundary error={maximumError:F2}.");
        Assert.True(
            errors[^1] <= 0.01,
            $"Preview {transitionName} finished with a boundary error of " +
            $"{errors[^1]:F2}.");
    }

    private static void AssertHorizontalPositionIsStable(
        IReadOnlyList<double> positions,
        string transitionName)
    {
        Assert.NotEmpty(positions);
        var minimum = positions.Min();
        var maximum = positions.Max();
        Assert.True(
            maximum - minimum <= 0.01,
            $"Settings island moved during preview {transitionName}: " +
            $"min={minimum:F2}, max={maximum:F2}, delta={maximum - minimum:F2}.");
    }
}
