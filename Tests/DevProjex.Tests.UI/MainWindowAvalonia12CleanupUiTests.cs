using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using DevProjex.Avalonia.Controls;

namespace DevProjex.Tests.UI;

[Collection(UiWorkspaceCollection.Name)]
public sealed class MainWindowAvalonia12CleanupUiTests(UiWorkspaceFixture workspace)
{
    [AvaloniaFact]
    public async Task DropZone_AnimationRunsOnlyWhileDropZoneIsVisible()
    {
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);

        try
        {
            var dropZone = UiTestDriver.GetRequiredControl<Border>(window, "DropZoneContainer");
            var viewModel = UiTestDriver.GetViewModel(window);

            // This is an idle-performance contract: a hidden drop zone must not retain
            // the selector that owns its infinite animations.
            Assert.False(dropZone.IsVisible);
            Assert.DoesNotContain("drop-zone-animating", dropZone.Classes);

            viewModel.IsProjectLoaded = false;
            await UiTestDriver.WaitForSettledFramesAsync(frameCount: 4);

            Assert.True(dropZone.IsVisible);
            Assert.Contains("drop-zone-animating", dropZone.Classes);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task TopMenuPopups_UseNativeLightDismissAndPlacementConstraints()
    {
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);

        try
        {
            var themePopup = UiTestDriver.GetRequiredTopMenuControl<Popup>(window, "ThemePopup");
            var helpPopup = UiTestDriver.GetRequiredTopMenuControl<Popup>(window, "HelpPopup");
            var helpDocsPopup = UiTestDriver.GetRequiredTopMenuControl<Popup>(window, "HelpDocsPopup");
            var updatePopup = UiTestDriver.GetRequiredTopMenuControl<Popup>(window, "UpdatePopup");
            var themeMenuItem = UiTestDriver.GetRequiredTopMenuControl<MenuItem>(window, "ThemeMenuItem");
            var helpMenuItem = UiTestDriver.GetRequiredTopMenuControl<MenuItem>(window, "HelpMenuItem");
            var expectedAdjustment =
                PopupPositionerConstraintAdjustment.SlideX |
                PopupPositionerConstraintAdjustment.SlideY |
                PopupPositionerConstraintAdjustment.FlipX |
                PopupPositionerConstraintAdjustment.FlipY |
                PopupPositionerConstraintAdjustment.ResizeX |
                PopupPositionerConstraintAdjustment.ResizeY;

            Assert.True(themePopup.IsLightDismissEnabled);
            Assert.False(themePopup.OverlayDismissEventPassThrough);
            Assert.False(themePopup.ShouldUseOverlayLayer);
            Assert.False(themePopup.WindowManagerAddShadowHint);
            Assert.Equal(expectedAdjustment, themePopup.PlacementConstraintAdjustment);
            Assert.Same(themeMenuItem, themePopup.PlacementTarget);

            Assert.True(helpPopup.IsLightDismissEnabled);
            Assert.False(helpPopup.OverlayDismissEventPassThrough);
            Assert.False(helpPopup.ShouldUseOverlayLayer);
            Assert.False(helpPopup.WindowManagerAddShadowHint);
            Assert.Equal(expectedAdjustment, helpPopup.PlacementConstraintAdjustment);
            Assert.Equal(PlacementMode.Custom, helpPopup.Placement);
            Assert.NotNull(helpPopup.CustomPopupPlacementCallback);
            Assert.Equal(4, helpPopup.VerticalOffset);
            Assert.Same(helpMenuItem, helpPopup.PlacementTarget);

            Assert.True(helpDocsPopup.IsLightDismissEnabled);
            Assert.False(helpDocsPopup.OverlayDismissEventPassThrough);
            Assert.False(helpDocsPopup.ShouldUseOverlayLayer);
            Assert.False(helpDocsPopup.WindowManagerAddShadowHint);
            Assert.Equal(expectedAdjustment, helpDocsPopup.PlacementConstraintAdjustment);
            Assert.Equal(PlacementMode.Custom, helpDocsPopup.Placement);
            Assert.NotNull(helpDocsPopup.CustomPopupPlacementCallback);
            Assert.Equal(4, helpDocsPopup.VerticalOffset);
            Assert.Same(helpMenuItem, helpDocsPopup.PlacementTarget);

            Assert.True(updatePopup.IsLightDismissEnabled);
            Assert.False(updatePopup.OverlayDismissEventPassThrough);
            Assert.False(updatePopup.ShouldUseOverlayLayer);
            Assert.False(updatePopup.WindowManagerAddShadowHint);
            Assert.Equal(expectedAdjustment, updatePopup.PlacementConstraintAdjustment);
            Assert.Equal(PlacementMode.Custom, updatePopup.Placement);
            Assert.NotNull(updatePopup.CustomPopupPlacementCallback);
            Assert.Equal(4, updatePopup.VerticalOffset);
            Assert.Same(helpMenuItem, updatePopup.PlacementTarget);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task TopMenuPopoverCards_ClipRoundedSurfaceWithoutExternalShadow()
    {
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);

        try
        {
            await UiTestDriver.WaitForSettledFramesAsync(frameCount: 4);

            AssertPopoverCard(UiTestDriver.GetRequiredTopMenuControl<ThemePopoverView>(window, "ThemePopover"));
            AssertPopoverCard(UiTestDriver.GetRequiredTopMenuControl<AboutPopoverView>(window, "HelpPopover"));
            AssertPopoverCard(UiTestDriver.GetRequiredTopMenuControl<HelpPopoverView>(window, "HelpDocsPopover"));
            AssertPopoverCard(UiTestDriver.GetRequiredTopMenuControl<UpdatePopoverView>(window, "UpdatePopover"));
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task TreeNodeCheckbox_ClickTogglesCheckStateWithoutSelectingTreeRow()
    {
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);

        try
        {
            var tree = UiTestDriver.GetRequiredControl<ProjectTreeView>(window, "ProjectTree");
            var viewModel = UiTestDriver.GetViewModel(window);
            var rootNode = Assert.Single(viewModel.TreeNodes);
            rootNode.IsExpanded = true;
            await UiTestDriver.WaitForSettledFramesAsync(frameCount: 6);

            var srcNode = rootNode.Children.Single(node => string.Equals(node.DisplayName, "src", StringComparison.Ordinal));
            srcNode.IsChecked = false;
            srcNode.IsSelected = false;
            tree.SelectedItem = null;
            await UiTestDriver.WaitForSettledFramesAsync(frameCount: 4);

            var checkBox = await UiTestDriver.WaitForTreeNodeCheckBoxAsync(window, "src");
            await UiTestDriver.ClickAsync(window, checkBox);

            Assert.True(srcNode.IsChecked);
            Assert.False(srcNode.IsSelected);
            Assert.NotSame(srcNode, tree.SelectedItem);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    private static void AssertPopoverCard(UserControl popover)
    {
        var card = Assert.IsType<Border>(popover.Content);

        Assert.Contains("theme-popover", card.Classes);
        Assert.Equal(new CornerRadius(8), card.CornerRadius);
        Assert.True(card.ClipToBounds);
        Assert.Equal(0, card.BoxShadow.Count);
    }
}
