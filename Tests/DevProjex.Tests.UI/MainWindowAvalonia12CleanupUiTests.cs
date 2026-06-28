using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using DevProjex.Avalonia.Controls;

namespace DevProjex.Tests.UI;

[Collection(UiWorkspaceCollection.Name)]
public sealed class MainWindowAvalonia12CleanupUiTests(UiWorkspaceFixture workspace)
{
    [AvaloniaFact]
    public async Task DropZone_KeepsAnimationClassStatic_WhenProjectIsLoaded()
    {
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);

        try
        {
            var dropZone = UiTestDriver.GetRequiredControl<Border>(window, "DropZoneContainer");

            Assert.False(dropZone.IsVisible);
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
            Assert.Same(themeMenuItem, themePopup.PlacementTarget);

            Assert.True(helpPopup.IsLightDismissEnabled);
            Assert.False(helpPopup.OverlayDismissEventPassThrough);
            Assert.False(helpPopup.ShouldUseOverlayLayer);
            Assert.False(helpPopup.WindowManagerAddShadowHint);
            Assert.Equal(expectedAdjustment, helpPopup.PlacementConstraintAdjustment);
            Assert.Same(helpMenuItem, helpPopup.PlacementTarget);

            Assert.True(helpDocsPopup.IsLightDismissEnabled);
            Assert.False(helpDocsPopup.OverlayDismissEventPassThrough);
            Assert.False(helpDocsPopup.ShouldUseOverlayLayer);
            Assert.False(helpDocsPopup.WindowManagerAddShadowHint);
            Assert.Equal(expectedAdjustment, helpDocsPopup.PlacementConstraintAdjustment);
            Assert.Same(helpMenuItem, helpDocsPopup.PlacementTarget);
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
