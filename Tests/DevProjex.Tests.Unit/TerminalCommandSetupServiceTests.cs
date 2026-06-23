using DevProjex.Infrastructure.TerminalCommands;
using DevProjex.Infrastructure.ThemePresets;

namespace DevProjex.Tests.Unit;

public sealed class TerminalCommandSetupServiceTests
{
	[Fact]
	public void Probe_WindowsPackagedApp_ReportsOperatingSystemManagedAlias()
	{
		var service = CreateService(
			TerminalCommandHostPlatform.Windows,
			home: null,
			pathValue: null,
			executablePath: @"C:\Program Files\WindowsApps\DevProjex\DevProjex.exe",
			isWindowsPackaged: true);

		var snapshot = service.Probe();

		Assert.Equal(TerminalCommandSetupState.ManagedByOperatingSystem, snapshot.State);
		Assert.Equal(CommandLineExecutableAliases.WindowsStoreAlias, snapshot.CommandName);
		Assert.True(snapshot.IsReady);
		Assert.False(snapshot.IsActionable);
		Assert.Null(snapshot.CommandPath);
	}

	[Fact]
	public void Probe_WindowsPortableBuild_DoesNotOfferPathMutation()
	{
		var service = CreateService(
			TerminalCommandHostPlatform.Windows,
			home: null,
			pathValue: null,
			executablePath: @"C:\Tools\DevProjex\DevProjex.exe",
			isWindowsPackaged: false);

		var snapshot = service.Probe();
		var install = service.InstallOrRepair();

		Assert.Equal(TerminalCommandSetupState.UnsupportedOnCurrentPackage, snapshot.State);
		Assert.False(snapshot.CanInstall);
		Assert.False(snapshot.CanRepair);
		Assert.False(install.Success);
		Assert.Equal(TerminalCommandInstallOutcome.NotSupported, install.Outcome);
	}

	[Fact]
	public void Probe_UnixMissingCommand_ReturnsActionableNotInstalledState()
	{
		using var temp = new TemporaryDirectory();
		var target = temp.CreateFile("app/DevProjex", "fake executable");
		var userBin = Path.Combine(temp.Path, ".local", "bin");
		var service = CreateService(
			TerminalCommandHostPlatform.Linux,
			temp.Path,
			userBin,
			target);

		var snapshot = service.Probe();

		Assert.Equal(TerminalCommandSetupState.NotInstalled, snapshot.State);
		Assert.True(snapshot.CanInstall);
		Assert.False(snapshot.CanRepair);
		Assert.True(snapshot.UserBinDirectoryIsInPath);
		Assert.Equal(Path.Combine(userBin, CommandLineExecutableAliases.UnixCommand), snapshot.CommandPath);
	}

	[Fact]
	public void Probe_UnixMissingCommand_WhenUserBinMissingFromPath_KeepsInstallActionableAndReturnsHint()
	{
		using var temp = new TemporaryDirectory();
		var target = temp.CreateFile("app/DevProjex", "fake executable");
		var service = CreateService(
			TerminalCommandHostPlatform.MacOS,
			temp.Path,
			Path.Combine(temp.Path, "other-bin"),
			target);

		var snapshot = service.Probe();

		Assert.Equal(TerminalCommandSetupState.NotInstalled, snapshot.State);
		Assert.True(snapshot.CanInstall);
		Assert.False(snapshot.UserBinDirectoryIsInPath);
		Assert.Contains(".local/bin", snapshot.ShellProfileHint, StringComparison.Ordinal);
	}

	[Fact]
	public void Probe_UnixPathLookup_UsesColonSeparatorAndNormalizesTrailingSlashes()
	{
		using var temp = new TemporaryDirectory();
		var target = temp.CreateFile("app/DevProjex", "fake executable");
		var userBin = Path.Combine(temp.Path, ".local", "bin");
		var pathValue = string.Join(':', "/usr/bin", userBin + Path.DirectorySeparatorChar, "/bin");
		var service = new TerminalCommandSetupService(new TerminalCommandSetupServiceOptions
		{
			Platform = TerminalCommandHostPlatform.Linux,
			HomeDirectoryProvider = () => temp.Path,
			PathVariableProvider = () => pathValue,
			ExecutablePathProvider = () => target
		});

		var snapshot = service.Probe();

		Assert.True(snapshot.UserBinDirectoryIsInPath);
		Assert.Null(snapshot.ShellProfileHint);
	}

	[Fact]
	public void InstallOrRepair_UnixMissingCommand_CreatesManagedWrapperAndProbeBecomesInstalled()
	{
		using var temp = new TemporaryDirectory();
		var target = temp.CreateFile("apps/Dev Projex", "fake executable");
		var userBin = Path.Combine(temp.Path, ".local", "bin");
		var service = CreateService(TerminalCommandHostPlatform.Linux, temp.Path, userBin, target);

		var result = service.InstallOrRepair();
		var wrapperPath = Path.Combine(userBin, CommandLineExecutableAliases.UnixCommand);
		var wrapper = File.ReadAllText(wrapperPath);
		var snapshot = service.Probe();

		Assert.True(result.Success);
		Assert.Equal(TerminalCommandInstallOutcome.Created, result.Outcome);
		Assert.Equal(TerminalCommandSetupState.Installed, snapshot.State);
		Assert.StartsWith("#!/bin/sh", wrapper, StringComparison.Ordinal);
		Assert.Contains("# DevProjex terminal command wrapper", wrapper, StringComparison.Ordinal);
		Assert.Contains("# target: " + target, wrapper, StringComparison.Ordinal);
		Assert.Contains("exec '" + target + "' \"$@\"", wrapper, StringComparison.Ordinal);
	}

	[Fact]
	public void Probe_UnixCommandPathOccupiedByDirectory_ReturnsConflict()
	{
		using var temp = new TemporaryDirectory();
		var target = temp.CreateFile("app/DevProjex", "fake executable");
		temp.CreateFolder(Path.Combine(".local", "bin", CommandLineExecutableAliases.UnixCommand));
		var userBin = Path.Combine(temp.Path, ".local", "bin");
		var service = CreateService(TerminalCommandHostPlatform.Linux, temp.Path, userBin, target);

		var snapshot = service.Probe();
		var install = service.InstallOrRepair();

		Assert.Equal(TerminalCommandSetupState.ConflictingCommand, snapshot.State);
		Assert.False(snapshot.IsActionable);
		Assert.False(install.Success);
		Assert.Equal(TerminalCommandInstallOutcome.ConflictingCommand, install.Outcome);
	}

	[Fact]
	public void Probe_UnixExistingManagedWrapperWithCurrentTarget_ReturnsInstalled()
	{
		using var temp = new TemporaryDirectory();
		var target = temp.CreateFile("app/DevProjex", "fake executable");
		var userBin = temp.CreateFolder(".local/bin");
		var wrapperPath = Path.Combine(userBin, CommandLineExecutableAliases.UnixCommand);
		File.WriteAllText(wrapperPath, TerminalCommandSetupService.BuildWrapperContent(target));
		var service = CreateService(TerminalCommandHostPlatform.Linux, temp.Path, userBin, target);

		var snapshot = service.Probe();
		var install = service.InstallOrRepair();

		Assert.Equal(TerminalCommandSetupState.Installed, snapshot.State);
		Assert.True(snapshot.IsReady);
		Assert.True(install.Success);
		Assert.Equal(TerminalCommandInstallOutcome.AlreadyInstalled, install.Outcome);
	}

	[Fact]
	public void Probe_UnixLegacyManagedWrapperWithoutShebang_IsStillRecognized()
	{
		using var temp = new TemporaryDirectory();
		var target = temp.CreateFile("app/DevProjex", "fake executable");
		var userBin = temp.CreateFolder(".local/bin");
		var wrapperPath = Path.Combine(userBin, CommandLineExecutableAliases.UnixCommand);
		File.WriteAllText(
			wrapperPath,
			"# DevProjex terminal command wrapper\n# target: " + target + "\nexec '" + target + "' \"$@\"\n");
		var service = CreateService(TerminalCommandHostPlatform.Linux, temp.Path, userBin, target);

		var snapshot = service.Probe();

		Assert.Equal(TerminalCommandSetupState.Installed, snapshot.State);
		Assert.True(snapshot.IsReady);
	}

	[Fact]
	public void Probe_UnixMissingCurrentExecutable_DisablesInstallInsteadOfCreatingBrokenWrapper()
	{
		using var temp = new TemporaryDirectory();
		var missingTarget = Path.Combine(temp.Path, "missing", "DevProjex");
		var userBin = Path.Combine(temp.Path, ".local", "bin");
		var service = CreateService(TerminalCommandHostPlatform.Linux, temp.Path, userBin, missingTarget);

		var snapshot = service.Probe();
		var install = service.InstallOrRepair();

		Assert.Equal(TerminalCommandSetupState.Failed, snapshot.State);
		Assert.False(snapshot.IsActionable);
		Assert.False(install.Success);
		Assert.False(File.Exists(Path.Combine(userBin, CommandLineExecutableAliases.UnixCommand)));
	}

	[Fact]
	public void Probe_UnixManagedWrapperWithMissingTarget_ReturnsStaleAndRepairable()
	{
		using var temp = new TemporaryDirectory();
		var currentTarget = temp.CreateFile("app/current/DevProjex", "fake executable");
		var missingTarget = Path.Combine(temp.Path, "app", "old", "DevProjex");
		var userBin = temp.CreateFolder(".local/bin");
		File.WriteAllText(
			Path.Combine(userBin, CommandLineExecutableAliases.UnixCommand),
			TerminalCommandSetupService.BuildWrapperContent(missingTarget));
		var service = CreateService(TerminalCommandHostPlatform.Linux, temp.Path, userBin, currentTarget);

		var snapshot = service.Probe();

		Assert.Equal(TerminalCommandSetupState.Stale, snapshot.State);
		Assert.True(snapshot.CanRepair);
		Assert.Equal(missingTarget, snapshot.InstalledTargetExecutablePath);
	}

	[Fact]
	public void InstallOrRepair_UnixStaleWrapper_ReplacesTargetWithCurrentExecutable()
	{
		using var temp = new TemporaryDirectory();
		var currentTarget = temp.CreateFile("current/DevProjex", "fake executable");
		var oldTarget = temp.CreateFile("old/DevProjex", "old executable");
		var userBin = temp.CreateFolder(".local/bin");
		var commandPath = Path.Combine(userBin, CommandLineExecutableAliases.UnixCommand);
		File.WriteAllText(commandPath, TerminalCommandSetupService.BuildWrapperContent(oldTarget));
		var service = CreateService(TerminalCommandHostPlatform.MacOS, temp.Path, userBin, currentTarget);

		var result = service.InstallOrRepair();
		var wrapper = File.ReadAllText(commandPath);

		Assert.True(result.Success);
		Assert.Equal(TerminalCommandInstallOutcome.Repaired, result.Outcome);
		Assert.Equal(TerminalCommandSetupState.Installed, result.Snapshot.State);
		Assert.Contains("# target: " + currentTarget, wrapper, StringComparison.Ordinal);
		Assert.DoesNotContain("# target: " + oldTarget, wrapper, StringComparison.Ordinal);
	}

	[Fact]
	public void Probe_UnixForeignCommand_ReturnsConflictAndInstallDoesNotOverwriteIt()
	{
		using var temp = new TemporaryDirectory();
		var target = temp.CreateFile("app/DevProjex", "fake executable");
		var userBin = temp.CreateFolder(".local/bin");
		var commandPath = Path.Combine(userBin, CommandLineExecutableAliases.UnixCommand);
		File.WriteAllText(commandPath, "#!/bin/sh\necho foreign\n");
		var service = CreateService(TerminalCommandHostPlatform.Linux, temp.Path, userBin, target);

		var snapshot = service.Probe();
		var install = service.InstallOrRepair();
		var unchanged = File.ReadAllText(commandPath);

		Assert.Equal(TerminalCommandSetupState.ConflictingCommand, snapshot.State);
		Assert.False(snapshot.IsActionable);
		Assert.False(install.Success);
		Assert.Equal(TerminalCommandInstallOutcome.ConflictingCommand, install.Outcome);
		Assert.Contains("echo foreign", unchanged, StringComparison.Ordinal);
	}

	[Fact]
	public void BuildWrapperContent_QuotesTargetsWithApostrophesForPosixShells()
	{
		var target = "/Users/me/DevProjex's builds/DevProjex";

		var wrapper = TerminalCommandSetupService.BuildWrapperContent(target);

		Assert.StartsWith("#!/bin/sh", wrapper, StringComparison.Ordinal);
		Assert.Contains("# target: " + target, wrapper, StringComparison.Ordinal);
		Assert.Contains("exec '/Users/me/DevProjex'\"'\"'s builds/DevProjex' \"$@\"", wrapper, StringComparison.Ordinal);
	}

	[Fact]
	public void Probe_WhenEnvironmentProvidersThrow_DoesNotThrowAndDisablesSetup()
	{
		var service = new TerminalCommandSetupService(new TerminalCommandSetupServiceOptions
		{
			Platform = TerminalCommandHostPlatform.Linux,
			HomeDirectoryProvider = () => throw new InvalidOperationException("home failed"),
			PathVariableProvider = () => throw new InvalidOperationException("path failed"),
			ExecutablePathProvider = () => throw new InvalidOperationException("exe failed"),
			PathListSeparator = ';'
		});

		var snapshot = service.Probe();
		var install = service.InstallOrRepair();

		Assert.Equal(TerminalCommandSetupState.HomeDirectoryUnavailable, snapshot.State);
		Assert.False(snapshot.IsActionable);
		Assert.False(install.Success);
		Assert.Equal(TerminalCommandInstallOutcome.NotSupported, install.Outcome);
	}

	[Fact]
	public void PromptPolicy_DismissedNotInstalledPromptIsSuppressedButStaleRepairStillShows()
	{
		var settings = new AppViewSettings { IsTerminalCommandPromptDismissed = true };
		var notInstalled = new TerminalCommandSetupSnapshot(
			CommandLineExecutableAliases.UnixCommand,
			TerminalCommandSetupState.NotInstalled,
			"/home/me/.local/bin/devprojex",
			"/opt/DevProjex",
			null,
			"/home/me/.local/bin",
			UserBinDirectoryIsInPath: true,
			CanInstall: true,
			CanRepair: false,
			ShellProfileHint: null);
		var stale = notInstalled with
		{
			State = TerminalCommandSetupState.Stale,
			CanInstall = false,
			CanRepair = true,
			InstalledTargetExecutablePath = "/old/DevProjex"
		};

		Assert.False(TerminalCommandPromptPolicy.ShouldOfferAutomaticPrompt(settings, notInstalled, startedWithProjectPath: false));
		Assert.True(TerminalCommandPromptPolicy.ShouldOfferAutomaticPrompt(settings, stale, startedWithProjectPath: false));
	}

	[Fact]
	public void PromptPolicy_StartupPathSuppressesAutomaticPromptForAutomationFlows()
	{
		var settings = new AppViewSettings();
		var snapshot = new TerminalCommandSetupSnapshot(
			CommandLineExecutableAliases.UnixCommand,
			TerminalCommandSetupState.NotInstalled,
			"/home/me/.local/bin/devprojex",
			"/opt/DevProjex",
			null,
			"/home/me/.local/bin",
			UserBinDirectoryIsInPath: true,
			CanInstall: true,
			CanRepair: false,
			ShellProfileHint: null);

		Assert.False(TerminalCommandPromptPolicy.ShouldOfferAutomaticPrompt(settings, snapshot, startedWithProjectPath: true));
	}

	private static TerminalCommandSetupService CreateService(
		TerminalCommandHostPlatform platform,
		string? home,
		string? pathValue,
		string? executablePath,
		bool isWindowsPackaged = false)
	{
		return new TerminalCommandSetupService(new TerminalCommandSetupServiceOptions
		{
			Platform = platform,
			IsWindowsPackagedApp = () => isWindowsPackaged,
			HomeDirectoryProvider = () => home,
			PathVariableProvider = () => pathValue,
			ExecutablePathProvider = () => executablePath,
			PathListSeparator = ';'
		});
	}
}
