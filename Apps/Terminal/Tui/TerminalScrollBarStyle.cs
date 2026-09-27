using System.Drawing;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;

namespace DevProjex.Terminal.Tui;

internal static class TerminalScrollBarStyle
{
	public static void Apply(View view, bool useUnicode, bool vertical, bool horizontal)
	{
		ArgumentNullException.ThrowIfNull(view);
		if (vertical)
			view.ViewportSettings |= ViewportSettingsFlags.HasVerticalScrollBar;
		if (horizontal)
			view.ViewportSettings |= ViewportSettingsFlags.HasHorizontalScrollBar;

		var track = new Rune(useUnicode ? '·' : '.');
		if (vertical)
		{
			view.VerticalScrollBar.SchemeName = TerminalWorkspaceTheme.Secondary;
			view.VerticalScrollBar.DrawingContent += (_, _) =>
				DrawTrack(view.VerticalScrollBar, track, vertical: true);
			view.VerticalScrollBar.Slider.DrawingContent += (_, _) =>
				DrawThumb(view.VerticalScrollBar.Slider, new Rune(useUnicode ? '┃' : '|'));
		}
		if (horizontal)
		{
			view.HorizontalScrollBar.SchemeName = TerminalWorkspaceTheme.Secondary;
			view.HorizontalScrollBar.DrawingContent += (_, _) =>
				DrawTrack(view.HorizontalScrollBar, track, vertical: false);
			view.HorizontalScrollBar.Slider.DrawingContent += (_, _) =>
				DrawThumb(view.HorizontalScrollBar.Slider, new Rune(useUnicode ? '━' : '-'));
		}
		if (vertical)
			view.VerticalScrollBar.VisibleChanged += (_, _) => LayoutScrollBars(view, vertical, horizontal);
		if (horizontal)
			view.HorizontalScrollBar.VisibleChanged += (_, _) => LayoutScrollBars(view, vertical, horizontal);
	}

	// Showing or hiding a scrollbar moves the view's padding, but Terminal.Gui keeps both
	// scrollbar frames computed for the old padding until an unrelated relayout. A horizontal
	// bar that reappears in that state is placed below the viewport and stays invisible.
	private static void LayoutScrollBars(View view, bool vertical, bool horizontal)
	{
		if (vertical)
			view.VerticalScrollBar.Layout();
		if (horizontal)
			view.HorizontalScrollBar.Layout();
	}

	private static void DrawThumb(View slider, Rune glyph)
	{
		slider.SetAttributeForRole(VisualRole.ReadOnly);
		slider.FillRect(new Rectangle(Point.Empty, slider.Viewport.Size), glyph);
	}

	private static void DrawTrack(View scrollBar, Rune glyph, bool vertical)
	{
		scrollBar.SetAttributeForRole(VisualRole.ReadOnly);
		var track = vertical
			? new Rectangle(0, 1, scrollBar.Viewport.Width, Math.Max(0, scrollBar.Viewport.Height - 2))
			: new Rectangle(1, 0, Math.Max(0, scrollBar.Viewport.Width - 2), scrollBar.Viewport.Height);
		if (track.Width > 0 && track.Height > 0)
			scrollBar.FillRect(track, glyph);
	}
}
