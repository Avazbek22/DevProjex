using Terminal.Gui.Views;

namespace DevProjex.Terminal.Tui;

// Terminal.Gui otherwise treats '_' in dynamic paths, URLs, and names as a mnemonic marker.
internal sealed class TerminalLiteralLabel : Label
{
	public TerminalLiteralLabel()
	{
		HotKeySpecifier = new Rune('\uffff');
	}
}

internal sealed class TerminalLiteralFrameView : FrameView
{
	public TerminalLiteralFrameView()
	{
		HotKeySpecifier = new Rune('\uffff');
	}
}
