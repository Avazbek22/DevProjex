using DevProjex.Infrastructure.TerminalCommands;

namespace DevProjex.Tests.Unit;

public sealed class FlatpakShimFileSystemTests
{
	[Theory]
	[InlineData(TerminalCommandHostPlatform.Linux, "bash")]
	[InlineData(TerminalCommandHostPlatform.Linux, "zsh")]
	[InlineData(TerminalCommandHostPlatform.Linux, "fish")]
	[InlineData(TerminalCommandHostPlatform.MacOS, "bash")]
	[InlineData(TerminalCommandHostPlatform.MacOS, "zsh")]
	[InlineData(TerminalCommandHostPlatform.MacOS, "fish")]
	public void InstallRepairAndProfileSetupAreIdempotentOnAnIsolatedFilesystem(TerminalCommandHostPlatform platform, string shell)
	{
		using var directory = new TemporaryDirectory();
		var executable = directory.CreateFile("DevProjex", "placeholder");
		var bin = Path.Combine(directory.Path, ".local", "bin");
		var path = string.Empty;
		var service = Create(directory.Path, executable, platform, shell, () => path, value => path = value);
		Assert.False(Directory.Exists(bin));
		using var ordinary = FlatpakEnvironment.UseForTesting(new(false, null));
		Assert.True(service.InstallOrRepair().Success);
		var launcher = Path.Combine(bin, "devprojex");
		var original = File.ReadAllBytes(launcher);
		using (FlatpakEnvironment.UseForTesting(new(true, "test.app")))
		{
			Assert.Equal(TerminalCommandSetupState.Stale, service.Probe().State);
			Assert.True(service.InstallOrRepair().Success);
			var expected = Encoding.UTF8.GetBytes(TerminalCommandSetupService.BuildWrapperContent(executable));
			Assert.Equal(expected, File.ReadAllBytes(launcher));
			Assert.True(service.InstallOrRepair().Success);
			Assert.Equal(expected, File.ReadAllBytes(launcher));
			Assert.True(service.ConfigurePath().Success);
			var profiles = Directory.GetFiles(directory.Path, "*", SearchOption.AllDirectories)
				.Where(file => Path.GetFileName(file) is ".profile" or ".bashrc" or ".bash_profile" or ".zshrc" or "config.fish")
				.ToDictionary(file => file, File.ReadAllBytes);
			Assert.NotEmpty(profiles);
			Assert.True(service.ConfigurePath().Success);
			foreach (var profile in profiles) Assert.Equal(profile.Value, File.ReadAllBytes(profile.Key));
		}
		Assert.Equal(TerminalCommandSetupState.Stale, service.Probe().State);
		Assert.True(service.InstallOrRepair().Success);
		Assert.Equal(original, File.ReadAllBytes(launcher));
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void OldManagedFormatsIncludingCrLfUpgradeToTheExactFlatpakBytes(bool shebang)
	{
		using var directory = new TemporaryDirectory();
		var executable = directory.CreateFile("DevProjex", "placeholder");
		var bin = directory.CreateFolder(".local/bin");
		var launcher = Path.Combine(bin, "devprojex");
		File.WriteAllText(launcher, (shebang ? "#!/bin/sh\r\n" : "") + "# DevProjex terminal command wrapper\r\n# target: " + executable + "\r\nexec old \"$@\"\r\n");
		using var sandbox = FlatpakEnvironment.UseForTesting(new(true, "test.app"));
		var service = Create(directory.Path, executable, TerminalCommandHostPlatform.Linux, "bash", () => bin, _ => { });
		Assert.Equal(TerminalCommandSetupState.Stale, service.Probe().State);
		Assert.True(service.InstallOrRepair().Success);
		Assert.Equal(Encoding.UTF8.GetBytes(TerminalCommandSetupService.BuildWrapperContent(executable)), File.ReadAllBytes(launcher));
	}

	[Theory]
	[InlineData("file")]
	[InlineData("directory")]
	[InlineData("symlink")]
	public async Task ForeignEntriesAndTheirTargetsAreNeverOverwritten(string kind)
	{
		using var directory = new TemporaryDirectory();
		var executable = directory.CreateFile("DevProjex", "placeholder");
		var bin = directory.CreateFolder(".local/bin");
		var launcher = Path.Combine(bin, "devprojex");
		var target = directory.CreateFile("foreign", "foreign bytes\r\n");
		if (kind == "symlink" && OperatingSystem.IsWindows())
		{
			var targetDirectory = directory.CreateFolder("foreign-directory");
			var start = new System.Diagnostics.ProcessStartInfo("cmd.exe")
			{
				Arguments = $"/d /c mklink /J \"{launcher}\" \"{targetDirectory}\"", UseShellExecute = false,
				CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
			};
			using var process = System.Diagnostics.Process.Start(start)!;
			await process.WaitForExitAsync(TestContext.Current.CancellationToken);
			Assert.Equal(0, process.ExitCode);
		}
		else if (kind == "symlink") File.CreateSymbolicLink(launcher, target);
		else if (kind == "directory") Directory.CreateDirectory(launcher);
		else File.WriteAllBytes(launcher, File.ReadAllBytes(target));
		using var sandbox = FlatpakEnvironment.UseForTesting(new(true, "test.app"));
		var service = Create(directory.Path, executable, TerminalCommandHostPlatform.Linux, "bash", () => bin, _ => { });
		Assert.False(service.InstallOrRepair().Success);
		Assert.Equal("foreign bytes\r\n", File.ReadAllText(target));
		if (kind == "directory" || kind == "symlink" && OperatingSystem.IsWindows()) Assert.True(Directory.Exists(launcher));
		else Assert.Equal("foreign bytes\r\n", File.ReadAllText(launcher));
		if (kind == "symlink" && !OperatingSystem.IsWindows()) Assert.Equal(target, new FileInfo(launcher).LinkTarget);
		if (kind == "symlink" && OperatingSystem.IsWindows()) Directory.Delete(launcher);
	}

	[Fact]
	public void PermissionFailurePreservesTheInstalledBytes()
	{
		using var directory = new TemporaryDirectory();
		var executable = directory.CreateFile("DevProjex", "placeholder");
		var bin = Path.Combine(directory.Path, ".local", "bin");
		var service = Create(directory.Path, executable, TerminalCommandHostPlatform.Linux, "bash", () => bin, _ => { });
		using var ordinary = FlatpakEnvironment.UseForTesting(new(false, null));
		Assert.True(service.InstallOrRepair().Success);
		var launcher = Path.Combine(bin, "devprojex");
		var bytes = File.ReadAllBytes(launcher);
		try
		{
			if (OperatingSystem.IsWindows()) File.SetAttributes(launcher, FileAttributes.ReadOnly);
			else File.SetUnixFileMode(bin, UnixFileMode.UserRead | UnixFileMode.UserExecute);
			using var sandbox = FlatpakEnvironment.UseForTesting(new(true, "test.app"));
			Assert.False(service.InstallOrRepair().Success);
			Assert.Equal(bytes, File.ReadAllBytes(launcher));
		}
		finally
		{
			if (OperatingSystem.IsWindows()) File.SetAttributes(launcher, FileAttributes.Normal);
			else File.SetUnixFileMode(bin, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
		}
	}

	private static TerminalCommandSetupService Create(string home, string executable, TerminalCommandHostPlatform platform,
		string shell, Func<string> path, Action<string> writePath) => new(new TerminalCommandSetupServiceOptions
		{
			Platform = platform, HomeDirectoryProvider = () => home, LocalAppDataPathProvider = () => Path.Combine(home, "data"),
			ExecutablePathProvider = () => executable, IsWindowsPackagedApp = () => false,
			PathVariableProvider = path, ProcessPathVariableWriter = writePath, UserPathVariableProvider = () => "",
			MachinePathVariableProvider = () => "", UserPathVariableWriter = _ => throw new InvalidOperationException(),
			PathExtensionsProvider = () => ".EXE;.CMD", PathListSeparator = Path.PathSeparator,
			ShellPathProvider = () => "/bin/" + shell, XdgConfigHomeProvider = () => Path.Combine(home, ".config"), ZdotDirectoryProvider = () => home
		});
}
