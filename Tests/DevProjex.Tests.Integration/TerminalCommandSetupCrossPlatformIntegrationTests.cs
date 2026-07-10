using DevProjex.Infrastructure.TerminalCommands;

namespace DevProjex.Tests.Integration;

[Trait("Category", "TerminalCommand")]
public sealed class TerminalCommandSetupCrossPlatformIntegrationTests
{
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
		Assert.Equal(TerminalCommandSetupState.Installed, result.Snapshot.State);
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
}
