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
	public void Probe_WindowsPortableBuild_OffersUserLevelCommandSetup()
	{
		using var temp = new TemporaryDirectory();
		var target = temp.CreateFile("portable/DevProjex.exe", "fake executable");
		var service = CreateService(
			TerminalCommandHostPlatform.Windows,
			home: temp.Path,
			pathValue: null,
			executablePath: target,
			isWindowsPackaged: false);

		var snapshot = service.Probe();

		Assert.Equal(TerminalCommandSetupState.NotInstalled, snapshot.State);
		Assert.Equal(CommandLineExecutableAliases.UnixCommand, snapshot.CommandName);
		Assert.Equal(Path.Combine(temp.Path, "DevProjex", "bin", CommandLineExecutableAliases.WindowsPortableCommandFileName), snapshot.CommandPath);
		Assert.True(snapshot.CanInstall);
		Assert.False(snapshot.CanRepair);
		Assert.False(snapshot.UserBinDirectoryIsInPath);
		Assert.Contains("PATH", snapshot.ShellProfileHint, StringComparison.Ordinal);
	}

	[Fact]
	public void InstallOrRepair_WindowsPortableBuild_CreatesLauncherAndAddsUserPath()
	{
		using var temp = new TemporaryDirectory();
		var target = temp.CreateFile("portable/DevProjex.exe", "fake executable");
		var userPath = Path.Combine(temp.Path, "other-bin");
		var service = CreateWindowsPortableService(temp.Path, processPath: string.Empty, () => userPath, value => userPath = value, target);

		var result = service.InstallOrRepair();
		var commandPath = Path.Combine(temp.Path, "DevProjex", "bin", CommandLineExecutableAliases.WindowsPortableCommandFileName);
		var launcher = File.ReadAllText(commandPath);

		Assert.True(result.Success);
		Assert.Equal(TerminalCommandInstallOutcome.Created, result.Outcome);
		Assert.Equal(TerminalCommandSetupState.Installed, result.Snapshot.State);
		Assert.Contains("rem DevProjex terminal command wrapper", launcher, StringComparison.Ordinal);
		Assert.Contains("rem target: " + target, launcher, StringComparison.Ordinal);
		Assert.Contains("\"" + target + "\" %*", launcher, StringComparison.Ordinal);
		Assert.Contains(Path.GetDirectoryName(commandPath)!, userPath, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public void BuildWindowsLauncherContent_EscapesPercentSignsWithoutChangingManagedTargetComment()
	{
		var target = @"C:\Users\me\100% portable\DevProjex.exe";

		var launcher = TerminalCommandSetupService.BuildWindowsLauncherContent(target);

		Assert.Contains("rem target: " + target, launcher, StringComparison.Ordinal);
		Assert.Contains("\"C:\\Users\\me\\100%% portable\\DevProjex.exe\" %*", launcher, StringComparison.Ordinal);
	}

	[Fact]
	public void Probe_WindowsPortableBuild_MissingCurrentExecutable_DisablesInstallInsteadOfCreatingBrokenLauncher()
	{
		using var temp = new TemporaryDirectory();
		var missingTarget = Path.Combine(temp.Path, "portable", "DevProjex.exe");
		var userPath = string.Empty;
		var service = CreateWindowsPortableService(temp.Path, processPath: string.Empty, () => userPath, value => userPath = value, missingTarget);

		var snapshot = service.Probe();
		var result = service.InstallOrRepair();
		var commandPath = Path.Combine(temp.Path, "DevProjex", "bin", CommandLineExecutableAliases.WindowsPortableCommandFileName);

		Assert.Equal(TerminalCommandSetupState.Failed, snapshot.State);
		Assert.False(snapshot.IsActionable);
		Assert.False(result.Success);
		Assert.Equal(TerminalCommandInstallOutcome.NotSupported, result.Outcome);
		Assert.False(File.Exists(commandPath));
	}

	[Fact]
	public void Probe_WindowsPortableBuild_MissingLocalAppData_DisablesSetup()
	{
		using var temp = new TemporaryDirectory();
		var target = temp.CreateFile("portable/DevProjex.exe", "fake executable");
		var service = new TerminalCommandSetupService(new TerminalCommandSetupServiceOptions
		{
			Platform = TerminalCommandHostPlatform.Windows,
			IsWindowsPackagedApp = () => false,
			LocalAppDataPathProvider = () => null,
			PathVariableProvider = () => string.Empty,
			UserPathVariableProvider = () => string.Empty,
			MachinePathVariableProvider = () => string.Empty,
			UserPathVariableWriter = _ => throw new InvalidOperationException("Unexpected PATH write."),
			ExecutablePathProvider = () => target,
			PathListSeparator = ';'
		});

		var snapshot = service.Probe();
		var result = service.InstallOrRepair();

		Assert.Equal(TerminalCommandSetupState.HomeDirectoryUnavailable, snapshot.State);
		Assert.False(snapshot.IsActionable);
		Assert.False(result.Success);
		Assert.Equal(TerminalCommandInstallOutcome.NotSupported, result.Outcome);
		Assert.Null(snapshot.CommandPath);
	}

	[Fact]
	public void Probe_WindowsPortableBuild_WithCurrentLauncherAndUserPath_ReturnsInstalled()
	{
		using var temp = new TemporaryDirectory();
		var target = temp.CreateFile("portable/DevProjex.exe", "fake executable");
		var userBin = temp.CreateFolder("DevProjex/bin");
		var commandPath = Path.Combine(userBin, CommandLineExecutableAliases.WindowsPortableCommandFileName);
		File.WriteAllText(commandPath, TerminalCommandSetupService.BuildWindowsLauncherContent(target));
		var userPath = userBin + Path.DirectorySeparatorChar;
		var service = CreateWindowsPortableService(temp.Path, processPath: string.Empty, () => userPath, _ => throw new InvalidOperationException(), target);

		var snapshot = service.Probe();
		var install = service.InstallOrRepair();

		Assert.Equal(TerminalCommandSetupState.Installed, snapshot.State);
		Assert.True(snapshot.IsReady);
		Assert.True(snapshot.UserBinDirectoryIsInPath);
		Assert.True(install.Success);
		Assert.Equal(TerminalCommandInstallOutcome.AlreadyInstalled, install.Outcome);
	}

	[Fact]
	public void Probe_WindowsPortableBuild_WithCurrentLauncherAndOnlyProcessPath_ReturnsRepairableStale()
	{
		using var temp = new TemporaryDirectory();
		var target = temp.CreateFile("portable/DevProjex.exe", "fake executable");
		var userBin = temp.CreateFolder("DevProjex/bin");
		File.WriteAllText(
			Path.Combine(userBin, CommandLineExecutableAliases.WindowsPortableCommandFileName),
			TerminalCommandSetupService.BuildWindowsLauncherContent(target));
		var userPath = string.Empty;
		var service = CreateWindowsPortableService(temp.Path, processPath: userBin, () => userPath, value => userPath = value, target);

		var snapshot = service.Probe();
		var result = service.InstallOrRepair();

		Assert.Equal(TerminalCommandSetupState.Stale, snapshot.State);
		Assert.True(snapshot.CanRepair);
		Assert.False(snapshot.UserBinDirectoryIsInPath);
		Assert.True(result.Success);
		AssertPathListContains(userPath, userBin);
	}

	[Fact]
	public void InstallOrRepair_WindowsPortableBuild_DoesNotDuplicateExistingUserPathEntry()
	{
		using var temp = new TemporaryDirectory();
		var target = temp.CreateFile("portable/DevProjex.exe", "fake executable");
		var userBin = Path.Combine(temp.Path, "DevProjex", "bin");
		var userPath = userBin + Path.DirectorySeparatorChar;
		var writeCount = 0;
		var service = CreateWindowsPortableService(temp.Path, processPath: string.Empty, () => userPath, value =>
		{
			writeCount++;
			userPath = value;
		}, target);

		var result = service.InstallOrRepair();

		Assert.True(result.Success);
		Assert.Equal(TerminalCommandInstallOutcome.Created, result.Outcome);
		Assert.Equal(TerminalCommandSetupState.Installed, result.Snapshot.State);
		Assert.Equal(0, writeCount);
		Assert.Single(userPath.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
	}

	[Fact]
	public void InstallOrRepair_WindowsPortableBuild_DoesNotWriteUserPathWhenMachinePathAlreadyContainsBin()
	{
		using var temp = new TemporaryDirectory();
		var target = temp.CreateFile("portable/DevProjex.exe", "fake executable");
		var userBin = Path.Combine(temp.Path, "DevProjex", "bin");
		var userPath = string.Empty;
		var writeCount = 0;
		var service = CreateWindowsPortableService(
			temp.Path,
			processPath: string.Empty,
			() => userPath,
			value =>
			{
				writeCount++;
				userPath = value;
			},
			target,
			machinePathProvider: () => userBin);

		var result = service.InstallOrRepair();

		Assert.True(result.Success);
		Assert.Equal(TerminalCommandInstallOutcome.Created, result.Outcome);
		Assert.Equal(TerminalCommandSetupState.Installed, result.Snapshot.State);
		Assert.True(result.Snapshot.UserBinDirectoryIsInPath);
		Assert.Equal(0, writeCount);
		Assert.Equal(string.Empty, userPath);
	}

	[Fact]
	public void Probe_WindowsPortableBuild_WithCurrentLauncherButMissingPath_ReturnsRepairableStale()
	{
		using var temp = new TemporaryDirectory();
		var target = temp.CreateFile("portable/DevProjex.exe", "fake executable");
		var userBin = temp.CreateFolder("DevProjex/bin");
		File.WriteAllText(
			Path.Combine(userBin, CommandLineExecutableAliases.WindowsPortableCommandFileName),
			TerminalCommandSetupService.BuildWindowsLauncherContent(target));
		var userPath = string.Empty;
		var service = CreateWindowsPortableService(temp.Path, processPath: string.Empty, () => userPath, value => userPath = value, target);

		var snapshot = service.Probe();
		var result = service.InstallOrRepair();

		Assert.Equal(TerminalCommandSetupState.Stale, snapshot.State);
		Assert.True(snapshot.CanRepair);
		Assert.False(snapshot.UserBinDirectoryIsInPath);
		Assert.True(result.Success);
		Assert.Equal(TerminalCommandInstallOutcome.Repaired, result.Outcome);
		AssertPathListContains(userPath, userBin);
	}

	[Fact]
	public void InstallOrRepair_WindowsPortableBuild_RepairsLauncherAfterExecutableMoved()
	{
		using var temp = new TemporaryDirectory();
		var oldTarget = temp.CreateFile("old/DevProjex.exe", "old executable");
		var currentTarget = temp.CreateFile("current/DevProjex.exe", "current executable");
		var userBin = temp.CreateFolder("DevProjex/bin");
		var commandPath = Path.Combine(userBin, CommandLineExecutableAliases.WindowsPortableCommandFileName);
		File.WriteAllText(commandPath, TerminalCommandSetupService.BuildWindowsLauncherContent(oldTarget));
		var userPath = userBin;
		var service = CreateWindowsPortableService(temp.Path, processPath: string.Empty, () => userPath, value => userPath = value, currentTarget);

		var snapshot = service.Probe();
		var result = service.InstallOrRepair();
		var launcher = File.ReadAllText(commandPath);

		Assert.Equal(TerminalCommandSetupState.Stale, snapshot.State);
		Assert.Equal(oldTarget, snapshot.InstalledTargetExecutablePath);
		Assert.True(result.Success);
		Assert.Equal(TerminalCommandInstallOutcome.Repaired, result.Outcome);
		Assert.Contains("rem target: " + currentTarget, launcher, StringComparison.Ordinal);
		Assert.DoesNotContain("rem target: " + oldTarget, launcher, StringComparison.Ordinal);
	}

	[Fact]
	public void InstallOrRepair_WindowsPortableBuild_DoesNotOverwriteForeignCommand()
	{
		using var temp = new TemporaryDirectory();
		var target = temp.CreateFile("portable/DevProjex.exe", "fake executable");
		var userBin = temp.CreateFolder("DevProjex/bin");
		var commandPath = Path.Combine(userBin, CommandLineExecutableAliases.WindowsPortableCommandFileName);
		File.WriteAllText(commandPath, "@echo foreign");
		var userPath = userBin;
		var service = CreateWindowsPortableService(temp.Path, processPath: string.Empty, () => userPath, value => userPath = value, target);

		var snapshot = service.Probe();
		var result = service.InstallOrRepair();
		var unchanged = File.ReadAllText(commandPath);

		Assert.Equal(TerminalCommandSetupState.ConflictingCommand, snapshot.State);
		Assert.False(snapshot.IsActionable);
		Assert.False(result.Success);
		Assert.Equal(TerminalCommandInstallOutcome.ConflictingCommand, result.Outcome);
		Assert.Contains("foreign", unchanged, StringComparison.Ordinal);
	}

	[Fact]
	public void Probe_WindowsPortableBuild_CommandPathOccupiedByDirectory_ReturnsConflict()
	{
		using var temp = new TemporaryDirectory();
		var target = temp.CreateFile("portable/DevProjex.exe", "fake executable");
		temp.CreateFolder(Path.Combine("DevProjex", "bin", CommandLineExecutableAliases.WindowsPortableCommandFileName));
		var service = CreateWindowsPortableService(temp.Path, processPath: string.Empty, () => string.Empty, _ => throw new InvalidOperationException(), target);

		var snapshot = service.Probe();
		var result = service.InstallOrRepair();

		Assert.Equal(TerminalCommandSetupState.ConflictingCommand, snapshot.State);
		Assert.False(snapshot.IsActionable);
		Assert.False(result.Success);
		Assert.Equal(TerminalCommandInstallOutcome.ConflictingCommand, result.Outcome);
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
		var home = Path.Combine("unix-home", Guid.NewGuid().ToString("N"));
		var userBin = Path.Combine(home, ".local", "bin");
		var pathValue = string.Join(':', "/usr/bin", userBin + Path.DirectorySeparatorChar, "/bin");
		var service = new TerminalCommandSetupService(new TerminalCommandSetupServiceOptions
		{
			Platform = TerminalCommandHostPlatform.Linux,
			HomeDirectoryProvider = () => home,
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
	public void PromptPolicy_WindowsPortableReleaseShowsOneTimeInstallPrompt()
	{
		var settings = new AppViewSettings();
		var snapshot = new TerminalCommandSetupSnapshot(
			CommandLineExecutableAliases.UnixCommand,
			TerminalCommandSetupState.NotInstalled,
			CommandPath: @"C:\Users\me\AppData\Local\DevProjex\bin\devprojex.cmd",
			TargetExecutablePath: @"C:\Tools\DevProjex\DevProjex.exe",
			InstalledTargetExecutablePath: null,
			UserBinDirectory: @"C:\Users\me\AppData\Local\DevProjex\bin",
			UserBinDirectoryIsInPath: false,
			CanInstall: true,
			CanRepair: false,
			ShellProfileHint: null);

		Assert.True(TerminalCommandPromptPolicy.ShouldOfferAutomaticPrompt(settings, snapshot, startedWithProjectPath: false));
		Assert.True(TerminalCommandPromptPolicy.IsDismissibleAutomaticPrompt(snapshot));
	}

	[Fact]
	public void PromptPolicy_WindowsPortablePromptIsSuppressedAfterDismissal()
	{
		var settings = new AppViewSettings { IsTerminalCommandPromptDismissed = true };
		var snapshot = new TerminalCommandSetupSnapshot(
			CommandLineExecutableAliases.UnixCommand,
			TerminalCommandSetupState.NotInstalled,
			CommandPath: @"C:\Users\me\AppData\Local\DevProjex\bin\devprojex.cmd",
			TargetExecutablePath: @"C:\Tools\DevProjex\DevProjex.exe",
			InstalledTargetExecutablePath: null,
			UserBinDirectory: @"C:\Users\me\AppData\Local\DevProjex\bin",
			UserBinDirectoryIsInPath: false,
			CanInstall: true,
			CanRepair: false,
			ShellProfileHint: null);

		Assert.False(TerminalCommandPromptPolicy.ShouldOfferAutomaticPrompt(settings, snapshot, startedWithProjectPath: false));
	}

	[Fact]
	public void PromptPolicy_UnsupportedPackageDoesNotShowAutomaticPrompt()
	{
		var settings = new AppViewSettings();
		var snapshot = new TerminalCommandSetupSnapshot(
			CommandLineExecutableAliases.WindowsPortableExecutable,
			TerminalCommandSetupState.UnsupportedOnCurrentPackage,
			CommandPath: null,
			TargetExecutablePath: @"C:\Tools\DevProjex\DevProjex.exe",
			InstalledTargetExecutablePath: null,
			UserBinDirectory: null,
			UserBinDirectoryIsInPath: false,
			CanInstall: false,
			CanRepair: false,
			ShellProfileHint: null);

		Assert.False(TerminalCommandPromptPolicy.ShouldOfferAutomaticPrompt(settings, snapshot, startedWithProjectPath: false));
		Assert.False(TerminalCommandPromptPolicy.IsDismissibleAutomaticPrompt(snapshot));
	}

	[Fact]
	public void PromptPolicy_WindowsStoreManagedAliasDoesNotShowAutomaticPrompt()
	{
		var settings = new AppViewSettings();
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

		Assert.False(TerminalCommandPromptPolicy.ShouldOfferAutomaticPrompt(settings, snapshot, startedWithProjectPath: false));
		Assert.False(TerminalCommandPromptPolicy.IsDismissibleAutomaticPrompt(snapshot));
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
			LocalAppDataPathProvider = () => home,
			PathVariableProvider = () => pathValue,
			UserPathVariableProvider = () => pathValue,
			MachinePathVariableProvider = () => null,
			UserPathVariableWriter = _ => throw new InvalidOperationException("Unexpected PATH write."),
			ExecutablePathProvider = () => executablePath,
			PathListSeparator = ';'
		});
	}

	private static TerminalCommandSetupService CreateWindowsPortableService(
		string localAppData,
		string? processPath,
		Func<string?> userPathProvider,
		Action<string> userPathWriter,
		string executablePath,
		Func<string?>? machinePathProvider = null)
	{
		return new TerminalCommandSetupService(new TerminalCommandSetupServiceOptions
		{
			Platform = TerminalCommandHostPlatform.Windows,
			IsWindowsPackagedApp = () => false,
			HomeDirectoryProvider = () => localAppData,
			LocalAppDataPathProvider = () => localAppData,
			PathVariableProvider = () => processPath,
			UserPathVariableProvider = userPathProvider,
			MachinePathVariableProvider = machinePathProvider ?? (() => string.Empty),
			UserPathVariableWriter = userPathWriter,
			ExecutablePathProvider = () => executablePath,
			PathListSeparator = ';'
		});
	}

	private static void AssertPathListContains(string pathValue, string expectedDirectory)
	{
		var entries = pathValue.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		Assert.Contains(entries, entry => string.Equals(
			NormalizeForPathListAssert(entry),
			NormalizeForPathListAssert(expectedDirectory),
			StringComparison.OrdinalIgnoreCase));
	}

	private static string NormalizeForPathListAssert(string value) =>
		Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
