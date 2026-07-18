using DevProjex.Avalonia.Services;

namespace DevProjex.Tests.Unit.Avalonia;

[Trait("Category", "TerminalCommand")]
public sealed class TerminalCommandPromptDismissalTests
{
	[Theory]
	[InlineData((int)TerminalCommandDialogAction.None, false, false)]
	[InlineData((int)TerminalCommandDialogAction.None, true, true)]
	[InlineData((int)TerminalCommandDialogAction.DismissPrompt, false, true)]
	[InlineData((int)TerminalCommandDialogAction.DismissPrompt, true, true)]
	[InlineData((int)TerminalCommandDialogAction.InstallOrRepair, false, false)]
	[InlineData((int)TerminalCommandDialogAction.InstallOrRepair, true, false)]
	[InlineData((int)TerminalCommandDialogAction.Reinstall, false, false)]
	[InlineData((int)TerminalCommandDialogAction.Reinstall, true, false)]
	[InlineData((int)TerminalCommandDialogAction.ConfigurePath, false, false)]
	[InlineData((int)TerminalCommandDialogAction.ConfigurePath, true, false)]
	public void ShouldPersistTerminalCommandPromptDismissal_OnlyPersistsRealDismissals(
		int actionValue,
		bool dontShowAgain,
		bool expected)
	{
		var action = (TerminalCommandDialogAction)actionValue;
		var result = new TerminalCommandDialogResult(action, dontShowAgain);

		Assert.Equal(expected, MainWindow.ShouldPersistTerminalCommandPromptDismissal(result));
	}

	[Theory]
	[InlineData((int)TerminalCommandInstallOutcome.AlreadyInstalled)]
	[InlineData((int)TerminalCommandInstallOutcome.Created)]
	[InlineData((int)TerminalCommandInstallOutcome.Repaired)]
	[InlineData((int)TerminalCommandInstallOutcome.Reinstalled)]
	public void ResolveTerminalCommandPostInstallUiAction_SuccessDoesNotShowFollowUpDialog(int outcomeValue)
	{
		var result = new TerminalCommandInstallResult(
			Success: true,
			Outcome: (TerminalCommandInstallOutcome)outcomeValue,
			Snapshot: CreateInstalledSnapshot());

		var action = MainWindow.ResolveTerminalCommandPostInstallUiAction(result);

		Assert.Equal(MainWindow.TerminalCommandPostInstallUiAction.None, action);
	}

	[Theory]
	[InlineData((int)TerminalCommandInstallOutcome.NotSupported)]
	[InlineData((int)TerminalCommandInstallOutcome.ConflictingCommand)]
	[InlineData((int)TerminalCommandInstallOutcome.Failed)]
	public void ResolveTerminalCommandPostInstallUiAction_FailureShowsErrorOnly(int outcomeValue)
	{
		var result = new TerminalCommandInstallResult(
			Success: false,
			Outcome: (TerminalCommandInstallOutcome)outcomeValue,
			Snapshot: CreateInstalledSnapshot(),
			ErrorMessage: "Synthetic failure.");

		var action = MainWindow.ResolveTerminalCommandPostInstallUiAction(result);

		Assert.Equal(MainWindow.TerminalCommandPostInstallUiAction.ShowError, action);
	}

	[Theory]
	[InlineData(TerminalCommandSetupState.InstalledPathMissing, true)]
	[InlineData(TerminalCommandSetupState.CommandShadowed, true)]
	[InlineData(TerminalCommandSetupState.Installed, false)]
	[InlineData(TerminalCommandSetupState.Stale, false)]
	public void RequiresTerminalCommandPathConfiguration_ContinuesOnlyForRecoverablePathStates(
		TerminalCommandSetupState state,
		bool expected)
	{
		var snapshot = CreateInstalledSnapshot() with { State = state };

		Assert.Equal(expected, MainWindow.RequiresTerminalCommandPathConfiguration(snapshot));
	}

	private static TerminalCommandSetupSnapshot CreateInstalledSnapshot() =>
		new(
			CommandLineExecutableAliases.UnixCommand,
			TerminalCommandSetupState.Installed,
			CommandPath: "/home/me/.local/bin/devprojex",
			TargetExecutablePath: "/opt/DevProjex/DevProjex",
			InstalledTargetExecutablePath: "/opt/DevProjex/DevProjex",
			UserBinDirectory: "/home/me/.local/bin",
			UserBinDirectoryIsInPath: true,
			CanInstall: false,
			CanRepair: false,
			ShellProfileHint: null);
}
