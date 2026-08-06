using DevProjex.Avalonia.Services;

namespace DevProjex.Avalonia.Coordinators;

internal sealed record TreeViewportControls(
    TreeView TreeView,
    Border TreeIsland,
    Border PreviewIsland,
    Border PreviewLineNumbersBackground,
    ScrollViewer PreviewTextScrollViewer,
    VirtualizedLineNumbersControl PreviewLineNumbersControl);

internal sealed class TreeViewportController(
    MainWindowViewModel viewModel,
    TreeViewportControls controls,
    Action cancelBackgroundMemoryCleanup,
    Action<MemoryCleanupReason> scheduleBackgroundMemoryCleanup)
{
    private const string TreeItemPaddingResourceKey =
        "TreeItemPaddingResource";
    private const string TreeItemSpacingResourceKey =
        "TreeItemSpacingResource";
    private const string TreeIconSizeResourceKey =
        "TreeIconSizeResource";
    private const string TreeTextMarginResourceKey =
        "TreeTextMarginResource";

    public void UpdateVisualResources()
    {
        controls.TreeView.Resources[TreeItemPaddingResourceKey] =
            viewModel.TreeItemPadding;
        controls.TreeView.Resources[TreeItemSpacingResourceKey] =
            viewModel.TreeItemSpacing;
        controls.TreeView.Resources[TreeIconSizeResourceKey] =
            viewModel.TreeIconSize;
        controls.TreeView.Resources[TreeTextMarginResourceKey] =
            viewModel.TreeTextMargin;
    }

    public void ExpandAll()
    {
        // A compacting collection must not interrupt lazy-node realization.
        cancelBackgroundMemoryCleanup();
        SetExpandedState(expand: true);
    }

    public void CollapseAll()
    {
        SetExpandedState(expand: false);

        // Layout must detach realized rows before native/managed trimming.
        scheduleBackgroundMemoryCleanup(
            MemoryCleanupReason.TreeCollapseCompleted);
    }

    public void ZoomIn()
        => AdjustZoomFontSize(1);

    public void ZoomOut()
        => AdjustZoomFontSize(-1);

    public void ResetZoom()
    {
        if (viewModel.IsPreviewTreeVisible)
        {
            ResetTreeZoom();
            ResetPreviewZoom();
            return;
        }

        if (viewModel.IsAnyPreviewVisible)
            ResetPreviewZoom();
        else
            ResetTreeZoom();
    }

    public void HandleTreePointerEntered()
    {
        if (viewModel.SearchVisible ||
            viewModel.FilterVisible ||
            !viewModel.IsTreePaneVisible)
        {
            return;
        }

        controls.TreeView.Focus();
    }

    public void HandlePointerWheelChanged(
        PointerWheelEventArgs e)
    {
        var zoomTarget = GetZoomSurfaceTarget(e.Source);
        if (!TreeZoomWheelHandler.TryGetZoomStep(
                e.KeyModifiers,
                e.Delta,
                zoomTarget != ZoomSurfaceTarget.None,
                out var step))
        {
            return;
        }

        AdjustZoomFontSize(step, zoomTarget);
        e.Handled = true;
    }

    private void SetExpandedState(bool expand)
    {
        if (!viewModel.IsTreePaneVisible)
            return;

        foreach (var node in viewModel.TreeNodes)
        {
            node.SetExpandedRecursive(expand);
            if (!expand)
                node.IsExpanded = true;
        }
    }

    private void AdjustZoomFontSize(
        double delta,
        ZoomSurfaceTarget? target = null)
    {
        if (viewModel.IsPreviewTreeVisible && target is null)
        {
            viewModel.TreeFontSize =
                ClampZoomFontSize(
                    viewModel.TreeFontSize + delta);
            viewModel.PreviewFontSize =
                ClampZoomFontSize(
                    viewModel.PreviewFontSize + delta);
            return;
        }

        var effectiveTarget =
            target ??
            (viewModel.IsAnyPreviewVisible
                ? ZoomSurfaceTarget.Preview
                : ZoomSurfaceTarget.Tree);
        if (effectiveTarget == ZoomSurfaceTarget.Preview)
        {
            viewModel.PreviewFontSize =
                ClampZoomFontSize(
                    viewModel.PreviewFontSize + delta);
            return;
        }

        viewModel.TreeFontSize =
            ClampZoomFontSize(viewModel.TreeFontSize + delta);
    }

    private ZoomSurfaceTarget GetZoomSurfaceTarget(object? source)
    {
        if (ReferenceEquals(source, controls.TreeView) ||
            ReferenceEquals(source, controls.TreeIsland))
        {
            return ZoomSurfaceTarget.Tree;
        }

        if (viewModel.IsAnyPreviewVisible &&
            IsPreviewSurface(source))
        {
            return ZoomSurfaceTarget.Preview;
        }

        if (source is not Visual visual)
            return ZoomSurfaceTarget.None;

        foreach (var ancestor in visual.GetVisualAncestors())
        {
            if (ReferenceEquals(ancestor, controls.TreeIsland) ||
                ReferenceEquals(ancestor, controls.TreeView))
            {
                return ZoomSurfaceTarget.Tree;
            }

            if (viewModel.IsAnyPreviewVisible &&
                IsPreviewSurface(ancestor))
            {
                return ZoomSurfaceTarget.Preview;
            }
        }

        return ZoomSurfaceTarget.None;
    }

    private bool IsPreviewSurface(object? candidate)
        => ReferenceEquals(candidate, controls.PreviewIsland) ||
           ReferenceEquals(
               candidate,
               controls.PreviewLineNumbersBackground) ||
           ReferenceEquals(
               candidate,
               controls.PreviewTextScrollViewer) ||
           ReferenceEquals(
               candidate,
               controls.PreviewLineNumbersControl);

    private static double ClampZoomFontSize(double value)
        => Math.Clamp(value, 6, 28);

    private void ResetTreeZoom()
        => viewModel.TreeFontSize =
            MainWindowViewModel.DefaultTreeFontSize;

    private void ResetPreviewZoom()
        => viewModel.PreviewFontSize =
            MainWindowViewModel.DefaultPreviewFontSize;

    private enum ZoomSurfaceTarget
    {
        None,
        Tree,
        Preview
    }
}
