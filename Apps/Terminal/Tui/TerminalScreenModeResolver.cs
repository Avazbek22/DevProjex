using DevProjex.Terminal.CommandLine;

namespace DevProjex.Terminal.Tui;

public static class TerminalScreenModeResolver
{
	public static TerminalScreenMode Resolve(
		TerminalScreenMode requested,
		ITerminalEnvironment environment)
	{
		if (requested != TerminalScreenMode.Auto)
			return requested;

		var multiplexed =
			!string.IsNullOrWhiteSpace(environment.Variables.GetValueOrDefault("TMUX")) ||
			!string.IsNullOrWhiteSpace(environment.Variables.GetValueOrDefault("ZELLIJ"));
		return multiplexed || environment.IsCi
			? TerminalScreenMode.Inline
			: TerminalScreenMode.Alternate;
	}
}
