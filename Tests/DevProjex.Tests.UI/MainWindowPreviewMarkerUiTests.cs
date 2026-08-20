using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.VisualTree;
using DevProjex.Avalonia.Controls;
using DevProjex.Application.Services;

namespace DevProjex.Tests.UI;

[Collection(UiWorkspaceCollection.Name)]
public sealed class MainWindowPreviewMarkerUiTests
{
	[AvaloniaFact]
	public async Task MarkerOverStickyPath_ActivatesScrollBarAndKeepsMarkerPriority()
	{
		using var project = UiTestProject.CreateWithPreviewMarkerWorkspace();
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

		try
		{
			await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(window, IgnoreOptionId.HideSecrets);
			await UiTestDriver.ClickApplySettingsAsync(window);
			await UiTestDriver.OpenPreviewAsync(window);
			await UiTestDriver.SwitchPreviewModeAsync(window, PreviewContentMode.Content);

			var markerBar = UiTestDriver.GetRequiredControl<PreviewMarkerBar>(window, "PreviewMarkerBar");
			var preview = UiTestDriver.GetRequiredControl<VirtualizedPreviewTextControl>(window, "PreviewTextControl");
			var previewIsland = UiTestDriver.GetRequiredControl<Border>(window, "PreviewIsland");
			var stickyHeader = UiTestDriver.GetRequiredControl<Border>(window, "PreviewStickyHeaderContainer");
			var stickyNavigate = UiTestDriver.GetRequiredControl<Button>(window, "PreviewStickyHeaderNavigateButton");
			var scrollViewer = UiTestDriver.GetRequiredPreviewScrollViewer(window);
			var verticalScrollBar = Assert.Single(
				scrollViewer.GetVisualDescendants().OfType<ScrollBar>(),
				static scrollBar => scrollBar.Orientation == Orientation.Vertical);
			verticalScrollBar.HideDelay = TimeSpan.Zero;
			verticalScrollBar.ShowDelay = TimeSpan.Zero;

			await UiTestDriver.WaitForConditionAsync(
				window,
				() => markerBar.IsVisible &&
				      markerBar.MarkerTicks.Count(static tick =>
					      tick.Target.Category == PreviewMarkerCategory.Redaction) == 3 &&
				      scrollViewer.Extent.Height > scrollViewer.Viewport.Height,
				"large preview to expose three marker ticks");

			scrollViewer.Offset = new Vector(scrollViewer.Offset.X, 64);
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => stickyHeader.IsVisible,
				"sticky path to overlap the top of the collapsed scrollbar");
			window.MouseMove(UiTestDriver.GetControlCenter(previewIsland, window), RawInputModifiers.None);
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => !verticalScrollBar.IsExpanded && stickyHeader.Margin.Right < 0.1,
				"preview scrollbar to return to its collapsed state");

			var firstTick = markerBar.MarkerTicks
				.Where(static tick => tick.Target.Category == PreviewMarkerCategory.Redaction)
				.OrderBy(static tick => tick.Y)
				.First();
			var markerPoint = Assert.IsType<Point>(markerBar.TranslatePoint(
				new Point(markerBar.Bounds.Width - 1, firstTick.Y),
				window));
			Assert.True(UiTestDriver.GetBoundsInWindow(stickyHeader, window).Contains(markerPoint));
			var coveredHit = Assert.IsAssignableFrom<Visual>(window.InputHitTest(markerPoint));
			Assert.True(
				ReferenceEquals(coveredHit, stickyNavigate) ||
				ReferenceEquals(coveredHit.FindAncestorOfType<Button>(), stickyNavigate));

			window.MouseMove(markerPoint, RawInputModifiers.None);
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => stickyHeader.Margin.Right > 0.1 &&
				      !scrollViewer.AllowAutoHide &&
				      !verticalScrollBar.AllowAutoHide,
				"scrollbar hover to move the sticky path away and keep the rail active");
			await UiTestDriver.WaitForSettledFramesAsync(frameCount: 3);
			window.MouseMove(markerPoint, RawInputModifiers.None);
			await UiTestDriver.WaitForSettledFramesAsync(frameCount: 2);
			var revealedHit = Assert.IsAssignableFrom<InputElement>(window.InputHitTest(markerPoint));
			Assert.Equal("Hand", revealedHit.Cursor?.ToString());
			Assert.False(
				ReferenceEquals(revealedHit, stickyNavigate) ||
				revealedHit is Visual visual &&
				ReferenceEquals(visual.FindAncestorOfType<Button>(), stickyNavigate));

			window.MouseDown(markerPoint, MouseButton.Left, RawInputModifiers.LeftMouseButton);
			window.MouseUp(markerPoint, MouseButton.Left, RawInputModifiers.None);
			var lineTop = preview.GetVerticalOffsetForLine(firstTick.Target.LineNumber);
			var lineHeight = preview.GetVerticalOffsetForLine(firstTick.Target.LineNumber + 1) - lineTop;
			var expectedOffset = Math.Clamp(
				lineTop + (lineHeight / 2) - (scrollViewer.Viewport.Height / 2),
				0,
				Math.Max(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height));
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => Math.Abs(scrollViewer.Offset.Y - expectedOffset) < 1,
				"top marker to win over sticky-path navigation");
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	[AvaloniaFact]
	public async Task MarkerPointer_UsesHandCentersTheSelectedLineAndSupportsDragging()
	{
		using var project = UiTestProject.CreateWithPreviewMarkerWorkspace();
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

		try
		{
			await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(window, IgnoreOptionId.HideSecrets);
			await UiTestDriver.ClickApplySettingsAsync(window);
			await UiTestDriver.OpenPreviewAsync(window);
			await UiTestDriver.SwitchPreviewModeAsync(window, PreviewContentMode.Content);

			var markerBar = UiTestDriver.GetRequiredControl<PreviewMarkerBar>(window, "PreviewMarkerBar");
			var preview = UiTestDriver.GetRequiredControl<VirtualizedPreviewTextControl>(window, "PreviewTextControl");
			var scrollViewer = UiTestDriver.GetRequiredPreviewScrollViewer(window);
			var verticalScrollBar = Assert.Single(
				scrollViewer.GetVisualDescendants().OfType<ScrollBar>(),
				static scrollBar => scrollBar.Orientation == Orientation.Vertical);
			var track = Assert.Single(verticalScrollBar.GetVisualDescendants().OfType<Track>());
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => markerBar.IsVisible &&
				      markerBar.Bounds.Height > 0 &&
				      preview.MarkerSnapshot.Markers.Count(static marker =>
					      marker.Category == PreviewMarkerCategory.Redaction) == 3 &&
				      scrollViewer.Extent.Height > scrollViewer.Viewport.Height &&
				      Math.Abs(
					      UiTestDriver.GetBoundsInWindow(markerBar, window).Top -
					      UiTestDriver.GetBoundsInWindow(track, window).Top) < 0.5 &&
				      Math.Abs(markerBar.Bounds.Height - track.Bounds.Height) < 0.5,
				"preview marker stripe to contain three scrollable redactions");

			var tick = markerBar.MarkerTicks
				.Where(static candidate => candidate.Target.Category == PreviewMarkerCategory.Redaction)
				.OrderByDescending(static candidate => candidate.Y)
				.First();
			var pointerPoint = markerBar.TranslatePoint(
				new Point(markerBar.Bounds.Width - 1, tick.Y),
				window);
			Assert.NotNull(pointerPoint);

			window.MouseMove(pointerPoint.Value, RawInputModifiers.None);
			await UiTestDriver.WaitForSettledFramesAsync(frameCount: 3);
			var hit = Assert.IsAssignableFrom<InputElement>(window.InputHitTest(pointerPoint.Value));
			Assert.Equal("Hand", hit.Cursor?.ToString());

			window.MouseDown(pointerPoint.Value, MouseButton.Left, RawInputModifiers.LeftMouseButton);
			window.MouseUp(pointerPoint.Value, MouseButton.Left, RawInputModifiers.None);
			var lineTop = preview.GetVerticalOffsetForLine(tick.Target.LineNumber);
			var lineHeight = preview.GetVerticalOffsetForLine(tick.Target.LineNumber + 1) - lineTop;
			var maximumOffset = Math.Max(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
			var expectedOffset = Math.Clamp(
				lineTop + (lineHeight / 2) - (scrollViewer.Viewport.Height / 2),
				0,
				maximumOffset);
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => Math.Abs(scrollViewer.Offset.Y - expectedOffset) < 1,
				"marker click to center its exact document line");

			var thumb = Assert.Single(verticalScrollBar.GetVisualDescendants().OfType<Thumb>());
			var thumbBounds = UiTestDriver.GetBoundsInWindow(thumb, window);
			Assert.InRange(Math.Abs(thumbBounds.Center.Y - pointerPoint.Value.Y), 0, 3);

			var offsetBeforeDrag = scrollViewer.Offset.Y;
			var scrollBarBounds = UiTestDriver.GetBoundsInWindow(verticalScrollBar, window);
			window.MouseMove(
				new Point(scrollBarBounds.Center.X, pointerPoint.Value.Y),
				RawInputModifiers.None);
			await UiTestDriver.WaitForSettledFramesAsync(frameCount: 3);
			thumbBounds = UiTestDriver.GetBoundsInWindow(thumb, window);
			var trackBounds = UiTestDriver.GetBoundsInWindow(track, window);
			var thumbDragPoint = new Point(trackBounds.Right - 1, thumbBounds.Center.Y);
			var dragTarget = new Point(thumbDragPoint.X, thumbDragPoint.Y - 40);
			var originalViewerAllowAutoHide = scrollViewer.AllowAutoHide;
			var originalScrollBarAllowAutoHide = verticalScrollBar.AllowAutoHide;
			window.MouseMove(thumbDragPoint, RawInputModifiers.None);
			window.MouseDown(thumbDragPoint, MouseButton.Left, RawInputModifiers.LeftMouseButton);
			window.MouseMove(dragTarget, RawInputModifiers.LeftMouseButton);
			Assert.Equal("Arrow", scrollViewer.Cursor?.ToString());
			Assert.False(scrollViewer.AllowAutoHide);
			Assert.False(verticalScrollBar.AllowAutoHide);
			window.MouseUp(dragTarget, MouseButton.Left, RawInputModifiers.None);
			Assert.Equal(originalViewerAllowAutoHide, scrollViewer.AllowAutoHide);
			Assert.Equal(originalScrollBarAllowAutoHide, verticalScrollBar.AllowAutoHide);
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => scrollViewer.Offset.Y < offsetBeforeDrag - 1,
				"scroll thumb drag to remain interactive where a preview marker overlaps it");
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
		}
	}
}
