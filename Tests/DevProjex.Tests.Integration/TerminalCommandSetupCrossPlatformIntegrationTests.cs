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
		Assert.Contains("rem target: " + secondTarget, launcher, StringComparison.Ordinal);
		Assert.DoesNotContain("rem target: " + firstTarget, launcher, StringComparison.Ordinal);
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
