using DevProjex.Infrastructure.ThemePresets;

namespace DevProjex.Tests.Unit.Avalonia;

[Trait("Category", "TerminalCommand")]
public sealed class TerminalCommandAutomaticPromptGateTests
{
	[Theory]
	[InlineData("DevProjex.v4.9.5.win-x64.exe")]
	[InlineData("DevProjex.v4.9.5.win-arm64.exe")]
	[InlineData("DevProjex.v4.9.5.linux-x64.portable")]
	[InlineData("DevProjex.v4.9.5.linux-arm64.portable")]
	[InlineData("DevProjex.v4.9.5.osx-x64")]
	[InlineData("DevProjex.v4.9.5.osx-arm64")]
	[InlineData("DevProjex.v5.0.0-preview.2+build7.linux-x64.portable")]
	public void ShouldShowAutomaticTerminalCommandPrompt_ShowsForEveryPublishedPortableArtifact(string fileName)
	{
		var snapshot = CreateNotInstalledSnapshot(Path.Combine("downloads", fileName));

		Assert.True(MainWindow.ShouldShowAutomaticTerminalCommandPrompt(
			new AppViewSettings(),
			snapshot,
			startedWithProjectPath: false));
	}

	[Theory]
	[InlineData("DevProjex.Tests.Unit.exe")]
	[InlineData("DevProjex.v.win-x64.exe")]
	[InlineData("DevProjex.v4.9.5.freebsd-x64")]
	[InlineData("DevProjex.v4.9.5.win-x64.exe.bak")]
	[InlineData("DevProjex.v4.9.5.linux-x64.tar.gz")]
	[InlineData("DevProjex.v4.9.5.osx-arm64.app.tar.gz")]
	[InlineData("dotnet.exe")]
	public void ShouldShowAutomaticTerminalCommandPrompt_RejectsNonReleaseExecutableNames(string fileName)
	{
		var snapshot = CreateNotInstalledSnapshot(Path.Combine("downloads", fileName));

		Assert.False(MainWindow.ShouldShowAutomaticTerminalCommandPrompt(
			new AppViewSettings(),
			snapshot,
			startedWithProjectPath: false));
	}

	[Fact]
	public void ReleaseScript_OnlyDirectExecutableArtifactsPassExecutableIdentityGate()
	{
		var scriptPath = Path.Combine(FindRepositoryRoot(), "Scripts", "release-all.ps1");
		var script = File.ReadAllText(scriptPath);
		var matches = System.Text.RegularExpressions.Regex.Matches(
			script,
			"Name\\s*=\\s*\"(?<name>DevProjex\\.v\\$version\\.[^\"]+)\"");

		Assert.Equal(6, matches.Count);
		foreach (System.Text.RegularExpressions.Match match in matches)
		{
			var artifactName = match.Groups["name"].Value.Replace("$version", "4.9.5", StringComparison.Ordinal);
			var isDirectExecutable = artifactName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
			Assert.Equal(
				isDirectExecutable,
				CommandLineExecutableAliases.IsPublishedPortableFileName(artifactName));
		}
	}

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
	public void ShouldShowAutomaticTerminalCommandPrompt_ShowsForUnixPublishedExecutable()
	{
		var snapshot = new TerminalCommandSetupSnapshot(
			CommandLineExecutableAliases.UnixCommand,
			TerminalCommandSetupState.NotInstalled,
			CommandPath: "/home/me/.local/bin/devprojex",
			TargetExecutablePath: "/opt/DevProjex/DevProjex",
			InstalledTargetExecutablePath: null,
			UserBinDirectory: "/home/me/.local/bin",
			UserBinDirectoryIsInPath: true,
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
	public void ResolveAutomaticTerminalCommandStartupAction_RepairsStaleCommandSilently()
	{
		var snapshot = new TerminalCommandSetupSnapshot(
			CommandLineExecutableAliases.UnixCommand,
			TerminalCommandSetupState.Stale,
			CommandPath: @"C:\Users\me\AppData\Local\DevProjex\bin\devprojex.cmd",
			TargetExecutablePath: @"C:\Users\me\Downloads\DevProjex\DevProjex.exe",
			InstalledTargetExecutablePath: @"C:\Users\me\Downloads\DevProjex\Old\DevProjex.exe",
			UserBinDirectory: @"C:\Users\me\AppData\Local\DevProjex\bin",
			UserBinDirectoryIsInPath: true,
			CanInstall: false,
			CanRepair: true,
			ShellProfileHint: null);

		Assert.False(MainWindow.ShouldShowAutomaticTerminalCommandPrompt(
			new AppViewSettings { IsTerminalCommandPromptDismissed = true },
			snapshot,
			startedWithProjectPath: false));
		Assert.Equal(
			MainWindow.AutomaticTerminalCommandStartupAction.RepairSilently,
			MainWindow.ResolveAutomaticTerminalCommandStartupAction(
				new AppViewSettings { IsTerminalCommandPromptDismissed = true },
				snapshot,
				startedWithProjectPath: false));
	}

	[Fact]
	public void ShouldShowAutomaticTerminalCommandPrompt_ShowsForInstalledUnixLauncherMissingFromPath()
	{
		var snapshot = new TerminalCommandSetupSnapshot(
			CommandLineExecutableAliases.UnixCommand,
			TerminalCommandSetupState.InstalledPathMissing,
			CommandPath: "/home/me/.local/bin/devprojex",
			TargetExecutablePath: "/opt/DevProjex/DevProjex",
			InstalledTargetExecutablePath: "/opt/DevProjex/DevProjex",
			UserBinDirectory: "/home/me/.local/bin",
			UserBinDirectoryIsInPath: false,
			CanInstall: false,
			CanRepair: false,
			ShellProfileHint: "Add ~/.local/bin to PATH.",
			PathSetupCommand: "fish_add_path $HOME/.local/bin");

		Assert.True(MainWindow.ShouldShowAutomaticTerminalCommandPrompt(
			new AppViewSettings(),
			snapshot,
			startedWithProjectPath: false));
		Assert.False(MainWindow.ShouldShowAutomaticTerminalCommandPrompt(
			new AppViewSettings { IsTerminalCommandPromptDismissed = true },
			snapshot,
			startedWithProjectPath: false));
	}

	[Fact]
	public void ResolveAutomaticTerminalCommandStartupAction_DoesNotRepairStaleTestHost()
	{
		var snapshot = new TerminalCommandSetupSnapshot(
			CommandLineExecutableAliases.UnixCommand,
			TerminalCommandSetupState.Stale,
			CommandPath: @"C:\Users\me\AppData\Local\DevProjex\bin\devprojex.cmd",
			TargetExecutablePath: @"C:\Program Files\dotnet\dotnet.exe",
			InstalledTargetExecutablePath: @"C:\Users\me\Downloads\DevProjex\DevProjex.exe",
			UserBinDirectory: @"C:\Users\me\AppData\Local\DevProjex\bin",
			UserBinDirectoryIsInPath: true,
			CanInstall: false,
			CanRepair: true,
			ShellProfileHint: null);

		Assert.Equal(
			MainWindow.AutomaticTerminalCommandStartupAction.None,
			MainWindow.ResolveAutomaticTerminalCommandStartupAction(
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

	private static TerminalCommandSetupSnapshot CreateNotInstalledSnapshot(string targetPath) =>
		new(
			CommandLineExecutableAliases.UnixCommand,
			TerminalCommandSetupState.NotInstalled,
			CommandPath: "/home/me/.local/bin/devprojex",
			TargetExecutablePath: targetPath,
			InstalledTargetExecutablePath: null,
			UserBinDirectory: "/home/me/.local/bin",
			UserBinDirectoryIsInPath: false,
			CanInstall: true,
			CanRepair: false,
			ShellProfileHint: null);

	private static string FindRepositoryRoot()
	{
		var directory = AppContext.BaseDirectory;
		while (directory is not null)
		{
			if (File.Exists(Path.Combine(directory, "DevProjex.sln")))
				return directory;

			directory = Directory.GetParent(directory)?.FullName;
		}

		throw new InvalidOperationException("Repository root not found.");
	}
}
