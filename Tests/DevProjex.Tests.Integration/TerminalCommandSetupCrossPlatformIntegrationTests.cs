using DevProjex.Infrastructure.TerminalCommands;

namespace DevProjex.Tests.Integration;

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
}
