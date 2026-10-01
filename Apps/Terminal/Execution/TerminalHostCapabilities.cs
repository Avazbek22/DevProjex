namespace DevProjex.Terminal.Execution;

public sealed record TerminalHostCapabilities(bool HasDesktopApplication)
{
	public static TerminalHostCapabilities Desktop { get; } = new(true);
	public static TerminalHostCapabilities Headless { get; } = new(false);
}
