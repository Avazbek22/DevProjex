using DevProjex.Avalonia.Services;
using DevProjex.Infrastructure.TerminalCommands;

namespace DevProjex.Tests.Unit;

public sealed class FlatpakHostPathSetupTests
{
	private const string AppId = "io.github.Avazbek22.DevProjex";
	private const string SandboxPath = "/app/bin:/usr/bin";
	private const string PathSetupLine =
		"case \":$PATH:\" in *\":$HOME/.local/bin:\"*) ;; *) export PATH=\"$HOME/.local/bin:$PATH\" ;; esac";

	[Fact]
	public void ConfiguredHostProfilesSurviveANewSandboxLaunch()
	{
		using var sandbox = FlatpakEnvironment.UseForTesting(new(true, AppId));
		using var directory = new TemporaryDirectory();
		var executable = directory.CreateFile("DevProjex", "placeholder");

		var firstLaunch = CreateService(directory.Path, executable, () => SandboxPath);
		Assert.True(firstLaunch.InstallOrRepair().Success);
		Assert.Equal(TerminalCommandSetupState.InstalledPathMissing, firstLaunch.Probe().State);
		Assert.True(firstLaunch.ConfigurePath().Success);

		// A later `flatpak run` starts with the sandbox PATH again; only the host profiles remember the setup.
		var laterLaunch = CreateService(directory.Path, executable, () => SandboxPath);
		var snapshot = laterLaunch.Probe();

		Assert.Equal(TerminalCommandSetupState.Installed, snapshot.State);
		Assert.Null(snapshot.PathSetupCommand);
	}

	[Theory]
	[InlineData(".bashrc")]
	[InlineData(".profile")]
	public void OneUnconfiguredBashProfileKeepsThePathPrompt(string configuredProfile)
	{
		using var sandbox = FlatpakEnvironment.UseForTesting(new(true, AppId));
		using var directory = new TemporaryDirectory();
		var executable = directory.CreateFile("DevProjex", "placeholder");
		var service = CreateService(directory.Path, executable, () => SandboxPath);
		Assert.True(service.InstallOrRepair().Success);
		File.WriteAllText(Path.Combine(directory.Path, configuredProfile), PathSetupLine + "\n");

		Assert.Equal(TerminalCommandSetupState.InstalledPathMissing, service.Probe().State);
	}

	[Fact]
	public void ACommentedOrAlteredSetupLineIsNotEvidence()
	{
		using var sandbox = FlatpakEnvironment.UseForTesting(new(true, AppId));
		using var directory = new TemporaryDirectory();
		var executable = directory.CreateFile("DevProjex", "placeholder");
		var service = CreateService(directory.Path, executable, () => SandboxPath);
		Assert.True(service.InstallOrRepair().Success);
		foreach (var profile in new[] { ".bashrc", ".profile" })
			File.WriteAllText(Path.Combine(directory.Path, profile), "# " + PathSetupLine + "\nexport PATH=\"$HOME/bin:$PATH\"\n");

		Assert.Equal(TerminalCommandSetupState.InstalledPathMissing, service.Probe().State);
	}

	[Fact]
	public void OrdinaryLinuxStillTrustsOnlyTheProcessPath()
	{
		using var directory = new TemporaryDirectory();
		var executable = directory.CreateFile("DevProjex", "placeholder");
		var service = CreateService(directory.Path, executable, () => SandboxPath);
		Assert.True(service.InstallOrRepair().Success);
		foreach (var profile in new[] { ".bashrc", ".profile" })
			File.WriteAllText(Path.Combine(directory.Path, profile), PathSetupLine + "\n");

		Assert.Equal(TerminalCommandSetupState.InstalledPathMissing, service.Probe().State);
	}

	[Fact]
	public void SandboxProcessPathContainingTheDirectoryStillResolvesTheRealCommand()
	{
		using var sandbox = FlatpakEnvironment.UseForTesting(new(true, AppId));
		using var directory = new TemporaryDirectory();
		var executable = directory.CreateFile("DevProjex", "placeholder");
		var bin = Path.Combine(directory.Path, ".local", "bin");
		var service = CreateService(directory.Path, executable, () => bin);
		Assert.True(service.InstallOrRepair().Success);

		var snapshot = service.Probe();

		Assert.Equal(TerminalCommandSetupState.Installed, snapshot.State);
		Assert.Equal(Path.Combine(bin, "devprojex"), snapshot.ResolvedCommandPath);
	}

	[Fact]
	public void McpConnectionNeverAsksForThePathCommandInsideFlatpak()
	{
		var platforms = new[] { TerminalCommandHostPlatform.Linux, TerminalCommandHostPlatform.MacOS, TerminalCommandHostPlatform.Windows };
		var promptedOutside = platforms.SelectMany(platform => Enum.GetValues<TerminalCommandSetupState>()
				.Where(state => McpConnectionPathPromptPolicy.ShouldShow(state, platform)))
			.ToArray();

		using var sandbox = FlatpakEnvironment.UseForTesting(new(true, AppId));

		Assert.NotEmpty(promptedOutside);
		foreach (var platform in platforms)
		foreach (var state in Enum.GetValues<TerminalCommandSetupState>())
			Assert.False(McpConnectionPathPromptPolicy.ShouldShow(state, platform), $"{platform}/{state}");
	}

	private static TerminalCommandSetupService CreateService(string home, string executable, Func<string?> path) =>
		new(new TerminalCommandSetupServiceOptions
		{
			Platform = TerminalCommandHostPlatform.Linux,
			HomeDirectoryProvider = () => home,
			LocalAppDataPathProvider = () => Path.Combine(home, ".local", "share"),
			IsWindowsPackagedApp = () => false,
			ExecutablePathProvider = () => executable,
			PathVariableProvider = path,
			ProcessPathVariableWriter = _ => { },
			PathListSeparator = Path.PathSeparator,
			UserPathVariableProvider = () => string.Empty,
			MachinePathVariableProvider = () => string.Empty,
			PathExtensionsProvider = () => ".COM;.EXE;.BAT;.CMD",
			ShellPathProvider = () => "/bin/bash",
			XdgConfigHomeProvider = () => Path.Combine(home, ".config"),
			ZdotDirectoryProvider = () => home
		});
}
