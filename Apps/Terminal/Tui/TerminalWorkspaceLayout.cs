namespace DevProjex.Terminal.Tui;

public enum TerminalWorkspaceLayoutMode
{
	TooSmall,
	Compact,
	Tabbed,
	Split,
	Wide
}

public static class TerminalWorkspaceLayout
{
	public static TerminalWorkspaceLayoutMode Resolve(int width, int height)
	{
		if (width < 60 || height < 20)
			return TerminalWorkspaceLayoutMode.TooSmall;
		if (width < 80)
			return TerminalWorkspaceLayoutMode.Compact;
		if (width < 120)
			return TerminalWorkspaceLayoutMode.Tabbed;
		if (height < 28)
			return TerminalWorkspaceLayoutMode.Split;
		if (width < 150)
			return TerminalWorkspaceLayoutMode.Split;
		return TerminalWorkspaceLayoutMode.Wide;
	}
}
