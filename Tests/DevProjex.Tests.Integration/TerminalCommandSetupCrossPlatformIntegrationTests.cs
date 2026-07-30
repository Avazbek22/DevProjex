using DevProjex.Infrastructure.TerminalCommands;

namespace DevProjex.Tests.Integration;

[Trait("Category", "TerminalCommand")]
public sealed class TerminalCommandSetupCrossPlatformIntegrationTests
{
	[Theory]
	[InlineData("bash")]
	[InlineData("zsh")]
	[InlineData("fish")]
	public async Task ConfigurePath_InstalledUnixShellLoadsProfileAndResolvesManagedLauncher(string shellName)
	{
		if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
			return;

		var shellPath = FindExecutableInPath(shellName);
		if (shellPath is null)
		{
			Assert.NotEqual(
				"1",
				Environment.GetEnvironmentVariable("DEVPROJEX_REQUIRE_TERMINAL_SHELL_MATRIX"));
			return;
		}

		using var temp = new TemporaryDirectory();
		var home = temp.CreateDirectory("home");
		var target = temp.CreateFile("app/DevProjex", "fake executable");
		var xdgConfigHome = temp.CreateDirectory("xdg-config");
		var zdotDirectory = temp.CreateDirectory("zdot");
		var processPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
		var service = new TerminalCommandSetupService(new TerminalCommandSetupServiceOptions
		{
			Platform = OperatingSystem.IsMacOS() ? TerminalCommandHostPlatform.MacOS : TerminalCommandHostPlatform.Linux,
			HomeDirectoryProvider = () => home,
			PathVariableProvider = () => processPath,
			ProcessPathVariableWriter = value => processPath = value,
			ShellPathProvider = () => shellPath,
			XdgConfigHomeProvider = () => xdgConfigHome,
			ZdotDirectoryProvider = () => zdotDirectory,
			ExecutablePathProvider = () => target
		});

		var install = service.InstallOrRepair();
		var configure = service.ConfigurePath();
		var userBin = Path.Combine(home, ".local", "bin");
		var result = await RunShellResolutionAsync(
			shellPath,
			shellName,
			home,
			xdgConfigHome,
			zdotDirectory,
			Environment.GetEnvironmentVariable("PATH") ?? string.Empty);

		Assert.True(install.Success, install.ErrorMessage);
		Assert.True(configure.Success, configure.ErrorMessage);
		Assert.Equal(0, result.ExitCode);
		Assert.Equal(Path.Combine(userBin, CommandLineExecutableAliases.UnixCommand), result.Stdout.Trim());
		Assert.Equal(string.Empty, result.Stderr.Trim());
		if (shellName == "bash")
		{
			var loginResult = await RunShellResolutionAsync(
				shellPath,
				shellName,
				home,
				xdgConfigHome,
				zdotDirectory,
				Environment.GetEnvironmentVariable("PATH") ?? string.Empty,
				useBashLoginProfile: true);
			Assert.Equal(0, loginResult.ExitCode);
			Assert.Equal(Path.Combine(userBin, CommandLineExecutableAliases.UnixCommand), loginResult.Stdout.Trim());
			Assert.Equal(string.Empty, loginResult.Stderr.Trim());
		}
	}

	[Fact]
	public void Probe_DefaultRuntimeService_DoesNotThrowAndReturnsKnownState()
	{
		var service = new TerminalCommandSetupService();

		var snapshot = service.Probe();

		Assert.False(string.IsNullOrWhiteSpace(snapshot.CommandName));
		Assert.True(Enum.IsDefined(snapshot.State));
	}

	[Fact]
	public void InstallOrRepair_UnixRuntime_CreatesWrapperWithoutTouchingRealHome()
	{
		if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
			return;

		using var temp = new TemporaryDirectory();
		var target = temp.CreateFile("DevProjex", "fake executable");
		var userBin = Path.Combine(temp.Path, ".local", "bin");
		var service = new TerminalCommandSetupService(new TerminalCommandSetupServiceOptions
		{
			Platform = OperatingSystem.IsMacOS()
				? TerminalCommandHostPlatform.MacOS
				: TerminalCommandHostPlatform.Linux,
			HomeDirectoryProvider = () => temp.Path,
			PathVariableProvider = () => userBin,
			ExecutablePathProvider = () => target
		});

		var result = service.InstallOrRepair();
		var commandPath = Path.Combine(userBin, CommandLineExecutableAliases.UnixCommand);

		Assert.True(result.Success);
		Assert.True(File.Exists(commandPath));
		Assert.Contains(target, File.ReadAllText(commandPath), StringComparison.Ordinal);
#pragma warning disable CA1416
		var mode = File.GetUnixFileMode(commandPath);
#pragma warning restore CA1416
		Assert.True((mode & UnixFileMode.UserExecute) == UnixFileMode.UserExecute);
	}

	[Fact]
	public void InstallOrRepair_UnixRuntime_PathProviderFailureStillCreatesExecutableWrapperInSandbox()
	{
		if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
			return;

		using var temp = new TemporaryDirectory();
		var target = temp.CreateFile("DevProjex", "fake executable");
		var userBin = Path.Combine(temp.Path, ".local", "bin");
		var service = new TerminalCommandSetupService(new TerminalCommandSetupServiceOptions
		{
			Platform = OperatingSystem.IsMacOS()
				? TerminalCommandHostPlatform.MacOS
				: TerminalCommandHostPlatform.Linux,
			HomeDirectoryProvider = () => temp.Path,
			PathVariableProvider = () => throw new IOException("PATH unavailable in test runner"),
			ExecutablePathProvider = () => target
		});

		var result = service.InstallOrRepair();
		var commandPath = Path.Combine(userBin, CommandLineExecutableAliases.UnixCommand);

		Assert.True(result.Success);
		Assert.Equal(TerminalCommandSetupState.InstalledPathMissing, result.Snapshot.State);
		Assert.False(result.Snapshot.UserBinDirectoryIsInPath);
		Assert.Contains(".local/bin", result.Snapshot.ShellProfileHint, StringComparison.Ordinal);
		Assert.True(File.Exists(commandPath));
		Assert.Contains(target, File.ReadAllText(commandPath), StringComparison.Ordinal);
#pragma warning disable CA1416
		var mode = File.GetUnixFileMode(commandPath);
#pragma warning restore CA1416
		Assert.True((mode & UnixFileMode.UserExecute) == UnixFileMode.UserExecute);
	}

	[Fact]
	public void InstallOrRepair_UnixPathDirectorySymlinkResolvesToManagedLauncherWithoutFalseShadowing()
	{
		if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
			return;

		using var temp = new TemporaryDirectory();
		var target = temp.CreateFile("app/DevProjex", "fake executable");
		var userBin = temp.CreateDirectory("home/.local/bin");
		var aliasBin = Path.Combine(temp.Path, "bin-alias");
		Directory.CreateSymbolicLink(aliasBin, userBin);
		var service = new TerminalCommandSetupService(new TerminalCommandSetupServiceOptions
		{
			Platform = OperatingSystem.IsMacOS() ? TerminalCommandHostPlatform.MacOS : TerminalCommandHostPlatform.Linux,
			HomeDirectoryProvider = () => Path.Combine(temp.Path, "home"),
			PathVariableProvider = () => aliasBin,
			ExecutablePathProvider = () => target
		});

		var result = service.InstallOrRepair();

		Assert.True(result.Success, result.ErrorMessage);
		Assert.Equal(TerminalCommandSetupState.Installed, result.Snapshot.State);
		Assert.True(result.Snapshot.UserBinDirectoryIsInPath);
		Assert.Equal(Path.Combine(aliasBin, CommandLineExecutableAliases.UnixCommand), result.Snapshot.ResolvedCommandPath);
	}

	[Fact]
	public void InstallOrRepair_MacOsCaseInsensitivePathUsesActualVolumeSemantics()
	{
		if (!OperatingSystem.IsMacOS())
			return;

		using var temp = new TemporaryDirectory();
		var home = temp.CreateDirectory("home");
		var target = temp.CreateFile("app/DevProjex", "fake executable");
		var userBin = temp.CreateDirectory("home/.local/bin");
		var caseVariant = Path.Combine(home, ".LOCAL", "BIN");
		if (!Directory.Exists(caseVariant))
			return;

		var service = new TerminalCommandSetupService(new TerminalCommandSetupServiceOptions
		{
			Platform = TerminalCommandHostPlatform.MacOS,
			HomeDirectoryProvider = () => home,
			PathVariableProvider = () => caseVariant,
			ExecutablePathProvider = () => target
		});

		var result = service.InstallOrRepair();

		Assert.True(result.Success, result.ErrorMessage);
		Assert.Equal(TerminalCommandSetupState.Installed, result.Snapshot.State);
		Assert.True(result.Snapshot.UserBinDirectoryIsInPath);
		Assert.Equal(Path.Combine(caseVariant, CommandLineExecutableAliases.UnixCommand), result.Snapshot.ResolvedCommandPath);
	}

	[Fact]
	public void UnixFirstRunJourney_CreatesLauncherConfiguresShellAndConvergesToInstalled()
	{
		using var temp = new TemporaryDirectory();
		var target = temp.CreateFile("app/DevProjex", "fake executable");
		var processPath = Path.Combine(temp.Path, "system-bin");
		var service = new TerminalCommandSetupService(new TerminalCommandSetupServiceOptions
		{
			Platform = TerminalCommandHostPlatform.Linux,
			HomeDirectoryProvider = () => temp.Path,
			PathVariableProvider = () => processPath,
			ProcessPathVariableWriter = value => processPath = value,
			ShellPathProvider = () => "/bin/bash",
			ExecutablePathProvider = () => target,
			// The journey simulates Unix behavior over real host paths on every CI operating system.
			PathListSeparator = Path.PathSeparator
		});

		var before = service.Probe();
		var install = service.InstallOrRepair();
		var configurePath = service.ConfigurePath();
		var after = service.Probe();
		var profilePath = Path.Combine(temp.Path, ".bashrc");
		var profileAfterFirstRun = File.ReadAllText(profilePath);
		var repeatedConfiguration = service.ConfigurePath();

		Assert.Equal(TerminalCommandSetupState.NotInstalled, before.State);
		Assert.True(install.Success, install.ErrorMessage);
		Assert.Equal(TerminalCommandSetupState.InstalledPathMissing, install.Snapshot.State);
		Assert.True(configurePath.Success, configurePath.ErrorMessage);
		Assert.Equal(TerminalCommandSetupState.Installed, configurePath.Snapshot.State);
		Assert.Equal(TerminalCommandSetupState.Installed, after.State);
		Assert.True(after.IsReady);
		Assert.Contains(Path.Combine(temp.Path, ".local", "bin"), processPath, StringComparison.Ordinal);
		Assert.Contains("# DevProjex terminal PATH", profileAfterFirstRun, StringComparison.Ordinal);
		Assert.Contains("case \":$PATH:\" in", profileAfterFirstRun, StringComparison.Ordinal);
		Assert.Contains("export PATH=\"$HOME/.local/bin:$PATH\"", profileAfterFirstRun, StringComparison.Ordinal);
		Assert.Contains("# DevProjex terminal PATH", File.ReadAllText(Path.Combine(temp.Path, ".profile")), StringComparison.Ordinal);
		Assert.True(repeatedConfiguration.Success, repeatedConfiguration.ErrorMessage);
		Assert.Equal(profileAfterFirstRun, File.ReadAllText(profilePath));
	}

	[Fact]
	public void InstallOrRepair_WindowsPortableSimulation_CreatesLauncherAndRepairsThroughPublicApi()
	{
		using var temp = new TemporaryDirectory();
		var firstTarget = temp.CreateFile("v1/DevProjex.exe", "first executable");
		var secondTarget = temp.CreateFile("v2/DevProjex.exe", "second executable");
		var userPath = string.Empty;

		var firstService = CreateWindowsPortableService(temp.Path, () => userPath, value => userPath = value, firstTarget);
		var firstResult = firstService.InstallOrRepair();
		var commandPath = Path.Combine(temp.Path, "DevProjex", "bin", CommandLineExecutableAliases.WindowsPortableCommandFileName);

		Assert.True(firstResult.Success);
		Assert.Equal(TerminalCommandInstallOutcome.Created, firstResult.Outcome);
		Assert.Equal(TerminalCommandSetupState.Installed, firstResult.Snapshot.State);
		Assert.True(File.Exists(commandPath));
		Assert.Contains(Path.GetDirectoryName(commandPath)!, userPath, StringComparison.OrdinalIgnoreCase);

		var secondService = CreateWindowsPortableService(temp.Path, () => userPath, value => userPath = value, secondTarget);
		var stale = secondService.Probe();
		var repair = secondService.InstallOrRepair();
		var launcher = File.ReadAllText(commandPath);

		Assert.Equal(TerminalCommandSetupState.Stale, stale.State);
		Assert.True(repair.Success);
		Assert.Equal(TerminalCommandInstallOutcome.Repaired, repair.Outcome);
		Assert.Contains(WindowsTargetMarker(secondTarget), launcher, StringComparison.Ordinal);
		Assert.DoesNotContain(WindowsTargetMarker(firstTarget), launcher, StringComparison.Ordinal);
	}

	[Fact]
	public void InstallOrRepair_ReleaseGateEnvironment_InstallsOfficialWindowsLauncherThroughPublicApi()
	{
		using var fallbackWorkspace = new TemporaryDirectory();
		var configuredTarget = Environment.GetEnvironmentVariable(
			"DEVPROJEX_RELEASE_WINDOWS_LAUNCHER_TARGET");
		var configuredLocalAppData = Environment.GetEnvironmentVariable(
			"DEVPROJEX_RELEASE_WINDOWS_LAUNCHER_LOCAL_APP_DATA");
		Assert.True(
			string.IsNullOrWhiteSpace(configuredTarget) ==
			string.IsNullOrWhiteSpace(configuredLocalAppData),
			"Release launcher target and local application data root must be provided together.");
		var targetPath = string.IsNullOrWhiteSpace(configuredTarget)
			? fallbackWorkspace.CreateFile("published/DevProjex.exe", "test executable")
			: Path.GetFullPath(configuredTarget!);
		var localAppData = string.IsNullOrWhiteSpace(configuredLocalAppData)
			? fallbackWorkspace.Path
			: Path.GetFullPath(configuredLocalAppData!);
		Assert.True(File.Exists(targetPath), $"Release launcher target does not exist: {targetPath}");
		var userPath = string.Empty;
		var service = CreateWindowsPortableService(
			localAppData,
			() => userPath,
			value => userPath = value,
			targetPath);

		var result = service.InstallOrRepair();
		var expectedLauncherPath = Path.Combine(
			localAppData,
			"DevProjex",
			"bin",
			CommandLineExecutableAliases.WindowsPortableCommandFileName);

		Assert.True(result.Success, result.ErrorMessage);
		Assert.Equal(TerminalCommandInstallOutcome.Created, result.Outcome);
		Assert.Equal(expectedLauncherPath, result.Snapshot.CommandPath);
		Assert.Equal(
			TerminalCommandSetupService.BuildWindowsLauncherContent(targetPath),
			File.ReadAllText(expectedLauncherPath));
	}

	[Fact]
	public async Task WindowsPortableLauncher_ForwardsArgumentsAndReturnsExactExitCode()
	{
		if (!OperatingSystem.IsWindows())
			Assert.Skip("The portable .cmd launcher requires the native Windows command processor.");

		using var workspace = new TemporaryDirectory();
		var targetDirectory = workspace.CreateDirectory("portable & ^ ! 100% (кириллица)");
		var targetPath = Path.Combine(targetDirectory, "DevProjex.exe");
		File.Copy(Path.Combine(Environment.SystemDirectory, "cscript.exe"), targetPath);
		var scriptPath = workspace.CreateFile(
			"script folder/сapture args.js",
			"""
			var shell = new ActiveXObject("WScript.Shell");
			var fileSystem = new ActiveXObject("Scripting.FileSystemObject");
			var output = fileSystem.CreateTextFile(shell.Environment("PROCESS")("DPX_CAPTURE"), true, true);
			for (var index = 0; index < WScript.Arguments.length; index++) {
			    output.WriteLine(encodeURIComponent(WScript.Arguments.Item(index)));
			}
			output.Close();
			WScript.Quit(37);
			""");
		var capturePath = Path.Combine(workspace.Path, "captured-arguments.txt");
		string[] expectedArguments =
		[
			"space value",
			string.Empty,
			"-leading",
			"--",
			"кириллица",
			"ampersand&value",
			"percent%value",
			"bang!value",
			"caret^value",
			"(parentheses)"
		];
		var userPath = string.Empty;
		var service = CreateWindowsPortableService(
			workspace.Path,
			() => userPath,
			value => userPath = value,
			targetPath);
		var install = service.InstallOrRepair();
		Assert.True(install.Success, install.ErrorMessage);
		var launcherPath = install.Snapshot.CommandPath;
		Assert.False(string.IsNullOrWhiteSpace(launcherPath));
		const string scriptPathVariable = "%DPX_SCRIPT_PATH%";
		const string unicodeArgumentVariable = "%DPX_UNICODE_ARGUMENT%";
		var command = string.Join(
			" ",
			new[] { QuoteForCmd(launcherPath!), "//nologo", "\"" + scriptPathVariable + "\"" }
				.Concat(expectedArguments.Select(argument =>
					string.Equals(argument, "кириллица", StringComparison.Ordinal)
						? "\"" + unicodeArgumentVariable + "\""
						: QuoteForCmd(argument))));
		var callerPath = workspace.CreateFile(
			"caller folder/run-launcher.cmd",
			string.Join(
				"\r\n",
				"@echo off",
				"setlocal DisableDelayedExpansion",
				"chcp 437 >nul",
				command,
				string.Empty));
		var startInfo = new ProcessStartInfo
		{
			FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true
		};
		startInfo.Environment["DPX_CAPTURE"] = capturePath;
		startInfo.Environment["DPX_SCRIPT_PATH"] = scriptPath;
		startInfo.Environment["DPX_UNICODE_ARGUMENT"] = "кириллица";
		startInfo.ArgumentList.Add("/d");
		startInfo.ArgumentList.Add("/c");
		startInfo.ArgumentList.Add(callerPath);

		using var process = Process.Start(startInfo) ??
		                    throw new InvalidOperationException("Could not start cmd.exe.");
		var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
		var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
		await process.WaitForExitAsync(TestContext.Current.CancellationToken);
		var stdoutText = await stdout;
		var stderrText = await stderr;
		if (!File.Exists(capturePath))
		{
			throw new Xunit.Sdk.XunitException(
				$"Launcher did not create the capture file. ExitCode={process.ExitCode}; " +
				$"StdOut={stdoutText}; StdErr={stderrText}; Command={command}");
		}
		var actualArguments = File.ReadAllLines(capturePath)
			.Select(Uri.UnescapeDataString)
			.ToArray();

		Assert.Equal(37, process.ExitCode);
		Assert.Equal(string.Empty, stdoutText);
		Assert.Equal(string.Empty, stderrText);
		Assert.Equal(expectedArguments, actualArguments);
	}

	[Fact]
	public async Task WindowsFrameworkDependentLauncher_ReturnsExactManagedApplicationExitCode()
	{
		if (!OperatingSystem.IsWindows())
			Assert.Skip("The framework-dependent .cmd launcher requires the native Windows command processor.");

		using var workspace = new TemporaryDirectory();
		var targetPath = Path.Combine(AppContext.BaseDirectory, "DevProjex.exe");
		var managedAssemblyPath = Path.ChangeExtension(targetPath, ".dll");
		Assert.True(File.Exists(targetPath), $"Application host does not exist: {targetPath}");
		Assert.True(File.Exists(managedAssemblyPath), $"Managed application does not exist: {managedAssemblyPath}");
		var userPath = string.Empty;
		var service = CreateWindowsPortableService(
			workspace.Path,
			() => userPath,
			value => userPath = value,
			targetPath);
		var install = service.InstallOrRepair();
		Assert.True(install.Success, install.ErrorMessage);
		var launcherPath = Assert.IsType<string>(install.Snapshot.CommandPath);
		var codePageCapturePath = Path.Combine(workspace.Path, "caller-code-page.txt");
		var callerPath = workspace.CreateFile(
			"caller/run-framework-launcher.cmd",
			string.Join(
				"\r\n",
				"@echo off",
				"setlocal DisableDelayedExpansion",
				"chcp 437 >nul",
				"call " + QuoteForCmd(launcherPath) + " --definitely-unknown --language en",
				"set \"DPX_LAUNCHER_EXIT_CODE=%ERRORLEVEL%\"",
				"chcp > \"%DPX_CODE_PAGE_CAPTURE%\"",
				"exit /b %DPX_LAUNCHER_EXIT_CODE%",
				string.Empty));
		var startInfo = new ProcessStartInfo
		{
			FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true,
			WorkingDirectory = workspace.Path
		};
		startInfo.Environment["DPX_CODE_PAGE_CAPTURE"] = codePageCapturePath;
		startInfo.ArgumentList.Add("/d");
		startInfo.ArgumentList.Add("/c");
		startInfo.ArgumentList.Add(callerPath);

		using var process = Process.Start(startInfo) ??
		                    throw new InvalidOperationException("Could not start cmd.exe.");
		var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
		var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
		await process.WaitForExitAsync(TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.UsageError, process.ExitCode);
		Assert.Empty(await stdout);
		Assert.Contains(
			"error[DPX-CLI-UNKNOWN-OPTION]",
			await stderr,
			StringComparison.Ordinal);
		Assert.EndsWith("437", File.ReadAllText(codePageCapturePath).Trim(), StringComparison.Ordinal);
	}

	private static TerminalCommandSetupService CreateWindowsPortableService(
		string localAppData,
		Func<string?> userPathProvider,
		Action<string> userPathWriter,
		string executablePath)
	{
		return new TerminalCommandSetupService(new TerminalCommandSetupServiceOptions
		{
			Platform = TerminalCommandHostPlatform.Windows,
			IsWindowsPackagedApp = () => false,
			HomeDirectoryProvider = () => localAppData,
			LocalAppDataPathProvider = () => localAppData,
			PathVariableProvider = () => string.Empty,
			UserPathVariableProvider = userPathProvider,
			MachinePathVariableProvider = () => string.Empty,
			UserPathVariableWriter = userPathWriter,
			ExecutablePathProvider = () => executablePath,
			PathListSeparator = ';'
		});
	}

	private static string WindowsTargetMarker(string targetPath) =>
		"rem target-base64: " +
		Convert.ToBase64String(Encoding.UTF8.GetBytes(targetPath));

	private static string QuoteForCmd(string value) =>
		"\"" +
		value
			.Replace("%", "%%", StringComparison.Ordinal)
			.Replace("\"", "\"\"", StringComparison.Ordinal) +
		"\"";

	private static string? FindExecutableInPath(string executableName)
	{
		var path = Environment.GetEnvironmentVariable("PATH");
		if (string.IsNullOrWhiteSpace(path))
			return null;

		return path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.Select(directory => Path.Combine(directory, executableName))
			.FirstOrDefault(File.Exists);
	}

	private static async Task<ShellProcessResult> RunShellResolutionAsync(
		string shellPath,
		string shellName,
		string home,
		string xdgConfigHome,
		string zdotDirectory,
		string path,
		bool useBashLoginProfile = false)
	{
		var command = shellName switch
		{
			"bash" => useBashLoginProfile
				? ". \"$HOME/.profile\"; command -v devprojex"
				: ". \"$HOME/.bashrc\"; command -v devprojex",
			"zsh" => ". \"$ZDOTDIR/.zshrc\"; command -v devprojex",
			"fish" => "source \"$XDG_CONFIG_HOME/fish/config.fish\"; type -p devprojex",
			_ => throw new ArgumentOutOfRangeException(nameof(shellName))
		};
		var startInfo = new ProcessStartInfo
		{
			FileName = shellPath,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true
		};
		startInfo.Environment["HOME"] = home;
		startInfo.Environment["XDG_CONFIG_HOME"] = xdgConfigHome;
		startInfo.Environment["ZDOTDIR"] = zdotDirectory;
		startInfo.Environment["PATH"] = path;
		startInfo.ArgumentList.Add("-c");
		startInfo.ArgumentList.Add(command);

		using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start {shellName}.");
		var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
		var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
		await process.WaitForExitAsync(TestContext.Current.CancellationToken);
		return new ShellProcessResult(process.ExitCode, await stdout, await stderr);
	}

	private sealed record ShellProcessResult(int ExitCode, string Stdout, string Stderr);
}
