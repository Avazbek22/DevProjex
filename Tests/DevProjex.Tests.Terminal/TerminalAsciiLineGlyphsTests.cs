using System.Drawing;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;

namespace DevProjex.Tests.Terminal;

// Terminal.Gui line glyphs are process-wide, so these tests must not overlap other tests.
[Collection(EnvironmentVariableCollection.Name)]
public sealed class TerminalAsciiLineGlyphsTests
{
	[Fact]
	public void FramesUseAsciiGlyphsWhileActiveAndUnicodeGlyphsAfterwards()
	{
		string ascii;
		using (new TerminalAsciiLineGlyphs())
			ascii = DrawDividedFrame();

		Assert.Equal(
			string.Join(
				Environment.NewLine,
				"+--+",
				"|  |",
				"+--+",
				"|  |",
				"+--+"),
			ascii);
		Assert.Equal(
			string.Join(
				Environment.NewLine,
				"┌──┐",
				"│  │",
				"├──┤",
				"│  │",
				"└──┘"),
			DrawDividedFrame());
	}

	private static string DrawDividedFrame()
	{
		var canvas = new LineCanvas();
		canvas.AddLine(new Point(0, 0), 4, Orientation.Horizontal, LineStyle.Single);
		canvas.AddLine(new Point(0, 2), 4, Orientation.Horizontal, LineStyle.Single);
		canvas.AddLine(new Point(0, 4), 4, Orientation.Horizontal, LineStyle.Single);
		canvas.AddLine(new Point(0, 0), 5, Orientation.Vertical, LineStyle.Single);
		canvas.AddLine(new Point(3, 0), 5, Orientation.Vertical, LineStyle.Single);
		return canvas.ToString();
	}
}
