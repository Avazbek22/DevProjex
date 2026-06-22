using DevProjex.Avalonia.Services;

namespace DevProjex.Tests.Unit.Avalonia;

public sealed class TerminalCommandSetupDialogTextTests
{
	[Theory]
	[InlineData(TerminalCommandSetupState.ManagedByOperatingSystem, false, false)]
	[InlineData(TerminalCommandSetupState.UnsupportedOnCurrentPackage, false, false)]
	[InlineData(TerminalCommandSetupState.UnsupportedOnCurrentPlatform, false, false)]
	[InlineData(TerminalCommandSetupState.HomeDirectoryUnavailable, false, false)]
	[InlineData(TerminalCommandSetupState.NotInstalled, true, false)]
	[InlineData(TerminalCommandSetupState.Installed, false, false)]
	[InlineData(TerminalCommandSetupState.Stale, false, true)]
	[InlineData(TerminalCommandSetupState.ConflictingCommand, false, false)]
	[InlineData(TerminalCommandSetupState.PermissionDenied, false, false)]
	[InlineData(TerminalCommandSetupState.Failed, false, false)]
	public void Create_MapsEveryTerminalCommandStateToNonEmptyUserText(
		TerminalCommandSetupState state,
		bool canInstall,
		bool canRepair)
	{
		var localization = new LocalizationService(new JsonLocalizationCatalog(), AppLanguage.En);
		var snapshot = new TerminalCommandSetupSnapshot(
			CommandLineExecutableAliases.UnixCommand,
			state,
			"/home/me/.local/bin/devprojex",
			"/opt/DevProjex",
			state == TerminalCommandSetupState.Stale ? "/old/DevProjex" : null,
			"/home/me/.local/bin",
			UserBinDirectoryIsInPath: false,
			CanInstall: canInstall,
			CanRepair: canRepair,
			ShellProfileHint: "Add ~/.local/bin to PATH.");

		var text = TerminalCommandSetupDialogText.Create(localization, snapshot);

		Assert.False(string.IsNullOrWhiteSpace(text.Title));
		Assert.False(string.IsNullOrWhiteSpace(text.Body));
		Assert.False(string.IsNullOrWhiteSpace(text.Details));
		Assert.False(text.Body.StartsWith("Dialog.", StringComparison.Ordinal));
		Assert.Equal(canInstall || canRepair, text.ShowInstallButton);
		Assert.Contains("devprojex --help", text.CommandLine, StringComparison.Ordinal);
	}

	[Fact]
	public void Create_UsesRepairLabelForStaleWrapper()
	{
		var localization = new LocalizationService(new JsonLocalizationCatalog(), AppLanguage.En);
		var snapshot = new TerminalCommandSetupSnapshot(
			CommandLineExecutableAliases.UnixCommand,
			TerminalCommandSetupState.Stale,
			"/home/me/.local/bin/devprojex",
			"/opt/DevProjex",
			"/old/DevProjex",
			"/home/me/.local/bin",
			UserBinDirectoryIsInPath: true,
			CanInstall: false,
			CanRepair: true,
			ShellProfileHint: null);

		var text = TerminalCommandSetupDialogText.Create(localization, snapshot);

		Assert.Equal("Repair", text.InstallButtonText);
		Assert.Contains("Installed target", text.Details, StringComparison.Ordinal);
	}
}
