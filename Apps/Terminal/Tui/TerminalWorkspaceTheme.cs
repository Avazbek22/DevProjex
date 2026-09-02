using Terminal.Gui.Configuration;
using Terminal.Gui.Drawing;
using TerminalAttribute = global::Terminal.Gui.Drawing.Attribute;

namespace DevProjex.Terminal.Tui;

internal static class TerminalWorkspaceTheme
{
	public const string Base = "DevProjexBase";
	public const string Panel = "DevProjexPanel";
	public const string FocusedPanel = "DevProjexFocusedPanel";
	public const string List = "DevProjexList";
	public const string InactiveList = "DevProjexInactiveList";
	public const string Accent = "DevProjexAccent";
	public const string Secondary = "DevProjexSecondary";
	public const string Success = "DevProjexSuccess";
	public const string Warning = "DevProjexWarning";
	public const string Error = "DevProjexError";
	public const string Dialog = "DevProjexDialog";

	public static void Register(bool monochrome)
	{
		if (monochrome)
		{
			RegisterMonochrome();
			return;
		}

		var terminal = new TerminalAttribute(Color.None, Color.None);
		var accent = new TerminalAttribute(new Color("#4AA3FF"), Color.None, TextStyle.Bold);
		var secondary = new TerminalAttribute(Color.DarkGray, Color.None);
		var selection = new TerminalAttribute(Color.White, new Color("#164E78"), TextStyle.Bold);
		var subtleFocus = new TerminalAttribute(new Color("#74B9FF"), Color.None, TextStyle.Bold);

		Add(Base, new Scheme(terminal)
		{
			Focus = terminal,
			HotNormal = accent,
			HotFocus = selection,
			ReadOnly = terminal,
			Disabled = secondary
		});
		Add(Panel, new Scheme(secondary)
		{
			Focus = secondary,
			HotNormal = secondary,
			HotFocus = subtleFocus,
			ReadOnly = secondary,
			Disabled = secondary
		});
		Add(FocusedPanel, new Scheme(subtleFocus)
		{
			Focus = subtleFocus,
			HotNormal = accent,
			HotFocus = accent,
			ReadOnly = subtleFocus,
			Disabled = secondary
		});
		Add(List, new Scheme(terminal)
		{
			Focus = selection,
			Active = selection,
			Highlight = selection,
			HotNormal = accent,
			HotFocus = selection,
			ReadOnly = terminal,
			Disabled = secondary
		});
		Add(InactiveList, new Scheme(terminal)
		{
			Focus = terminal,
			Active = terminal,
			Highlight = terminal,
			HotNormal = accent,
			HotFocus = accent,
			ReadOnly = terminal,
			Disabled = secondary
		});
		Add(Accent, new Scheme(accent)
		{
			Focus = accent,
			HotNormal = accent,
			HotFocus = accent,
			ReadOnly = accent,
			Disabled = secondary
		});
		Add(Secondary, new Scheme(secondary)
		{
			Focus = secondary,
			HotNormal = secondary,
			HotFocus = secondary,
			ReadOnly = secondary,
			Disabled = secondary
		});
		Add(Success, StatusScheme(Color.BrightGreen, terminal, selection, secondary));
		Add(Warning, StatusScheme(Color.BrightYellow, terminal, selection, secondary));
		Add(Error, StatusScheme(Color.BrightRed, terminal, selection, secondary));
		Add(Dialog, new Scheme(terminal)
		{
			Focus = selection,
			Active = selection,
			Highlight = selection,
			HotNormal = accent,
			HotFocus = selection,
			ReadOnly = terminal,
			Disabled = secondary
		});
	}

	private static Scheme StatusScheme(
		Color color,
		TerminalAttribute terminal,
		TerminalAttribute selection,
		TerminalAttribute secondary)
	{
		var normal = new TerminalAttribute(color, Color.None, TextStyle.Bold);
		return new Scheme(normal)
		{
			Focus = selection,
			HotNormal = normal,
			HotFocus = selection,
			ReadOnly = normal,
			Disabled = secondary
		};
	}

	private static void RegisterMonochrome()
	{
		var terminal = new TerminalAttribute(Color.None, Color.None);
		var bold = new TerminalAttribute(Color.None, Color.None, TextStyle.Bold);
		var dim = new TerminalAttribute(Color.None, Color.None, TextStyle.Faint);
		var selection = new TerminalAttribute(Color.None, Color.None, TextStyle.Reverse | TextStyle.Bold);

		Add(Base, new Scheme(terminal)
		{
			Focus = terminal,
			HotNormal = bold,
			HotFocus = selection,
			ReadOnly = terminal,
			Disabled = dim
		});
		Add(Panel, new Scheme(dim) { Focus = dim, HotNormal = dim, HotFocus = bold, ReadOnly = dim });
		Add(FocusedPanel, new Scheme(bold) { Focus = bold, HotNormal = bold, HotFocus = selection, ReadOnly = bold });
		Add(List, new Scheme(terminal)
		{
			Focus = selection,
			Active = selection,
			Highlight = selection,
			HotNormal = bold,
			HotFocus = selection,
			ReadOnly = terminal,
			Disabled = dim
		});
		Add(InactiveList, new Scheme(terminal)
		{
			Focus = terminal,
			Active = terminal,
			Highlight = terminal,
			HotNormal = bold,
			HotFocus = bold,
			ReadOnly = terminal,
			Disabled = dim
		});
		Add(Accent, new Scheme(bold) { Focus = bold, HotNormal = bold, HotFocus = selection, ReadOnly = bold });
		Add(Secondary, new Scheme(dim) { Focus = dim, HotNormal = dim, HotFocus = dim, ReadOnly = dim });
		Add(Success, new Scheme(bold) { Focus = selection, HotNormal = bold, HotFocus = selection, ReadOnly = bold });
		Add(Warning, new Scheme(bold) { Focus = selection, HotNormal = bold, HotFocus = selection, ReadOnly = bold });
		Add(Error, new Scheme(bold) { Focus = selection, HotNormal = bold, HotFocus = selection, ReadOnly = bold });
		Add(Dialog, new Scheme(terminal)
		{
			Focus = selection,
			Active = selection,
			Highlight = selection,
			HotNormal = bold,
			HotFocus = selection,
			ReadOnly = terminal,
			Disabled = dim
		});
	}

	private static void Add(string name, Scheme scheme) =>
		SchemeManager.AddScheme(name, scheme);
}
