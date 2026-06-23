using DevProjex.Infrastructure.ThemePresets;

namespace DevProjex.Infrastructure.TerminalCommands;

public static class TerminalCommandPromptPolicy
{
	public static bool IsDismissibleAutomaticPrompt(TerminalCommandSetupSnapshot snapshot) =>
		snapshot.State is TerminalCommandSetupState.NotInstalled;

	public static bool ShouldOfferAutomaticPrompt(
		AppViewSettings settings,
		TerminalCommandSetupSnapshot snapshot,
		bool startedWithProjectPath)
	{
		if (startedWithProjectPath)
			return false;

		return snapshot.State switch
		{
			TerminalCommandSetupState.Stale => snapshot.CanRepair,
			TerminalCommandSetupState.NotInstalled => snapshot.CanInstall && !settings.IsTerminalCommandPromptDismissed,
			_ => false
		};
	}
}
