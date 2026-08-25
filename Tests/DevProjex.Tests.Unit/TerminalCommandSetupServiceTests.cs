using DevProjex.Infrastructure.TerminalCommands;
using DevProjex.Infrastructure.ThemePresets;

namespace DevProjex.Tests.Unit;

[Trait("Category", "TerminalCommand")]
public sealed class TerminalCommandSetupServiceTests
{
	[Fact]
	public void LauncherValidationDoesNotAcquireTheParentTerminal()
	{
		var startInfo =
			TerminalCommandSetupService.CreateLauncherValidationStartInfo(
				"dotnet");

		Assert.False(startInfo.UseShellExecute);
		Assert.True(startInfo.RedirectStandardInput);
		Assert.True(startInfo.RedirectStandardOutput);
		Assert.True(startInfo.RedirectStandardError);
	}

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
	public void InstallOrRepair_WindowsPackagedApp_NeverCreatesPortableLauncherOrWritesUserPath()
	{
		using var temp = new TemporaryDirectory();
		var userPath = "existing-user-path";
		var writeCount = 0;
		var service = new TerminalCommandSetupService(new TerminalCommandSetupServiceOptions
		{
			Platform = TerminalCommandHostPlatform.Windows,
			IsWindowsPackagedApp = () => true,
			LocalAppDataPathProvider = () => temp.Path,
			PathVariableProvider = () => userPath,
			UserPathVariableProvider = () => userPath,
			MachinePathVariableProvider = () => string.Empty,
			UserPathVariableWriter = value =>
			{
				writeCount++;
				userPath = value;
			},
			ExecutablePathProvider = () => @"C:\Program Files\WindowsApps\DevProjex\DevProjex.exe",
			PathListSeparator = ';'
		});

		var result = service.InstallOrRepair();

		Assert.True(result.Success);
		Assert.Equal(TerminalCommandInstallOutcome.AlreadyInstalled, result.Outcome);
		Assert.Equal(TerminalCommandSetupState.ManagedByOperatingSystem, result.Snapshot.State);
		Assert.Equal(0, writeCount);
		Assert.False(Directory.Exists(Path.Combine(temp.Path, "DevProjex", "bin")));
		Assert.Null(result.Snapshot.CommandPath);
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
		Assert.Contains("setlocal DisableDelayedExpansion", launcher, StringComparison.Ordinal);
		Assert.Contains(WindowsTargetMarker(target), launcher, StringComparison.Ordinal);
		Assert.Contains("set \"DEVPROJEX_EXE=" + target + "\"", launcher, StringComparison.Ordinal);
		Assert.Contains("set \"DEVPROJEX_DLL=" + Path.ChangeExtension(target, ".dll") + "\"", launcher, StringComparison.Ordinal);
		Assert.Contains(
			"if not exist \"%DEVPROJEX_DLL%\" goto :run-native",
			launcher,
			StringComparison.Ordinal);
		Assert.Contains("dotnet \"%DEVPROJEX_DLL%\" %*", launcher, StringComparison.Ordinal);
		Assert.Contains("\"%DEVPROJEX_EXE%\" %*", launcher, StringComparison.Ordinal);
		Assert.Contains("set \"DEVPROJEX_EXIT_CODE=%ERRORLEVEL%\"", launcher, StringComparison.Ordinal);
		Assert.Contains("exit /b %DEVPROJEX_EXIT_CODE%", launcher, StringComparison.Ordinal);
		Assert.DoesNotContain("if exist \"%DEVPROJEX_DLL%\" (", launcher, StringComparison.Ordinal);
		Assert.DoesNotContain("start /wait", launcher, StringComparison.OrdinalIgnoreCase);
		Assert.Contains(Path.GetDirectoryName(commandPath)!, userPath, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public void BuildWindowsLauncherContent_EscapesPercentSignsWithoutChangingManagedTargetComment()
	{
		var target = @"C:\Users\me\100% portable\DevProjex.exe";

		var launcher = TerminalCommandSetupService.BuildWindowsLauncherContent(target);
		var encodedTarget = Convert.ToBase64String(Encoding.UTF8.GetBytes(target));

		Assert.Contains("rem target-base64: " + encodedTarget, launcher, StringComparison.Ordinal);
		Assert.DoesNotContain("rem target: " + target, launcher, StringComparison.Ordinal);
		Assert.Contains("set \"DEVPROJEX_EXE=C:\\Users\\me\\100%% portable\\DevProjex.exe\"", launcher, StringComparison.Ordinal);
		Assert.Contains("set \"DEVPROJEX_DLL=C:\\Users\\me\\100%% portable\\DevProjex.dll\"", launcher, StringComparison.Ordinal);
	}

	[Fact]
	public void BuildWindowsLauncherContent_PreservesBatchMetacharactersInsideQuotedEnvironmentValues()
	{
		var target = @"C:\Tools & Stuff\Dev^Projex!100%\DevProjex.exe";

		var launcher = TerminalCommandSetupService.BuildWindowsLauncherContent(target);
		var encodedTarget = Convert.ToBase64String(Encoding.UTF8.GetBytes(target));

		Assert.Contains("rem target-base64: " + encodedTarget, launcher, StringComparison.Ordinal);
		Assert.DoesNotContain("rem target: " + target, launcher, StringComparison.Ordinal);
		Assert.Contains("set \"DEVPROJEX_EXE=C:\\Tools & Stuff\\Dev^Projex!100%%\\DevProjex.exe\"", launcher, StringComparison.Ordinal);
		Assert.Contains("set \"DEVPROJEX_DLL=C:\\Tools & Stuff\\Dev^Projex!100%%\\DevProjex.dll\"", launcher, StringComparison.Ordinal);
		Assert.DoesNotContain("start /wait", launcher, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public void BuildWindowsLauncherContent_AllInvocationsUseTerminalRouteForSingleFilePublish()
	{
		var target = @"C:\Tools\DevProjex\DevProjex.exe";

		var launcher = TerminalCommandSetupService.BuildWindowsLauncherContent(target);

		Assert.Contains(
			"for /f \"tokens=2 delims=:\" %%C in ('chcp') do set \"DEVPROJEX_ORIGINAL_CODE_PAGE=%%C\"",
			launcher,
			StringComparison.Ordinal);
		Assert.Contains("chcp 65001 >nul", launcher, StringComparison.Ordinal);
		Assert.Contains("set \"DEVPROJEX_TERMINAL_HOST=1\"", launcher, StringComparison.Ordinal);
		Assert.Contains("dotnet \"%DEVPROJEX_DLL%\" %*", launcher, StringComparison.Ordinal);
		Assert.Contains("\"%DEVPROJEX_EXE%\" %*", launcher, StringComparison.Ordinal);
		Assert.Contains("set \"DEVPROJEX_EXIT_CODE=%ERRORLEVEL%\"", launcher, StringComparison.Ordinal);
		Assert.Contains("chcp %DEVPROJEX_ORIGINAL_CODE_PAGE% >nul", launcher, StringComparison.Ordinal);
		Assert.Contains("exit /b %DEVPROJEX_EXIT_CODE%", launcher, StringComparison.Ordinal);
		Assert.True(
			launcher.IndexOf(
				"chcp %DEVPROJEX_ORIGINAL_CODE_PAGE% >nul",
				StringComparison.Ordinal) <
			launcher.IndexOf(
				"dotnet \"%DEVPROJEX_DLL%\" %*",
				StringComparison.Ordinal),
			"The launcher must restore the caller code page before the product runs.");
		Assert.True(
			launcher.LastIndexOf(
				"chcp %DEVPROJEX_ORIGINAL_CODE_PAGE% >nul",
				StringComparison.Ordinal) >
			launcher.IndexOf(
				"dotnet \"%DEVPROJEX_DLL%\" %*",
				StringComparison.Ordinal),
			"The launcher must restore any code-page change made while the product ran.");
		Assert.DoesNotContain("if \"%~1\"==\"\"", launcher, StringComparison.Ordinal);
		Assert.DoesNotContain("start \"\"", launcher, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain("start /wait", launcher, StringComparison.OrdinalIgnoreCase);
	}

	[Theory]
	[InlineData(@"C:\Tools\DevProjex\DevProjex.exe", @"C:\Tools\DevProjex\DevProjex.dll")]
	[InlineData(@"C:/Tools/DevProjex/DevProjex.exe", @"C:/Tools/DevProjex/DevProjex.dll")]
	[InlineData("DevProjex.exe", "DevProjex.dll")]
	public void BuildWindowsManagedAssemblyPath_HandlesWindowsPathsOnAnyRunner(
		string executablePath,
		string expectedAssemblyPath)
	{
		var assemblyPath = TerminalCommandSetupService.BuildWindowsManagedAssemblyPath(executablePath);

		Assert.Equal(expectedAssemblyPath, assemblyPath);
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
	public void Probe_WindowsPortableBuild_PathProvidersThrow_KeepsSetupActionableWithoutCrashing()
	{
		using var temp = new TemporaryDirectory();
		var target = temp.CreateFile("portable/DevProjex.exe", "fake executable");
		var service = new TerminalCommandSetupService(new TerminalCommandSetupServiceOptions
		{
			Platform = TerminalCommandHostPlatform.Windows,
			IsWindowsPackagedApp = () => false,
			LocalAppDataPathProvider = () => temp.Path,
			PathVariableProvider = () => throw new IOException("process PATH unavailable"),
			UserPathVariableProvider = () => throw new IOException("user PATH unavailable"),
			MachinePathVariableProvider = () => throw new IOException("machine PATH unavailable"),
			UserPathVariableWriter = _ => throw new InvalidOperationException("Probe must not write PATH."),
			ExecutablePathProvider = () => target,
			PathListSeparator = ';'
		});

		var snapshot = service.Probe();

		Assert.Equal(TerminalCommandSetupState.NotInstalled, snapshot.State);
		Assert.True(snapshot.CanInstall);
		Assert.False(snapshot.UserBinDirectoryIsInPath);
		Assert.Contains("PATH", snapshot.ShellProfileHint, StringComparison.Ordinal);
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
	public void Reinstall_WindowsPortableInstalledLauncher_RewritesAndValidatesManagedCommand()
	{
		using var temp = new TemporaryDirectory();
		var target = temp.CreateFile("portable/DevProjex.exe", "fake executable");
		var userBin = temp.CreateFolder("DevProjex/bin");
		var commandPath = Path.Combine(userBin, CommandLineExecutableAliases.WindowsPortableCommandFileName);
		File.WriteAllText(commandPath, TerminalCommandSetupService.BuildWindowsLauncherContent(target));
		var validationCount = 0;
		var service = CreateWindowsPortableService(
			temp.Path,
			processPath: string.Empty,
			() => userBin,
			_ => throw new InvalidOperationException("Installed PATH must not be rewritten."),
			target,
			launcherValidator: (path, timeout) =>
			{
				validationCount++;
				Assert.Equal(NormalizeForPathListAssert(commandPath), NormalizeForPathListAssert(path));
				Assert.Equal(TimeSpan.FromSeconds(5), timeout);
				return new TerminalCommandValidationResult(true);
			});

		var result = service.Reinstall();

		Assert.True(result.Success, result.ErrorMessage);
		Assert.Equal(TerminalCommandInstallOutcome.Reinstalled, result.Outcome);
		Assert.Equal(TerminalCommandSetupState.Installed, result.Snapshot.State);
		Assert.Equal(1, validationCount);
		Assert.Equal(TerminalCommandSetupService.BuildWindowsLauncherContent(target), File.ReadAllText(commandPath));
	}

	[Fact]
	public void Reinstall_FunctionalValidationFailure_ReturnsFailureWithoutDestroyingManagedLauncher()
	{
		using var temp = new TemporaryDirectory();
		var target = temp.CreateFile("portable/DevProjex.exe", "fake executable");
		var userBin = temp.CreateFolder("DevProjex/bin");
		var commandPath = Path.Combine(userBin, CommandLineExecutableAliases.WindowsPortableCommandFileName);
		File.WriteAllText(commandPath, TerminalCommandSetupService.BuildWindowsLauncherContent(target));
		var service = CreateWindowsPortableService(
			temp.Path,
			processPath: string.Empty,
			() => userBin,
			_ => throw new InvalidOperationException("Installed PATH must not be rewritten."),
			target,
			launcherValidator: (_, _) => new TerminalCommandValidationResult(false, "synthetic validation failure"));

		var result = service.Reinstall();

		Assert.False(result.Success);
		Assert.Equal(TerminalCommandInstallOutcome.Failed, result.Outcome);
		Assert.Contains("synthetic validation failure", result.ErrorMessage, StringComparison.Ordinal);
		Assert.Equal(TerminalCommandSetupState.Installed, service.Probe().State);
		Assert.Equal(TerminalCommandSetupService.BuildWindowsLauncherContent(target), File.ReadAllText(commandPath));
	}

	[Fact]
	public void Reinstall_OperatingSystemManagedAlias_IsRejectedWithoutValidation()
	{
		var validationCount = 0;
		var service = new TerminalCommandSetupService(new TerminalCommandSetupServiceOptions
		{
			Platform = TerminalCommandHostPlatform.Windows,
			IsWindowsPackagedApp = () => true,
			LauncherValidator = (_, _) =>
			{
				validationCount++;
				return new TerminalCommandValidationResult(true);
			}
		});

		var result = service.Reinstall();

		Assert.False(result.Success);
		Assert.Equal(TerminalCommandInstallOutcome.NotSupported, result.Outcome);
		Assert.Equal(TerminalCommandSetupState.ManagedByOperatingSystem, result.Snapshot.State);
		Assert.Equal(0, validationCount);
	}

	[Fact]
	public void InstallOrRepair_WindowsPortableBuild_MovesLauncherBeforeWindowsAppsAlias()
	{
		using var temp = new TemporaryDirectory();
		var target = temp.CreateFile("portable/DevProjex.exe", "fake executable");
		var userBin = temp.CreateFolder("DevProjex/bin");
		var windowsApps = Path.Combine(temp.Path, "Microsoft", "WindowsApps");
		Directory.CreateDirectory(windowsApps);
		temp.CreateFile("Microsoft/WindowsApps/devprojex.exe", "foreign alias");
		var commandPath = Path.Combine(userBin, CommandLineExecutableAliases.WindowsPortableCommandFileName);
		File.WriteAllText(commandPath, TerminalCommandSetupService.BuildWindowsLauncherContent(target));
		var userPath = string.Join(';', windowsApps, userBin);
		var service = CreateWindowsPortableService(temp.Path, processPath: string.Empty, () => userPath, value => userPath = value, target);

		var snapshot = service.Probe();
		var install = service.ConfigurePath();
		var entries = userPath.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

		Assert.Equal(TerminalCommandSetupState.CommandShadowed, snapshot.State);
		Assert.False(snapshot.CanRepair);
		Assert.Equal(
			NormalizeForPathListAssert(Path.Combine(windowsApps, "devprojex.exe")),
			NormalizeForPathListAssert(snapshot.ResolvedCommandPath!),
			ignoreCase: true);
		Assert.True(install.Success);
		Assert.Equal(TerminalCommandSetupState.Installed, install.Snapshot.State);
		Assert.Equal(
			NormalizeForPathListAssert(userBin),
			NormalizeForPathListAssert(entries[0]));
		Assert.Equal(
			NormalizeForPathListAssert(windowsApps),
			NormalizeForPathListAssert(entries[1]));
		Assert.Equal(2, entries.Length);
	}

	[Fact]
	public void Probe_WindowsPortableBuild_LegacyLauncherWithoutConsoleRoute_ReturnsRepairableStale()
	{
		using var temp = new TemporaryDirectory();
		var target = temp.CreateFile("portable/DevProjex.exe", "fake executable");
		var userBin = temp.CreateFolder("DevProjex/bin");
		var commandPath = Path.Combine(userBin, CommandLineExecutableAliases.WindowsPortableCommandFileName);
		File.WriteAllText(
			commandPath,
			string.Join(
				"\r\n",
				"@echo off",
				"rem DevProjex terminal command wrapper",
				"rem target: " + target,
				"\"" + target + "\" %*",
				string.Empty));
		var userPath = userBin;
		var service = CreateWindowsPortableService(temp.Path, processPath: string.Empty, () => userPath, _ => throw new InvalidOperationException(), target);

		var snapshot = service.Probe();
		var install = service.InstallOrRepair();
		var repairedLauncher = File.ReadAllText(commandPath);

		Assert.Equal(TerminalCommandSetupState.Stale, snapshot.State);
		Assert.True(snapshot.CanRepair);
		Assert.False(snapshot.IsReady);
		Assert.True(install.Success);
		Assert.Equal(TerminalCommandInstallOutcome.Repaired, install.Outcome);
		Assert.Contains("dotnet \"%DEVPROJEX_DLL%\" %*", repairedLauncher, StringComparison.Ordinal);
	}

	[Fact]
	public void Probe_WindowsPortableBuild_LauncherWithStartWaitArgumentFallback_ReturnsRepairableStale()
	{
		using var temp = new TemporaryDirectory();
		var target = temp.CreateFile("portable/DevProjex.exe", "fake executable");
		var userBin = temp.CreateFolder("DevProjex/bin");
		var commandPath = Path.Combine(userBin, CommandLineExecutableAliases.WindowsPortableCommandFileName);
		File.WriteAllText(
			commandPath,
			string.Join(
				"\r\n",
				"@echo off",
				"rem DevProjex terminal command wrapper",
				"rem target: " + target,
				"set \"DEVPROJEX_EXE=" + target + "\"",
				"set \"DEVPROJEX_DLL=" + Path.ChangeExtension(target, ".dll") + "\"",
				"if \"%~1\"==\"\" (",
				"  start \"\" \"%DEVPROJEX_EXE%\"",
				"  exit /b 0",
				")",
				"if exist \"%DEVPROJEX_DLL%\" (",
				"  dotnet \"%DEVPROJEX_DLL%\" %*",
				"  exit /b",
				")",
				"start /wait \"\" \"%DEVPROJEX_EXE%\" %*",
				"exit /b",
				string.Empty));
		var userPath = userBin;
		var service = CreateWindowsPortableService(temp.Path, processPath: string.Empty, () => userPath, _ => throw new InvalidOperationException(), target);

		var snapshot = service.Probe();
		var install = service.InstallOrRepair();
		var repairedLauncher = File.ReadAllText(commandPath);

		Assert.Equal(TerminalCommandSetupState.Stale, snapshot.State);
		Assert.True(snapshot.CanRepair);
		Assert.True(install.Success);
		Assert.Equal(TerminalCommandInstallOutcome.Repaired, install.Outcome);
		Assert.Contains("\"%DEVPROJEX_EXE%\" %*", repairedLauncher, StringComparison.Ordinal);
		Assert.DoesNotContain("start /wait", repairedLauncher, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public void Probe_WindowsPortableBuild_UserPathEntryWithWindowsTrailingSlash_ReturnsInstalledOnAnyRunner()
	{
		using var temp = new TemporaryDirectory();
		var target = temp.CreateFile("portable/DevProjex.exe", "fake executable");
		var userBin = temp.CreateFolder("DevProjex/bin");
		var commandPath = Path.Combine(userBin, CommandLineExecutableAliases.WindowsPortableCommandFileName);
		File.WriteAllText(commandPath, TerminalCommandSetupService.BuildWindowsLauncherContent(target));
		var userPath = userBin + "\\";
		var service = CreateWindowsPortableService(
			temp.Path,
			processPath: string.Empty,
			() => userPath,
			_ => throw new InvalidOperationException("Existing Windows-style PATH entry must be recognized."),
			target);

		var snapshot = service.Probe();
		var install = service.InstallOrRepair();

		Assert.Equal(TerminalCommandSetupState.Installed, snapshot.State);
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
		var result = service.ConfigurePath();

		Assert.Equal(TerminalCommandSetupState.InstalledPathMissing, snapshot.State);
		Assert.False(snapshot.CanRepair);
		Assert.False(snapshot.UserBinDirectoryIsInPath);
		Assert.True(result.Success);
		AssertPathListContains(userPath, userBin);
	}

	[Fact]
	public void InstallOrRepair_WindowsPortableBuild_TransientUserPathReadFailureStillInstallsOnceProviderRecovers()
	{
		using var temp = new TemporaryDirectory();
		var target = temp.CreateFile("portable/DevProjex.exe", "fake executable");
		var userPath = string.Empty;
		var userPathReadCount = 0;
		var writeCount = 0;
		var service = new TerminalCommandSetupService(new TerminalCommandSetupServiceOptions
		{
			Platform = TerminalCommandHostPlatform.Windows,
			IsWindowsPackagedApp = () => false,
			LocalAppDataPathProvider = () => temp.Path,
			PathVariableProvider = () => string.Empty,
			UserPathVariableProvider = () =>
			{
				userPathReadCount++;
				if (userPathReadCount == 1)
					throw new IOException("transient user PATH read failure");

				return userPath;
			},
			MachinePathVariableProvider = () => string.Empty,
			UserPathVariableWriter = value =>
			{
				writeCount++;
				userPath = value;
			},
			ExecutablePathProvider = () => target,
			PathListSeparator = ';'
		});

		var result = service.InstallOrRepair();
		var commandPath = Path.Combine(temp.Path, "DevProjex", "bin", CommandLineExecutableAliases.WindowsPortableCommandFileName);

		Assert.True(result.Success);
		Assert.Equal(TerminalCommandInstallOutcome.Created, result.Outcome);
		Assert.Equal(TerminalCommandSetupState.Installed, result.Snapshot.State);
		Assert.Equal(1, writeCount);
		AssertPathListContains(userPath, Path.GetDirectoryName(commandPath)!);
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
	public void InstallOrRepair_WindowsPortableBuild_DoesNotDuplicateExistingWindowsSlashUserPathEntry()
	{
		using var temp = new TemporaryDirectory();
		var target = temp.CreateFile("portable/DevProjex.exe", "fake executable");
		var userBin = Path.Combine(temp.Path, "DevProjex", "bin");
		var userPath = userBin + "\\";
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
		Assert.Equal(userBin + "\\", userPath);
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
	public void InstallOrRepair_WindowsPortableBuild_RecognizesMachinePathWithDifferentCaseAndWindowsTrailingSlash()
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
			machinePathProvider: () => userBin.ToUpperInvariant() + "\\");

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
		var result = service.ConfigurePath();

		Assert.Equal(TerminalCommandSetupState.InstalledPathMissing, snapshot.State);
		Assert.False(snapshot.CanRepair);
		Assert.False(snapshot.UserBinDirectoryIsInPath);
		Assert.True(result.Success);
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
		Assert.Contains(WindowsTargetMarker(currentTarget), launcher, StringComparison.Ordinal);
		Assert.DoesNotContain(WindowsTargetMarker(oldTarget), launcher, StringComparison.Ordinal);
	}

	[Fact]
	public void InstallOrRepair_WindowsPortableBuild_RepairsMovedExecutableWithoutChangingReachableUserPath()
	{
		using var temp = new TemporaryDirectory();
		var oldTarget = temp.CreateFile("old version/DevProjex.exe", "old executable");
		var currentTarget = temp.CreateFile("current version/DevProjex.exe", "current executable");
		var existingTools = temp.CreateFolder("existing-tools");
		var userBin = temp.CreateFolder("DevProjex/bin");
		var commandPath = Path.Combine(userBin, CommandLineExecutableAliases.WindowsPortableCommandFileName);
		File.WriteAllText(commandPath, TerminalCommandSetupService.BuildWindowsLauncherContent(oldTarget));
		var userPath = string.Join(';', existingTools, userBin + "\\");
		var originalUserPath = userPath;
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
			currentTarget);

		var snapshot = service.Probe();
		var result = service.InstallOrRepair();
		var repairedLauncher = File.ReadAllText(commandPath);

		Assert.Equal(TerminalCommandSetupState.Stale, snapshot.State);
		Assert.True(snapshot.CanRepair);
		Assert.True(snapshot.UserBinDirectoryIsInPath);
		Assert.True(result.Success);
		Assert.Equal(TerminalCommandInstallOutcome.Repaired, result.Outcome);
		Assert.Equal(0, writeCount);
		Assert.Equal(originalUserPath, userPath);
		Assert.Equal(2, userPath.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length);
		Assert.Contains(WindowsTargetMarker(currentTarget), repairedLauncher, StringComparison.Ordinal);
		Assert.DoesNotContain(WindowsTargetMarker(oldTarget), repairedLauncher, StringComparison.Ordinal);
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
	public void Probe_UnixPathProviderThrows_KeepsInstallActionableAndInstallsWithProfileHint()
	{
		using var temp = new TemporaryDirectory();
		var target = temp.CreateFile("app/DevProjex", "fake executable");
		var service = new TerminalCommandSetupService(new TerminalCommandSetupServiceOptions
		{
			Platform = TerminalCommandHostPlatform.Linux,
			HomeDirectoryProvider = () => temp.Path,
			PathVariableProvider = () => throw new IOException("PATH unavailable"),
			ExecutablePathProvider = () => target
		});

		var snapshot = service.Probe();
		var result = service.InstallOrRepair();
		var wrapperPath = Path.Combine(temp.Path, ".local", "bin", CommandLineExecutableAliases.UnixCommand);

		Assert.Equal(TerminalCommandSetupState.NotInstalled, snapshot.State);
		Assert.True(snapshot.CanInstall);
		Assert.False(snapshot.UserBinDirectoryIsInPath);
		Assert.Contains(".local/bin", snapshot.ShellProfileHint, StringComparison.Ordinal);
		Assert.True(result.Success);
		Assert.Equal(TerminalCommandSetupState.InstalledPathMissing, result.Snapshot.State);
		Assert.False(result.Snapshot.UserBinDirectoryIsInPath);
		Assert.Contains(".local/bin", result.Snapshot.ShellProfileHint, StringComparison.Ordinal);
		Assert.True(File.Exists(wrapperPath));
	}

	[Fact]
	public void Probe_UnixPathLookup_UsesColonSeparatorAndNormalizesTrailingSlashes()
	{
		using var temp = new TemporaryDirectory();
		var target = temp.CreateFile("app/DevProjex", "fake executable");
		var home = Path.Combine("unix-home", Guid.NewGuid().ToString("N"));
		var userBin = Path.Combine(home, ".local", "bin");
		var pathValue = string.Join(':', "/usr/bin", userBin + "/", "/bin");
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
	public void Probe_MacOsPathLookup_UsesColonSeparatorByDefault()
	{
		using var temp = new TemporaryDirectory();
		var target = temp.CreateFile("app/DevProjex", "fake executable");
		var home = Path.Combine("mac-home", Guid.NewGuid().ToString("N"));
		var userBin = Path.Combine(home, ".local", "bin");
		var pathValue = string.Join(':', "/usr/local/bin", userBin, "/bin");
		var service = new TerminalCommandSetupService(new TerminalCommandSetupServiceOptions
		{
			Platform = TerminalCommandHostPlatform.MacOS,
			HomeDirectoryProvider = () => home,
			PathVariableProvider = () => pathValue,
			ExecutablePathProvider = () => target
		});

		var snapshot = service.Probe();

		Assert.True(snapshot.UserBinDirectoryIsInPath);
		Assert.Null(snapshot.ShellProfileHint);
	}

	[Fact]
	public void Probe_UnixPathLookup_PreservesWhitespaceInShadowingDirectory()
	{
		if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
			return;

		using var temp = new TemporaryDirectory();
		var target = temp.CreateFile("app/DevProjex", "fake executable");
		var shadowDirectory = temp.CreateFolder("shadow ");
		var shadowCommand = temp.CreateFile("shadow /devprojex", "#!/bin/sh\nexit 0\n");
		var userBin = temp.CreateFolder(".local/bin");
		var managedCommand = temp.CreateFile(
			".local/bin/devprojex",
			TerminalCommandSetupService.BuildWrapperContent(target));
		var executableMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
		File.SetUnixFileMode(shadowCommand, executableMode);
		File.SetUnixFileMode(managedCommand, executableMode);
		var service = new TerminalCommandSetupService(new TerminalCommandSetupServiceOptions
		{
			Platform = OperatingSystem.IsMacOS()
				? TerminalCommandHostPlatform.MacOS
				: TerminalCommandHostPlatform.Linux,
			HomeDirectoryProvider = () => temp.Path,
			PathVariableProvider = () => string.Join(Path.PathSeparator, shadowDirectory, userBin),
			ExecutablePathProvider = () => target
		});

		var snapshot = service.Probe();

		Assert.Equal(TerminalCommandSetupState.CommandShadowed, snapshot.State);
		Assert.Equal(shadowCommand, snapshot.ResolvedCommandPath);
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
		SetUnixExecutableMode(wrapperPath);
		var service = CreateService(TerminalCommandHostPlatform.Linux, temp.Path, userBin, target);

		var snapshot = service.Probe();
		var install = service.InstallOrRepair();

		Assert.Equal(TerminalCommandSetupState.Installed, snapshot.State);
		Assert.True(snapshot.IsReady);
		Assert.True(install.Success);
		Assert.Equal(TerminalCommandInstallOutcome.AlreadyInstalled, install.Outcome);
	}

	[Fact]
	public void Probe_UnixInstalledWrapperMissingFromPath_ReturnsExplicitRecoverableState()
	{
		using var temp = new TemporaryDirectory();
		var target = temp.CreateFile("app/DevProjex", "fake executable");
		var userBin = temp.CreateFolder(".local/bin");
		var wrapperPath = Path.Combine(userBin, CommandLineExecutableAliases.UnixCommand);
		File.WriteAllText(wrapperPath, TerminalCommandSetupService.BuildWrapperContent(target));
		SetUnixExecutableMode(wrapperPath);
		var service = CreateService(
			TerminalCommandHostPlatform.Linux,
			temp.Path,
			Path.Combine(temp.Path, "other-bin"),
			target);

		var snapshot = service.Probe();
		var install = service.InstallOrRepair();

		Assert.Equal(TerminalCommandSetupState.InstalledPathMissing, snapshot.State);
		Assert.False(snapshot.IsReady);
		Assert.False(snapshot.UserBinDirectoryIsInPath);
		Assert.Contains(".local/bin", snapshot.ShellProfileHint, StringComparison.Ordinal);
		Assert.True(install.Success);
		Assert.Equal(TerminalCommandInstallOutcome.AlreadyInstalled, install.Outcome);
	}

	[Theory]
	[InlineData("/bin/bash", ".bashrc", "grep -qxF")]
	[InlineData("/usr/bin/zsh", ".zshrc", "grep -qxF")]
	[InlineData("/usr/bin/fish", "fish_add_path", "fish_add_path")]
	[InlineData(null, ".profile", "grep -qxF")]
	public void Probe_UnixPathMissing_ProvidesIdempotentCommandForDetectedShell(
		string? shellPath,
		string expectedProfileMarker,
		string expectedCommandMarker)
	{
		using var temp = new TemporaryDirectory();
		var target = temp.CreateFile("app/DevProjex", "fake executable");
		var userBin = temp.CreateFolder(".local/bin");
		var wrapperPath = Path.Combine(userBin, CommandLineExecutableAliases.UnixCommand);
		File.WriteAllText(wrapperPath, TerminalCommandSetupService.BuildWrapperContent(target));
		SetUnixExecutableMode(wrapperPath);
		var service = new TerminalCommandSetupService(new TerminalCommandSetupServiceOptions
		{
			Platform = TerminalCommandHostPlatform.Linux,
			HomeDirectoryProvider = () => temp.Path,
			PathVariableProvider = () => Path.Combine(temp.Path, "other-bin"),
			ShellPathProvider = () => shellPath,
			ExecutablePathProvider = () => target
		});

		var snapshot = service.Probe();

		Assert.Equal(TerminalCommandSetupState.InstalledPathMissing, snapshot.State);
		Assert.Contains(expectedProfileMarker, snapshot.PathSetupCommand, StringComparison.Ordinal);
		Assert.Contains(expectedCommandMarker, snapshot.PathSetupCommand, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("/bin/bash", ".bashrc", "case \":$PATH:\" in *\":$HOME/.local/bin:\"*) ;; *) export PATH=\"$HOME/.local/bin:$PATH\" ;; esac")]
	[InlineData("/usr/bin/zsh", ".zshrc", "case \":$PATH:\" in *\":$HOME/.local/bin:\"*) ;; *) export PATH=\"$HOME/.local/bin:$PATH\" ;; esac")]
	[InlineData("/usr/bin/fish", ".config/fish/config.fish", "fish_add_path --move \"$HOME/.local/bin\"")]
	[InlineData(null, ".profile", "case \":$PATH:\" in *\":$HOME/.local/bin:\"*) ;; *) export PATH=\"$HOME/.local/bin:$PATH\" ;; esac")]
	public void ConfigurePath_UnixShells_PreservesProfileAndIsIdempotent(
		string? shellPath,
		string relativeProfilePath,
		string expectedSetupLine)
	{
		using var temp = new TemporaryDirectory();
		var target = temp.CreateFile("app/DevProjex", "fake executable");
		var userBin = temp.CreateFolder(".local/bin");
		var wrapperPath = Path.Combine(userBin, CommandLineExecutableAliases.UnixCommand);
		File.WriteAllText(wrapperPath, TerminalCommandSetupService.BuildWrapperContent(target));
		SetUnixExecutableMode(wrapperPath);
		var profilePath = Path.Combine(temp.Path, relativeProfilePath.Replace('/', Path.DirectorySeparatorChar));
		Directory.CreateDirectory(Path.GetDirectoryName(profilePath)!);
		File.WriteAllText(profilePath, "# Existing user configuration\n");
		var processPath = Path.Combine(temp.Path, "other-bin");
		var service = CreateUnixPathSetupService(temp.Path, target, shellPath, () => processPath, value => processPath = value);

		var first = service.ConfigurePath();
		var contentAfterFirstRun = File.ReadAllText(profilePath);
		var second = service.ConfigurePath();
		var contentAfterSecondRun = File.ReadAllText(profilePath);

		Assert.True(first.Success, first.ErrorMessage);
		Assert.Equal(TerminalCommandSetupState.Installed, first.Snapshot.State);
		Assert.True(second.Success, second.ErrorMessage);
		Assert.Equal(TerminalCommandSetupState.Installed, second.Snapshot.State);
		Assert.Contains("# Existing user configuration", contentAfterFirstRun, StringComparison.Ordinal);
		Assert.Equal(1, CountOccurrences(contentAfterFirstRun, "# DevProjex terminal PATH"));
		Assert.Equal(1, CountOccurrences(contentAfterFirstRun, expectedSetupLine));
		Assert.Equal(contentAfterFirstRun, contentAfterSecondRun);
		Assert.True(service.Probe().UserBinDirectoryIsInPath);
	}

	[Fact]
	public void ConfigurePath_ExistingEquivalentProfileLine_DoesNotRewriteUserProfile()
	{
		using var temp = new TemporaryDirectory();
		var target = temp.CreateFile("app/DevProjex", "fake executable");
		var userBin = temp.CreateFolder(".local/bin");
		var wrapperPath = Path.Combine(userBin, CommandLineExecutableAliases.UnixCommand);
		File.WriteAllText(wrapperPath, TerminalCommandSetupService.BuildWrapperContent(target));
		SetUnixExecutableMode(wrapperPath);
		const string originalProfile = "# Managed by the user\ncase \":$PATH:\" in *\":$HOME/.local/bin:\"*) ;; *) export PATH=\"$HOME/.local/bin:$PATH\" ;; esac\n";
		File.WriteAllText(Path.Combine(temp.Path, ".bashrc"), originalProfile);
		var processPath = Path.Combine(temp.Path, "other-bin");
		var service = CreateUnixPathSetupService(temp.Path, target, "/bin/bash", () => processPath, value => processPath = value);

		var result = service.ConfigurePath();

		Assert.True(result.Success, result.ErrorMessage);
		Assert.Equal(originalProfile, File.ReadAllText(Path.Combine(temp.Path, ".bashrc")));
		Assert.Equal(TerminalCommandSetupState.Installed, result.Snapshot.State);
	}

	[Fact]
	public void ConfigurePath_ProfileCannotBeOpened_DoesNotMutateCurrentProcessPath()
	{
		using var temp = new TemporaryDirectory();
		var target = temp.CreateFile("app/DevProjex", "fake executable");
		var userBin = temp.CreateFolder(".local/bin");
		var wrapperPath = Path.Combine(userBin, CommandLineExecutableAliases.UnixCommand);
		File.WriteAllText(wrapperPath, TerminalCommandSetupService.BuildWrapperContent(target));
		SetUnixExecutableMode(wrapperPath);
		temp.CreateFolder(".bashrc");
		var originalPath = Path.Combine(temp.Path, "other-bin");
		var processPath = originalPath;
		var writeCount = 0;
		var service = CreateUnixPathSetupService(
			temp.Path,
			target,
			"/bin/bash",
			() => processPath,
			value =>
			{
				writeCount++;
				processPath = value;
			});

		var result = service.ConfigurePath();

		Assert.False(result.Success);
		Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));
		Assert.Equal(0, writeCount);
		Assert.Equal(originalPath, processPath);
		Assert.Equal(TerminalCommandSetupState.InstalledPathMissing, result.Snapshot.State);
	}

	[Fact]
	public void ConfigurePath_OversizedShellProfile_DoesNotReadOrMutateIt()
	{
		using var temp = new TemporaryDirectory();
		var target = temp.CreateFile("app/DevProjex", "fake executable");
		var userBin = temp.CreateFolder(".local/bin");
		var wrapperPath = Path.Combine(userBin, CommandLineExecutableAliases.UnixCommand);
		File.WriteAllText(wrapperPath, TerminalCommandSetupService.BuildWrapperContent(target));
		SetUnixExecutableMode(wrapperPath);
		var profilePath = Path.Combine(temp.Path, ".bashrc");
		using (var profile = File.Create(profilePath))
			profile.SetLength(TerminalCommandSetupService.MaximumShellProfileBytes + 1L);
		var originalPath = Path.Combine(temp.Path, "other-bin");
		var processPath = originalPath;
		var writeCount = 0;
		var service = CreateUnixPathSetupService(
			temp.Path,
			target,
			"/bin/bash",
			() => processPath,
			value =>
			{
				writeCount++;
				processPath = value;
			});

		var result = service.ConfigurePath();

		Assert.False(result.Success);
		Assert.Contains("shell profile exceeds", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
		Assert.Equal(TerminalCommandSetupService.MaximumShellProfileBytes + 1L, new FileInfo(profilePath).Length);
		Assert.Equal(0, writeCount);
		Assert.Equal(originalPath, processPath);
	}

	[Fact]
	public void ConfigurePath_WindowsPortableLauncher_IsRejectedWithoutChangingPath()
	{
		using var temp = new TemporaryDirectory();
		var target = temp.CreateFile("app/DevProjex.exe", "fake executable");
		var writeCount = 0;
		var service = new TerminalCommandSetupService(new TerminalCommandSetupServiceOptions
		{
			Platform = TerminalCommandHostPlatform.Windows,
			IsWindowsPackagedApp = () => false,
			LocalAppDataPathProvider = () => temp.Path,
			PathVariableProvider = () => string.Empty,
			ProcessPathVariableWriter = _ => writeCount++,
			UserPathVariableProvider = () => string.Empty,
			MachinePathVariableProvider = () => string.Empty,
			UserPathVariableWriter = _ => { },
			ExecutablePathProvider = () => target
		});

		var result = service.ConfigurePath();

		Assert.False(result.Success);
		Assert.Equal(0, writeCount);
		Assert.Equal(TerminalCommandSetupState.NotInstalled, result.Snapshot.State);
	}

	[Fact]
	public void Probe_UnixManagedWrapperWithoutExecutePermission_ReturnsRepairableStale()
	{
		if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
			return;

		using var temp = new TemporaryDirectory();
		var target = temp.CreateFile("app/DevProjex", "fake executable");
		var userBin = temp.CreateFolder(".local/bin");
		var wrapperPath = Path.Combine(userBin, CommandLineExecutableAliases.UnixCommand);
		File.WriteAllText(wrapperPath, TerminalCommandSetupService.BuildWrapperContent(target));
		File.SetUnixFileMode(
			wrapperPath,
			UnixFileMode.UserRead | UnixFileMode.UserWrite |
			UnixFileMode.GroupRead | UnixFileMode.OtherRead);
		var service = CreateService(TerminalCommandHostPlatform.Linux, temp.Path, userBin, target);

		var snapshot = service.Probe();

		Assert.Equal(TerminalCommandSetupState.Stale, snapshot.State);
		Assert.True(snapshot.CanRepair);
		Assert.False(snapshot.IsReady);
	}

	[Fact]
	public void Probe_UnixLegacyManagedWrapperWithoutShebang_ReturnsRepairableStale()
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

		Assert.Equal(TerminalCommandSetupState.Stale, snapshot.State);
		Assert.True(snapshot.CanRepair);
		Assert.False(snapshot.IsReady);
	}

	[Fact]
	public void Probe_UnixManagedWrapperWithCorruptedBody_ReturnsRepairableStale()
	{
		using var temp = new TemporaryDirectory();
		var target = temp.CreateFile("app/DevProjex", "fake executable");
		var userBin = temp.CreateFolder(".local/bin");
		var wrapperPath = Path.Combine(userBin, CommandLineExecutableAliases.UnixCommand);
		File.WriteAllText(
			wrapperPath,
			"#!/bin/sh\n# DevProjex terminal command wrapper\n# target: " + target + "\necho broken\n");
		var service = CreateService(TerminalCommandHostPlatform.Linux, temp.Path, userBin, target);

		var snapshot = service.Probe();

		Assert.Equal(TerminalCommandSetupState.Stale, snapshot.State);
		Assert.True(snapshot.CanRepair);
	}

	[Fact]
	public void Probe_OversizedLauncherWithManagedHeader_RemainsAProtectedConflict()
	{
		using var temp = new TemporaryDirectory();
		var target = temp.CreateFile("app/DevProjex", "fake executable");
		var userBin = temp.CreateFolder(".local/bin");
		var wrapperPath = Path.Combine(userBin, CommandLineExecutableAliases.UnixCommand);
		File.WriteAllText(
			wrapperPath,
			TerminalCommandSetupService.BuildWrapperContent(target) + new string('x', 64 * 1024));
		var service = CreateService(TerminalCommandHostPlatform.Linux, temp.Path, userBin, target);

		var snapshot = service.Probe();
		var install = service.InstallOrRepair();

		Assert.Equal(TerminalCommandSetupState.ConflictingCommand, snapshot.State);
		Assert.False(snapshot.CanRepair);
		Assert.False(install.Success);
		Assert.Equal(TerminalCommandInstallOutcome.ConflictingCommand, install.Outcome);
	}

	[Fact]
	public void ValidateLauncher_DotnetHostCompletesVersionCheck()
	{
		var result = TerminalCommandSetupService.ValidateLauncher("dotnet", TimeSpan.FromSeconds(5));

		Assert.True(result.Success, result.ErrorMessage);
	}

	[Fact]
	public void ValidateLauncher_WindowsCommandPathWithSpaces_CompletesVersionCheck()
	{
		if (!OperatingSystem.IsWindows())
			return;

		using var temp = new TemporaryDirectory();
		var commandPath = Path.GetFullPath(temp.CreateFile(
			"folder with spaces/devprojex.cmd",
			"@echo off\r\ndotnet --version\r\nexit /b %ERRORLEVEL%\r\n"));

		var result = TerminalCommandSetupService.ValidateLauncher(commandPath, TimeSpan.FromSeconds(5));

		Assert.True(result.Success, result.ErrorMessage);
	}

	[Fact]
	public void ValidateLauncher_UnixWrapperWithExecutableTarget_CompletesVersionCheck()
	{
		if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
			return;

		using var temp = new TemporaryDirectory();
		var target = temp.CreateFile("app with spaces/DevProjex", "#!/bin/sh\necho 1.0.0\n");
		var wrapper = temp.CreateFile("bin/devprojex", TerminalCommandSetupService.BuildWrapperContent(target));
		var executableMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
		File.SetUnixFileMode(target, executableMode);
		File.SetUnixFileMode(wrapper, executableMode);

		var result = TerminalCommandSetupService.ValidateLauncher(wrapper, TimeSpan.FromSeconds(5));

		Assert.True(result.Success, result.ErrorMessage);
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
	public void InstallOrRepair_UnixStaleWrapper_RepairsEvenWhenUserBinIsMissingFromPath()
	{
		using var temp = new TemporaryDirectory();
		var currentTarget = temp.CreateFile("current app/DevProjex", "fake executable");
		var oldTarget = temp.CreateFile("old app/DevProjex", "old executable");
		var userBin = temp.CreateFolder(".local/bin");
		var commandPath = Path.Combine(userBin, CommandLineExecutableAliases.UnixCommand);
		File.WriteAllText(commandPath, TerminalCommandSetupService.BuildWrapperContent(oldTarget));
		var service = CreateService(
			TerminalCommandHostPlatform.Linux,
			temp.Path,
			Path.Combine(temp.Path, "other-bin"),
			currentTarget);

		var stale = service.Probe();
		var result = service.InstallOrRepair();
		var wrapper = File.ReadAllText(commandPath);

		Assert.Equal(TerminalCommandSetupState.Stale, stale.State);
		Assert.False(stale.UserBinDirectoryIsInPath);
		Assert.True(result.Success);
		Assert.Equal(TerminalCommandInstallOutcome.Repaired, result.Outcome);
		Assert.Equal(TerminalCommandSetupState.InstalledPathMissing, result.Snapshot.State);
		Assert.False(result.Snapshot.UserBinDirectoryIsInPath);
		Assert.Contains(".local/bin", result.Snapshot.ShellProfileHint, StringComparison.Ordinal);
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
	public void ConfigurePath_BashUpdatesInteractiveAndExistingLoginProfilesWithoutCreatingUnusedProfile()
	{
		using var temp = new TemporaryDirectory();
		var target = temp.CreateFile("app/DevProjex", "fake executable");
		var userBin = temp.CreateFolder(".local/bin");
		var wrapperPath = Path.Combine(userBin, CommandLineExecutableAliases.UnixCommand);
		File.WriteAllText(wrapperPath, TerminalCommandSetupService.BuildWrapperContent(target));
		SetUnixExecutableMode(wrapperPath);
		var loginProfile = temp.CreateFile(".bash_profile", "# Login settings\n");
		var processPath = temp.CreateFolder("system-bin");
		var service = CreateUnixPathSetupService(temp.Path, target, "/bin/bash", () => processPath, value => processPath = value);

		var result = service.ConfigurePath();

		Assert.True(result.Success, result.ErrorMessage);
		Assert.Contains("# DevProjex terminal PATH", File.ReadAllText(loginProfile), StringComparison.Ordinal);
		Assert.Contains("# DevProjex terminal PATH", File.ReadAllText(Path.Combine(temp.Path, ".bashrc")), StringComparison.Ordinal);
		Assert.False(File.Exists(Path.Combine(temp.Path, ".profile")));
	}

	[Fact]
	public void ConfigurePath_FishHonorsAbsoluteXdgConfigHome()
	{
		using var temp = new TemporaryDirectory();
		var target = temp.CreateFile("app/DevProjex", "fake executable");
		var userBin = temp.CreateFolder(".local/bin");
		var wrapperPath = Path.Combine(userBin, CommandLineExecutableAliases.UnixCommand);
		File.WriteAllText(wrapperPath, TerminalCommandSetupService.BuildWrapperContent(target));
		SetUnixExecutableMode(wrapperPath);
		var xdgConfigHome = temp.CreateFolder("custom-config");
		var processPath = temp.CreateFolder("system-bin");
		var service = CreateUnixPathSetupService(temp.Path, target, "/usr/bin/fish", () => processPath, value => processPath = value, xdgConfigHome: xdgConfigHome);

		var result = service.ConfigurePath();

		var profile = Path.Combine(xdgConfigHome, "fish", "config.fish");
		Assert.True(result.Success, result.ErrorMessage);
		Assert.Contains("fish_add_path --move", File.ReadAllText(profile), StringComparison.Ordinal);
		Assert.False(File.Exists(Path.Combine(temp.Path, ".config", "fish", "config.fish")));
	}

	[Fact]
	public void ConfigurePath_ZshHonorsExportedZdotDirectory()
	{
		using var temp = new TemporaryDirectory();
		var target = temp.CreateFile("app/DevProjex", "fake executable");
		var userBin = temp.CreateFolder(".local/bin");
		var wrapperPath = Path.Combine(userBin, CommandLineExecutableAliases.UnixCommand);
		File.WriteAllText(wrapperPath, TerminalCommandSetupService.BuildWrapperContent(target));
		SetUnixExecutableMode(wrapperPath);
		var zdotDirectory = temp.CreateFolder("zsh-config");
		var processPath = temp.CreateFolder("system-bin");
		var service = CreateUnixPathSetupService(temp.Path, target, "/usr/bin/zsh", () => processPath, value => processPath = value, zdotDirectory: zdotDirectory);

		var result = service.ConfigurePath();

		Assert.True(result.Success, result.ErrorMessage);
		Assert.Contains("# DevProjex terminal PATH", File.ReadAllText(Path.Combine(zdotDirectory, ".zshrc")), StringComparison.Ordinal);
		Assert.False(File.Exists(Path.Combine(temp.Path, ".zshrc")));
	}

	[Fact]
	public void ConfigurePath_UnixShadowedCommandMovesManagedLauncherToFirstPathPosition()
	{
		using var temp = new TemporaryDirectory();
		var target = temp.CreateFile("app/DevProjex", "fake executable");
		var userBin = temp.CreateFolder(".local/bin");
		var wrapperPath = Path.Combine(userBin, CommandLineExecutableAliases.UnixCommand);
		File.WriteAllText(wrapperPath, TerminalCommandSetupService.BuildWrapperContent(target));
		SetUnixExecutableMode(wrapperPath);
		var foreignBin = temp.CreateFolder("foreign-bin");
		var foreignCommand = temp.CreateFile("foreign-bin/devprojex", "foreign command");
		SetUnixExecutableMode(foreignCommand);
		var processPath = string.Join(Path.PathSeparator, foreignBin, userBin);
		var service = CreateUnixPathSetupService(temp.Path, target, "/bin/bash", () => processPath, value => processPath = value);

		var before = service.Probe();
		var result = service.ConfigurePath();

		Assert.Equal(TerminalCommandSetupState.CommandShadowed, before.State);
		Assert.Equal(
			NormalizeForPathListAssert(foreignCommand),
			NormalizeForPathListAssert(before.ResolvedCommandPath!));
		Assert.True(result.Success, result.ErrorMessage);
		Assert.Equal(TerminalCommandSetupState.Installed, result.Snapshot.State);
		Assert.Equal(
			NormalizeForPathListAssert(userBin),
			NormalizeForPathListAssert(processPath.Split(Path.PathSeparator)[0]));
	}

	[Fact]
	public void ConfigurePath_WindowsShadowedUserCommandMovesManagedLauncherFirst()
	{
		using var temp = new TemporaryDirectory();
		var target = temp.CreateFile("portable/DevProjex.exe", "fake executable");
		var userBin = temp.CreateFolder("DevProjex/bin");
		var commandPath = Path.Combine(userBin, CommandLineExecutableAliases.WindowsPortableCommandFileName);
		File.WriteAllText(commandPath, TerminalCommandSetupService.BuildWindowsLauncherContent(target));
		var foreignBin = temp.CreateFolder("foreign-bin");
		var foreignCommand = temp.CreateFile("foreign-bin/devprojex.exe", "foreign command");
		var userPath = string.Join(';', foreignBin, userBin);
		var service = CreateWindowsPortableService(temp.Path, string.Empty, () => userPath, value => userPath = value, target);

		var before = service.Probe();
		var result = service.ConfigurePath();

		Assert.Equal(TerminalCommandSetupState.CommandShadowed, before.State);
		Assert.Equal(
			NormalizeForPathListAssert(foreignCommand),
			NormalizeForPathListAssert(before.ResolvedCommandPath!),
			ignoreCase: true);
		Assert.True(result.Success, result.ErrorMessage);
		Assert.Equal(TerminalCommandSetupState.Installed, result.Snapshot.State);
		Assert.Equal(
			NormalizeForPathListAssert(userBin),
			NormalizeForPathListAssert(userPath.Split(';')[0]),
			ignoreCase: true);
	}

	[Fact]
	public void ConfigurePath_WindowsMachineCommandCannotBeHiddenByUserPathAndReportsFailure()
	{
		using var temp = new TemporaryDirectory();
		var target = temp.CreateFile("portable/DevProjex.exe", "fake executable");
		var userBin = temp.CreateFolder("DevProjex/bin");
		var commandPath = Path.Combine(userBin, CommandLineExecutableAliases.WindowsPortableCommandFileName);
		File.WriteAllText(commandPath, TerminalCommandSetupService.BuildWindowsLauncherContent(target));
		var machineBin = temp.CreateFolder("machine-bin");
		var foreignCommand = temp.CreateFile("machine-bin/devprojex.exe", "foreign command");
		var userPath = userBin;
		var service = CreateWindowsPortableService(temp.Path, string.Empty, () => userPath, value => userPath = value, target, machinePathProvider: () => machineBin);

		var before = service.Probe();
		var result = service.ConfigurePath();

		Assert.Equal(TerminalCommandSetupState.CommandShadowed, before.State);
		Assert.Equal(
			NormalizeForPathListAssert(foreignCommand),
			NormalizeForPathListAssert(before.ResolvedCommandPath!),
			ignoreCase: true);
		Assert.False(result.Success);
		Assert.Equal(TerminalCommandSetupState.CommandShadowed, result.Snapshot.State);
	}

	[Fact]
	public void Probe_WindowsPathWithoutCmdExtensionDoesNotReportLauncherAsReady()
	{
		using var temp = new TemporaryDirectory();
		var target = temp.CreateFile("portable/DevProjex.exe", "fake executable");
		var userBin = temp.CreateFolder("DevProjex/bin");
		var commandPath = Path.Combine(userBin, CommandLineExecutableAliases.WindowsPortableCommandFileName);
		File.WriteAllText(commandPath, TerminalCommandSetupService.BuildWindowsLauncherContent(target));
		var service = new TerminalCommandSetupService(new TerminalCommandSetupServiceOptions
		{
			Platform = TerminalCommandHostPlatform.Windows,
			IsWindowsPackagedApp = () => false,
			LocalAppDataPathProvider = () => temp.Path,
			UserPathVariableProvider = () => userBin,
			MachinePathVariableProvider = () => string.Empty,
			PathExtensionsProvider = () => ".EXE",
			ExecutablePathProvider = () => target,
			PathListSeparator = ';'
		});

		var snapshot = service.Probe();

		Assert.Equal(TerminalCommandSetupState.CommandShadowed, snapshot.State);
		Assert.Null(snapshot.ResolvedCommandPath);
		Assert.False(snapshot.IsReady);
	}

	[Fact]
	public void ConfigurePath_ProcessPathWriterNoOpDoesNotReportFalseSuccess()
	{
		using var temp = new TemporaryDirectory();
		var target = temp.CreateFile("app/DevProjex", "fake executable");
		var userBin = temp.CreateFolder(".local/bin");
		var wrapperPath = Path.Combine(userBin, CommandLineExecutableAliases.UnixCommand);
		File.WriteAllText(wrapperPath, TerminalCommandSetupService.BuildWrapperContent(target));
		SetUnixExecutableMode(wrapperPath);
		var originalPath = temp.CreateFolder("system-bin");
		var service = CreateUnixPathSetupService(temp.Path, target, "/bin/bash", () => originalPath, _ => { });

		var result = service.ConfigurePath();

		Assert.False(result.Success);
		Assert.Equal(TerminalCommandSetupState.InstalledPathMissing, result.Snapshot.State);
		Assert.Contains("still not the resolved", result.ErrorMessage, StringComparison.Ordinal);
	}

	[Fact]
	public void Probe_LockedManagedWrapperReportsIoFailureInsteadOfForeignConflict()
	{
		using var temp = new TemporaryDirectory();
		var target = temp.CreateFile("portable/DevProjex.exe", "fake executable");
		var userBin = temp.CreateFolder("DevProjex/bin");
		var commandPath = Path.Combine(userBin, CommandLineExecutableAliases.WindowsPortableCommandFileName);
		File.WriteAllText(commandPath, TerminalCommandSetupService.BuildWindowsLauncherContent(target));
		using var lockStream = new FileStream(commandPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
		var service = CreateWindowsPortableService(temp.Path, string.Empty, () => userBin, _ => { }, target);

		var snapshot = service.Probe();

		Assert.Equal(TerminalCommandSetupState.Failed, snapshot.State);
		Assert.NotEqual(TerminalCommandSetupState.ConflictingCommand, snapshot.State);
	}

	[Fact]
	public async Task InstallOrRepair_ConcurrentCallsLeaveOneCompleteManagedLauncherWithoutTempFiles()
	{
		using var temp = new TemporaryDirectory();
		var target = temp.CreateFile("portable/DevProjex.exe", "fake executable");
		var pathSync = new object();
		var userPath = string.Empty;
		var service = CreateWindowsPortableService(
			temp.Path,
			string.Empty,
			() => { lock (pathSync) return userPath; },
			value => { lock (pathSync) userPath = value; },
			target);

		var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(service.InstallOrRepair)));
		var commandDirectory = Path.Combine(temp.Path, "DevProjex", "bin");
		var commandPath = Path.Combine(commandDirectory, CommandLineExecutableAliases.WindowsPortableCommandFileName);

		Assert.All(results, result => Assert.True(result.Success, result.ErrorMessage));
		Assert.Equal(TerminalCommandSetupService.BuildWindowsLauncherContent(target), File.ReadAllText(commandPath));
		Assert.Empty(Directory.GetFiles(commandDirectory, ".devprojex.*.tmp"));
		Assert.Equal(TerminalCommandSetupState.Installed, service.Probe().State);
	}

	[Fact]
	public async Task Reinstall_ConcurrentServiceInstancesAtomicallyReplaceLauncherWithoutReaderConflicts()
	{
		using var temp = new TemporaryDirectory();
		var target = temp.CreateFile("portable/DevProjex.exe", "fake executable");
		var userBin = temp.CreateFolder("DevProjex/bin");
		var commandPath = Path.Combine(userBin, CommandLineExecutableAliases.WindowsPortableCommandFileName);
		File.WriteAllText(commandPath, TerminalCommandSetupService.BuildWindowsLauncherContent(target));
		var services = Enumerable.Range(0, 32)
			.Select(_ => CreateWindowsPortableService(
				temp.Path,
				processPath: string.Empty,
				() => userBin,
				_ => throw new InvalidOperationException("Installed PATH must not be rewritten."),
				target))
			.ToArray();

		var results = await Task.WhenAll(services.Select(service => Task.Run(service.Reinstall)));

		Assert.All(results, result => Assert.True(result.Success, result.ErrorMessage));
		Assert.All(results, result => Assert.Equal(TerminalCommandInstallOutcome.Reinstalled, result.Outcome));
		Assert.Equal(TerminalCommandSetupService.BuildWindowsLauncherContent(target), File.ReadAllText(commandPath));
		Assert.Empty(Directory.GetFiles(userBin, ".devprojex.*.tmp"));
		Assert.Equal(TerminalCommandSetupState.Installed, services[0].Probe().State);
	}

	[Fact]
	public void Probe_UnsupportedPlatform_ReturnsNonActionableSnapshotAndInstallIsNotSupported()
	{
		using var temp = new TemporaryDirectory();
		var target = temp.CreateFile("app/DevProjex", "fake executable");
		var service = CreateService(TerminalCommandHostPlatform.Other, temp.Path, temp.Path, target);

		var snapshot = service.Probe();
		var install = service.InstallOrRepair();

		Assert.Equal(TerminalCommandSetupState.UnsupportedOnCurrentPlatform, snapshot.State);
		Assert.False(snapshot.IsActionable);
		Assert.False(snapshot.IsReady);
		Assert.False(install.Success);
		Assert.Equal(TerminalCommandInstallOutcome.NotSupported, install.Outcome);
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
	public void BuildWrapperContent_UsesLfOnlyAndPreservesUnicodeSpaceAndApostropheTarget()
	{
		var target = "/home/me/Dev Projex's builds/Приложение/DevProjex";

		var wrapper = TerminalCommandSetupService.BuildWrapperContent(target);

		Assert.DoesNotContain("\r", wrapper, StringComparison.Ordinal);
		Assert.StartsWith("#!/bin/sh\n", wrapper, StringComparison.Ordinal);
		Assert.EndsWith("\n", wrapper, StringComparison.Ordinal);
		Assert.Contains("# target: " + target, wrapper, StringComparison.Ordinal);
		Assert.Contains("exec '/home/me/Dev Projex'\"'\"'s builds/Приложение/DevProjex' \"$@\"", wrapper, StringComparison.Ordinal);
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
	public void PromptPolicy_DismissedNotInstalledPromptIsSuppressedAndStaleRepairIsAutomatic()
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
		Assert.False(TerminalCommandPromptPolicy.ShouldOfferAutomaticPrompt(settings, stale, startedWithProjectPath: false));
		Assert.True(TerminalCommandPromptPolicy.ShouldRepairAutomatically(stale));
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

	[Theory]
	[InlineData((int)TerminalCommandSetupState.ManagedByOperatingSystem, false, false, false, false, false, false)]
	[InlineData((int)TerminalCommandSetupState.UnsupportedOnCurrentPackage, false, false, false, false, false, false)]
	[InlineData((int)TerminalCommandSetupState.UnsupportedOnCurrentPlatform, false, false, false, false, false, false)]
	[InlineData((int)TerminalCommandSetupState.HomeDirectoryUnavailable, false, false, false, false, false, false)]
	[InlineData((int)TerminalCommandSetupState.NotInstalled, true, false, false, false, true, true)]
	[InlineData((int)TerminalCommandSetupState.NotInstalled, true, false, true, false, false, true)]
	[InlineData((int)TerminalCommandSetupState.NotInstalled, false, false, false, false, false, true)]
	[InlineData((int)TerminalCommandSetupState.Installed, false, false, false, false, false, false)]
	[InlineData((int)TerminalCommandSetupState.InstalledPathMissing, false, false, false, false, true, true)]
	[InlineData((int)TerminalCommandSetupState.InstalledPathMissing, false, false, true, false, false, true)]
	[InlineData((int)TerminalCommandSetupState.CommandShadowed, false, false, false, false, true, true)]
	[InlineData((int)TerminalCommandSetupState.CommandShadowed, false, false, true, false, false, true)]
	[InlineData((int)TerminalCommandSetupState.Stale, false, true, false, false, false, false)]
	[InlineData((int)TerminalCommandSetupState.Stale, false, true, true, false, false, false)]
	[InlineData((int)TerminalCommandSetupState.Stale, false, false, false, false, false, false)]
	[InlineData((int)TerminalCommandSetupState.ConflictingCommand, false, false, false, false, false, false)]
	[InlineData((int)TerminalCommandSetupState.PermissionDenied, false, false, false, false, false, false)]
	[InlineData((int)TerminalCommandSetupState.Failed, false, false, false, false, false, false)]
	[InlineData((int)TerminalCommandSetupState.NotInstalled, true, false, false, true, false, true)]
	[InlineData((int)TerminalCommandSetupState.Stale, false, true, false, true, false, false)]
	public void PromptPolicy_StateMatrix_AllowsOnlyActionableNotInstalledOrStaleRepair(
		int stateValue,
		bool canInstall,
		bool canRepair,
		bool dismissed,
		bool startedWithProjectPath,
		bool expectedOffer,
		bool expectedDismissible)
	{
		var state = (TerminalCommandSetupState)stateValue;
		var settings = new AppViewSettings { IsTerminalCommandPromptDismissed = dismissed };
		var snapshot = new TerminalCommandSetupSnapshot(
			CommandLineExecutableAliases.UnixCommand,
			state,
			CommandPath: "/home/me/.local/bin/devprojex",
			TargetExecutablePath: "/opt/DevProjex/DevProjex",
			InstalledTargetExecutablePath: state == TerminalCommandSetupState.Stale ? "/old/DevProjex" : null,
			UserBinDirectory: "/home/me/.local/bin",
			UserBinDirectoryIsInPath: true,
			CanInstall: canInstall,
			CanRepair: canRepair,
			ShellProfileHint: null);

		var actualOffer = TerminalCommandPromptPolicy.ShouldOfferAutomaticPrompt(
			settings,
			snapshot,
			startedWithProjectPath);

		Assert.Equal(expectedOffer, actualOffer);
		Assert.Equal(expectedDismissible, TerminalCommandPromptPolicy.IsDismissibleAutomaticPrompt(snapshot));
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
		Func<string?>? machinePathProvider = null,
		Func<string, TimeSpan, TerminalCommandValidationResult>? launcherValidator = null)
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
			LauncherValidator = launcherValidator ?? ((_, _) => new TerminalCommandValidationResult(true)),
			PathListSeparator = ';'
		});
	}

	private static TerminalCommandSetupService CreateUnixPathSetupService(
		string home,
		string executablePath,
		string? shellPath,
		Func<string?> pathProvider,
		Action<string> pathWriter,
		string? xdgConfigHome = null,
		string? zdotDirectory = null)
	{
		return new TerminalCommandSetupService(new TerminalCommandSetupServiceOptions
		{
			Platform = TerminalCommandHostPlatform.Linux,
			HomeDirectoryProvider = () => home,
			PathVariableProvider = pathProvider,
			ProcessPathVariableWriter = pathWriter,
			ShellPathProvider = () => shellPath,
			XdgConfigHomeProvider = () => xdgConfigHome,
			ZdotDirectoryProvider = () => zdotDirectory,
			ExecutablePathProvider = () => executablePath,
			// The simulation uses real host paths, so their list separator must match the host filesystem.
			PathListSeparator = Path.PathSeparator
		});
	}

	private static int CountOccurrences(string value, string search)
	{
		var count = 0;
		var index = 0;
		while ((index = value.IndexOf(search, index, StringComparison.Ordinal)) >= 0)
		{
			count++;
			index += search.Length;
		}

		return count;
	}

	private static string WindowsTargetMarker(string targetPath) =>
		"rem target-base64: " +
		Convert.ToBase64String(Encoding.UTF8.GetBytes(targetPath));

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

	private static void SetUnixExecutableMode(string path)
	{
		if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
			return;

		File.SetUnixFileMode(
			path,
			UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
			UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
			UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
	}
}
