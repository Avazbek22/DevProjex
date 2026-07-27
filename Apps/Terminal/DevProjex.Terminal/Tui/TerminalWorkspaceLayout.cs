namespace DevProjex.Terminal.Tui;

public enum TerminalWorkspaceLayoutMode
{
	TooSmall,
	Compact,
	Tabbed,
	Split
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
		return TerminalWorkspaceLayoutMode.Split;
	}
}
