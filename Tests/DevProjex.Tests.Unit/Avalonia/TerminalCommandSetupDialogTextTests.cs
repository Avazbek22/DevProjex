using DevProjex.Avalonia.Services;

namespace DevProjex.Tests.Unit.Avalonia;

[Trait("Category", "TerminalCommand")]
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
		if (state == TerminalCommandSetupState.Installed)
			Assert.Empty(text.Details);
		else
			Assert.False(string.IsNullOrWhiteSpace(text.Details));
		Assert.False(text.Body.StartsWith("Dialog.", StringComparison.Ordinal));
		Assert.Equal(canInstall || canRepair, text.ShowInstallButton);
		var expectedCopyButton = state is
			TerminalCommandSetupState.ManagedByOperatingSystem or
			TerminalCommandSetupState.UnsupportedOnCurrentPackage;
		Assert.Equal(expectedCopyButton, text.ShowCopyButton);
		if (state == TerminalCommandSetupState.UnsupportedOnCurrentPackage)
		{
			Assert.Equal("/opt/DevProjex", text.CommandToCopy);
			Assert.Contains("/opt/DevProjex --help", text.CommandLine, StringComparison.Ordinal);
		}
		else if (state == TerminalCommandSetupState.Installed)
		{
			Assert.Equal("devprojex", text.CommandToCopy);
			Assert.Empty(text.CommandLine);
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
	public void Create_InstalledState_HidesTechnicalLauncherPaths()
	{
		var localization = new LocalizationService(new JsonLocalizationCatalog(), AppLanguage.Ru);
		var executablePath = @"C:\Users\me\DevProjex\DevProjex.exe";
		var snapshot = new TerminalCommandSetupSnapshot(
			CommandLineExecutableAliases.UnixCommand,
			TerminalCommandSetupState.Installed,
			CommandPath: @"C:\Users\me\AppData\Local\DevProjex\bin\devprojex.cmd",
			TargetExecutablePath: executablePath,
			InstalledTargetExecutablePath: executablePath,
			UserBinDirectory: @"C:\Users\me\AppData\Local\DevProjex\bin",
			UserBinDirectoryIsInPath: true,
			CanInstall: false,
			CanRepair: false,
			ShellProfileHint: null);

		var text = TerminalCommandSetupDialogText.Create(localization, snapshot);
		var combined = string.Join(Environment.NewLine, text.Body, text.Details, text.CommandLine);

		Assert.Empty(text.Details);
		Assert.Empty(text.CommandLine);
		Assert.False(text.ShowCopyButton);
		Assert.Equal("devprojex", text.CommandToCopy);
		Assert.Contains("devprojex", combined, StringComparison.Ordinal);
		Assert.Contains("devprojex --help", combined, StringComparison.Ordinal);
		Assert.DoesNotContain("C:\\Users\\me", combined, StringComparison.Ordinal);
		Assert.DoesNotContain("Команда для проверки", combined, StringComparison.Ordinal);
		Assert.DoesNotContain("Файл приложения", combined, StringComparison.Ordinal);
		Assert.DoesNotContain("Сейчас указывает", combined, StringComparison.Ordinal);
	}

	[Fact]
	public void Create_AutomaticPrompt_UsesShortUserFacingQuestionWithoutTechnicalDetails()
	{
		var localization = new LocalizationService(new JsonLocalizationCatalog(), AppLanguage.Ru);
		var snapshot = new TerminalCommandSetupSnapshot(
			CommandLineExecutableAliases.UnixCommand,
			TerminalCommandSetupState.NotInstalled,
			CommandPath: @"C:\Users\me\AppData\Local\DevProjex\bin\devprojex.cmd",
			TargetExecutablePath: @"C:\Users\me\DevProjex\DevProjex.exe",
			InstalledTargetExecutablePath: null,
			UserBinDirectory: @"C:\Users\me\AppData\Local\DevProjex\bin",
			UserBinDirectoryIsInPath: false,
			CanInstall: true,
			CanRepair: false,
			ShellProfileHint: "Restart already-open terminal windows after enabling it.");

		var text = TerminalCommandSetupDialogText.Create(localization, snapshot, isAutomaticPrompt: true);
		var combined = string.Join(Environment.NewLine, text.Title, text.Body, text.Details, text.CommandLine);

		Assert.Equal(
			"Сделать команду devprojex доступной в терминале?\n\nПриложение добавит команду devprojex в PATH для вашего пользователя.",
			text.Body);
		Assert.Contains("\n\n", text.Body, StringComparison.Ordinal);
		Assert.Empty(text.Details);
		Assert.Empty(text.CommandLine);
		Assert.True(text.ShowInstallButton);
		Assert.False(text.ShowCopyButton);
		Assert.DoesNotContain("Команда для проверки", combined, StringComparison.Ordinal);
		Assert.DoesNotContain("Файл приложения", combined, StringComparison.Ordinal);
		Assert.DoesNotContain("C:\\Users\\me", combined, StringComparison.Ordinal);
		Assert.DoesNotContain("Состояние:", combined, StringComparison.Ordinal);
	}

	[Fact]
	public void Create_AutomaticRepairPrompt_UsesStaleRepairBodyInsteadOfInstallQuestion()
	{
		var localization = new LocalizationService(new JsonLocalizationCatalog(), AppLanguage.Ru);
		var snapshot = new TerminalCommandSetupSnapshot(
			CommandLineExecutableAliases.UnixCommand,
			TerminalCommandSetupState.Stale,
			CommandPath: @"C:\Users\me\AppData\Local\DevProjex\bin\devprojex.cmd",
			TargetExecutablePath: @"C:\Users\me\DevProjex\DevProjex.exe",
			InstalledTargetExecutablePath: @"C:\Old\DevProjex.exe",
			UserBinDirectory: @"C:\Users\me\AppData\Local\DevProjex\bin",
			UserBinDirectoryIsInPath: true,
			CanInstall: false,
			CanRepair: true,
			ShellProfileHint: null);

		var text = TerminalCommandSetupDialogText.Create(localization, snapshot, isAutomaticPrompt: true);

		Assert.Contains("нужно обновить", text.Body, StringComparison.Ordinal);
		Assert.DoesNotContain("Сделать команду", text.Body, StringComparison.Ordinal);
		Assert.Equal("Исправить", text.InstallButtonText);
		Assert.False(text.ShowCopyButton);
		Assert.Empty(text.CommandLine);
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
