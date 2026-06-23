using DevProjex.Infrastructure.ThemePresets;

namespace DevProjex.Tests.Unit.Avalonia;

public sealed class TerminalCommandAutomaticPromptGateTests
{
	[Fact]
	public void ShouldShowAutomaticTerminalCommandPrompt_ShowsForWindowsPortableReleaseExecutable()
	{
		var snapshot = new TerminalCommandSetupSnapshot(
			CommandLineExecutableAliases.UnixCommand,
			TerminalCommandSetupState.NotInstalled,
			CommandPath: @"C:\Users\me\AppData\Local\DevProjex\bin\devprojex.cmd",
			TargetExecutablePath: @"C:\Users\me\Downloads\DevProjex\DevProjex.exe",
			InstalledTargetExecutablePath: null,
			UserBinDirectory: @"C:\Users\me\AppData\Local\DevProjex\bin",
			UserBinDirectoryIsInPath: false,
			CanInstall: true,
			CanRepair: false,
			ShellProfileHint: null);

		Assert.True(MainWindow.ShouldShowAutomaticTerminalCommandPrompt(
			new AppViewSettings(),
			snapshot,
			startedWithProjectPath: false));
	}

	[Fact]
	public void ShouldShowAutomaticTerminalCommandPrompt_SuppressesWindowsPortableAfterDismissal()
	{
		var snapshot = new TerminalCommandSetupSnapshot(
			CommandLineExecutableAliases.UnixCommand,
			TerminalCommandSetupState.NotInstalled,
			CommandPath: @"C:\Users\me\AppData\Local\DevProjex\bin\devprojex.cmd",
			TargetExecutablePath: @"C:\Users\me\Downloads\DevProjex\DevProjex.exe",
			InstalledTargetExecutablePath: null,
			UserBinDirectory: @"C:\Users\me\AppData\Local\DevProjex\bin",
			UserBinDirectoryIsInPath: false,
			CanInstall: true,
			CanRepair: false,
			ShellProfileHint: null);

		Assert.False(MainWindow.ShouldShowAutomaticTerminalCommandPrompt(
			new AppViewSettings { IsTerminalCommandPromptDismissed = true },
			snapshot,
			startedWithProjectPath: false));
	}

	[Fact]
	public void ShouldShowAutomaticTerminalCommandPrompt_SuppressesTestHostAndDotnetExecutables()
	{
		var snapshot = new TerminalCommandSetupSnapshot(
			CommandLineExecutableAliases.UnixCommand,
			TerminalCommandSetupState.NotInstalled,
			"/home/me/.local/bin/devprojex",
			TargetExecutablePath: @"C:\Program Files\dotnet\dotnet.exe",
			InstalledTargetExecutablePath: null,
			UserBinDirectory: "/home/me/.local/bin",
			UserBinDirectoryIsInPath: true,
			CanInstall: true,
			CanRepair: false,
			ShellProfileHint: null);

		Assert.False(MainWindow.ShouldShowAutomaticTerminalCommandPrompt(
			new AppViewSettings(),
			snapshot,
			startedWithProjectPath: false));
	}

	[Fact]
	public void ShouldShowAutomaticTerminalCommandPrompt_DoesNotShowForWindowsStoreManagedAlias()
	{
		var snapshot = new TerminalCommandSetupSnapshot(
			CommandLineExecutableAliases.WindowsStoreAlias,
			TerminalCommandSetupState.ManagedByOperatingSystem,
			CommandPath: null,
			TargetExecutablePath: null,
			InstalledTargetExecutablePath: null,
			UserBinDirectory: null,
			UserBinDirectoryIsInPath: true,
			CanInstall: false,
			CanRepair: false,
			ShellProfileHint: null);

		Assert.False(MainWindow.ShouldShowAutomaticTerminalCommandPrompt(
			new AppViewSettings(),
			snapshot,
			startedWithProjectPath: false));
	}
}
