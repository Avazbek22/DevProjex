using DevProjex.Infrastructure.ThemePresets;

namespace DevProjex.Infrastructure.TerminalCommands;

public static class TerminalCommandPromptPolicy
{
	public static bool IsDismissibleAutomaticPrompt(TerminalCommandSetupSnapshot snapshot) =>
		snapshot.State is TerminalCommandSetupState.NotInstalled or
			TerminalCommandSetupState.InstalledPathMissing or
			TerminalCommandSetupState.CommandShadowed;

	public static bool ShouldRepairAutomatically(TerminalCommandSetupSnapshot snapshot) =>
		snapshot.State == TerminalCommandSetupState.Stale && snapshot.CanRepair;

	public static bool ShouldOfferAutomaticPrompt(
		AppViewSettings settings,
		TerminalCommandSetupSnapshot snapshot,
		bool startedWithProjectPath)
	{
		if (startedWithProjectPath)
			return false;

		return snapshot.State switch
		{
			TerminalCommandSetupState.NotInstalled => snapshot.CanInstall && !settings.IsTerminalCommandPromptDismissed,
			TerminalCommandSetupState.InstalledPathMissing => !settings.IsTerminalCommandPromptDismissed,
			TerminalCommandSetupState.CommandShadowed => !settings.IsTerminalCommandPromptDismissed,
			_ => false
		};
	}
}
