using DevProjex.Terminal.Rendering;

namespace DevProjex.Terminal.CommandLine;

internal static class TerminalTuiInteractivityGate
{
	public static bool TryEnter(
		ITerminalEnvironment environment,
		LocalizationService localization)
	{
		if (environment.IsInputInteractive &&
		    environment.IsOutputInteractive &&
		    !environment.IsTermDumb)
		{
			return true;
		}

		environment.Error.WriteLine(TerminalErrorHeader.Format(localization, "DPX-TUI-NOT-INTERACTIVE"));
		environment.Error.WriteLine(localization["Terminal.Tui.Error.NotInteractive"]);
		environment.Error.WriteLine(localization["Terminal.Tui.Hint.DirectCommands"]);
		return false;
	}
}
