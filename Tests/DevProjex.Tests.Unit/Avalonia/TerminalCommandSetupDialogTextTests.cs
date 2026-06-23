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
		var expectedCopyButton = state is
			TerminalCommandSetupState.ManagedByOperatingSystem or
			TerminalCommandSetupState.UnsupportedOnCurrentPackage or
			TerminalCommandSetupState.Installed;
		Assert.Equal(expectedCopyButton, text.ShowCopyButton);
		if (state == TerminalCommandSetupState.UnsupportedOnCurrentPackage)
		{
			Assert.Equal("/opt/DevProjex", text.CommandToCopy);
			Assert.Contains("/opt/DevProjex --help", text.CommandLine, StringComparison.Ordinal);
		}
		else
		{
			Assert.Equal("devprojex", text.CommandToCopy);
			Assert.Contains("devprojex --help", text.CommandLine, StringComparison.Ordinal);
		}
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
		Assert.Contains("Currently points to", text.Details, StringComparison.Ordinal);
	}

	[Fact]
	public void Create_UnsupportedPackageFallbackUsesExecutablePathAsCopyableCommand()
	{
		var localization = new LocalizationService(new JsonLocalizationCatalog(), AppLanguage.En);
		var executablePath = @"C:\Program Files\DevProjex\DevProjex.exe";
		var snapshot = new TerminalCommandSetupSnapshot(
			CommandLineExecutableAliases.WindowsPortableExecutable,
			TerminalCommandSetupState.UnsupportedOnCurrentPackage,
			CommandPath: null,
			TargetExecutablePath: executablePath,
			InstalledTargetExecutablePath: null,
			UserBinDirectory: null,
			UserBinDirectoryIsInPath: false,
			CanInstall: false,
			CanRepair: false,
			ShellProfileHint: null);

		var text = TerminalCommandSetupDialogText.Create(localization, snapshot);

		Assert.Equal("\"" + executablePath + "\"", text.CommandToCopy);
		Assert.Contains("\"" + executablePath + "\" --help", text.CommandLine, StringComparison.Ordinal);
		Assert.Contains("App file", text.Details, StringComparison.Ordinal);
		Assert.True(text.ShowCopyButton);
		Assert.DoesNotContain("UnsupportedOnCurrentPackage", text.Body, StringComparison.Ordinal);
		Assert.DoesNotContain("State:", text.Details, StringComparison.Ordinal);
		Assert.DoesNotContain("Short command: DevProjex.exe", text.Details, StringComparison.Ordinal);
		Assert.False(text.ShowInstallButton);
	}

	[Fact]
	public void Create_WindowsPortableNotInstalled_UsesInstallActionInsteadOfCopyFallback()
	{
		var localization = new LocalizationService(new JsonLocalizationCatalog(), AppLanguage.En);
		var executablePath = @"C:\Users\me\DevProjex\DevProjex.exe";
		var snapshot = new TerminalCommandSetupSnapshot(
			CommandLineExecutableAliases.UnixCommand,
			TerminalCommandSetupState.NotInstalled,
			CommandPath: @"C:\Users\me\AppData\Local\DevProjex\bin\devprojex.cmd",
			TargetExecutablePath: executablePath,
			InstalledTargetExecutablePath: null,
			UserBinDirectory: @"C:\Users\me\AppData\Local\DevProjex\bin",
			UserBinDirectoryIsInPath: false,
			CanInstall: true,
			CanRepair: false,
			ShellProfileHint: "Restart already-open terminal windows after enabling it.");

		var text = TerminalCommandSetupDialogText.Create(localization, snapshot);

		Assert.Equal("devprojex", text.CommandToCopy);
		Assert.Contains("devprojex --help", text.CommandLine, StringComparison.Ordinal);
		Assert.True(text.ShowInstallButton);
		Assert.False(text.ShowCopyButton);
		Assert.Equal("Enable", text.InstallButtonText);
		Assert.Contains("Command file", text.Details, StringComparison.Ordinal);
	}

	[Fact]
	public void Create_RussianWindowsPortableInstallText_IsUserFacingAndDoesNotExposeInternalState()
	{
		var localization = new LocalizationService(new JsonLocalizationCatalog(), AppLanguage.Ru);
		var executablePath = @"C:\Users\me\DevProjex\DevProjex.exe";
		var snapshot = new TerminalCommandSetupSnapshot(
			CommandLineExecutableAliases.UnixCommand,
			TerminalCommandSetupState.NotInstalled,
			CommandPath: @"C:\Users\me\AppData\Local\DevProjex\bin\devprojex.cmd",
			TargetExecutablePath: executablePath,
			InstalledTargetExecutablePath: null,
			UserBinDirectory: @"C:\Users\me\AppData\Local\DevProjex\bin",
			UserBinDirectoryIsInPath: false,
			CanInstall: true,
			CanRepair: false,
			ShellProfileHint: "Restart already-open terminal windows after enabling it.");

		var text = TerminalCommandSetupDialogText.Create(localization, snapshot);
		var combined = string.Join(Environment.NewLine, text.Title, text.Body, text.Details, text.CommandLine);

		Assert.Contains("Запуск из терминала", combined, StringComparison.Ordinal);
		Assert.Contains("Команда для проверки", combined, StringComparison.Ordinal);
		Assert.Contains("Файл приложения", combined, StringComparison.Ordinal);
		Assert.DoesNotContain("UnsupportedOnCurrentPackage", combined, StringComparison.Ordinal);
		Assert.DoesNotContain("Состояние:", combined, StringComparison.Ordinal);
		Assert.DoesNotContain("executable", combined, StringComparison.OrdinalIgnoreCase);
		Assert.True(text.ShowInstallButton);
		Assert.False(text.ShowCopyButton);
	}
}
