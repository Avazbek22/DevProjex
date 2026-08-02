using System.Runtime.InteropServices;
using DevProjex.Infrastructure.ProjectProfiles;
using DevProjex.Terminal.DesktopControl;

namespace DevProjex.Tests.Terminal;

public sealed class DoctorReleaseRegressionTests
{
	[Fact]
	public async Task DoctorJsonUsesCanonicalLowercaseArchitectureToken()
	{
		using var workspace = new TemporaryDirectory();
		var environment = new TestTerminalEnvironment();
		var services = new TerminalServiceFactory(
				() => workspace.CreateDirectory("app-data"))
			.Create(AppLanguage.En);
		var registry = new DesktopInstanceRegistry(
			new DesktopControlPaths(() => workspace.CreateDirectory("ipc")));

		var exitCode = await new DoctorCommandHandler(
				services,
				environment,
				registry,
				() => workspace.Path)
			.ExecuteAsync(json: true, TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		using var document = JsonDocument.Parse(environment.StandardOutput);
		Assert.Equal(
			RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
			document.RootElement.GetProperty("architecture").GetString());
		Assert.Empty(environment.StandardError);
	}

	[Fact]
	public async Task DoctorJsonChecksUseStableLowercaseStatusSeverityAndCodes()
	{
		using var workspace = new TemporaryDirectory();
		var environment = new TestTerminalEnvironment();
		var services = new TerminalServiceFactory(
				() => workspace.CreateDirectory("app-data"))
			.Create(AppLanguage.En);
		var registry = new DesktopInstanceRegistry(
			new DesktopControlPaths(() => workspace.CreateDirectory("ipc")));

		var exitCode = await new DoctorCommandHandler(
				services,
				environment,
				registry,
				() => workspace.Path)
			.ExecuteAsync(json: true, TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		using var document = JsonDocument.Parse(environment.StandardOutput);
		foreach (var check in document.RootElement.GetProperty("checks").EnumerateArray())
		{
			var status = check.GetProperty("status").GetString();
			Assert.Contains(
				status,
				new[] { "pass", "warning", "failure", "skip" },
				StringComparer.Ordinal);
			var severity = check.GetProperty("severity").GetString();
			Assert.Equal(severity?.ToLowerInvariant(), severity);
			Assert.False(string.IsNullOrWhiteSpace(check.GetProperty("code").GetString()));
		}
		Assert.Empty(environment.StandardError);
	}

	[Fact]
	public async Task DoctorCacheCheckReportsConfiguredRepositoryCachePath()
	{
		using var workspace = new TemporaryDirectory();
		var environment = new TestTerminalEnvironment();
		var services = new TerminalServiceFactory(
				() => workspace.CreateDirectory("app-data"))
			.Create(AppLanguage.En);
		var registry = new DesktopInstanceRegistry(
			new DesktopControlPaths(() => workspace.CreateDirectory("ipc")));

		var exitCode = await new DoctorCommandHandler(
				services,
				environment,
				registry,
				() => workspace.Path)
			.ExecuteAsync(json: true, TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		using var document = JsonDocument.Parse(environment.StandardOutput);
		var cacheCheck = Assert.Single(
			document.RootElement.GetProperty("checks").EnumerateArray(),
			static check => check.GetProperty("name").GetString() == "repository-cache");
		Assert.Equal(
			Path.GetFullPath(services.RepoCacheService.CacheRootPath),
			Path.GetFullPath(cacheCheck.GetProperty("path").GetString()!));
		Assert.Empty(environment.StandardError);
	}

	[Fact]
	public async Task DoctorReportsConfiguredStorageRootsAndLeavesNoProbeFiles()
	{
		using var workspace = new TemporaryDirectory();
		var environment = new TestTerminalEnvironment();
		var appData = workspace.CreateDirectory("app-data");
		var services = new TerminalServiceFactory(() => appData)
			.Create(AppLanguage.En);
		var runtimeRoot = Path.Combine(workspace.Path, "xdg-runtime");
		var registry = new DesktopInstanceRegistry(
			new DesktopControlPaths(() => runtimeRoot));
		var roots = new DoctorStorageRoots(
			Path.Combine(workspace.Path, "xdg-config"),
			Path.Combine(workspace.Path, "xdg-data"),
			Path.Combine(workspace.Path, "xdg-state"),
			Path.Combine(workspace.Path, "xdg-cache"));

		var exitCode = await new DoctorCommandHandler(
				services,
				environment,
				registry,
				() => workspace.Path,
				() => roots)
			.ExecuteAsync(json: true, TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		using var document = JsonDocument.Parse(environment.StandardOutput);
		var checks = document.RootElement.GetProperty("checks")
			.EnumerateArray()
			.ToDictionary(
				static check => check.GetProperty("name").GetString()!,
				static check => check,
				StringComparer.Ordinal);
		AssertPath(checks, "configuration-root", roots.Configuration);
		AssertPath(checks, "data-root", roots.Data);
		AssertPath(checks, "state-root", roots.State);
		AssertPath(checks, "cache-root", roots.Cache);
		AssertPath(
			checks,
			"terminal-settings",
			services.TerminalSettingsStore.GetPath());
		AssertPath(
			checks,
			"profile-store",
			Assert.IsType<ProjectProfileStore>(services.LocalProfileStore).GetPath());
		AssertPath(
			checks,
			"recent-workspaces",
			services.RecentProjectsStore.GetPath());
		AssertPath(
			checks,
			"repository-cache",
			services.RepoCacheService.CacheRootPath);
		AssertPath(checks, "desktop-ipc", registry.RegistryDirectory);
		Assert.Equal(
			"skip",
			checks["desktop-ipc"].GetProperty("status").GetString());
		Assert.False(Directory.Exists(roots.Configuration));
		Assert.False(Directory.Exists(roots.Data));
		Assert.False(Directory.Exists(roots.State));
		Assert.False(Directory.Exists(roots.Cache));
		Assert.False(Directory.Exists(runtimeRoot));
		Assert.Empty(Directory.EnumerateFiles(
			workspace.Path,
			".devprojex-doctor-*.tmp",
			SearchOption.AllDirectories));
		Assert.Empty(environment.StandardError);
	}

	[Fact]
	public async Task DoctorWarnsWhenDesktopIpcDestinationCannotBeCreated()
	{
		using var workspace = new TemporaryDirectory();
		var environment = new TestTerminalEnvironment();
		var services = new TerminalServiceFactory(
				() => workspace.CreateDirectory("app-data"))
			.Create(AppLanguage.En);
		var blockingFile = Path.Combine(workspace.Path, "runtime-blocked");
		await File.WriteAllTextAsync(
			blockingFile,
			"not a directory",
			TestContext.Current.CancellationToken);
		var registry = new DesktopInstanceRegistry(
			new DesktopControlPaths(() => blockingFile));

		var exitCode = await new DoctorCommandHandler(
				services,
				environment,
				registry,
				() => workspace.Path)
			.ExecuteAsync(json: true, TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.PolicyFailure, exitCode);
		using var document = JsonDocument.Parse(environment.StandardOutput);
		var check = Assert.Single(
			document.RootElement.GetProperty("checks").EnumerateArray(),
			static candidate => candidate.GetProperty("name").GetString() == "desktop-ipc");
		Assert.Equal("failure", check.GetProperty("status").GetString());
		Assert.False(string.IsNullOrWhiteSpace(check.GetProperty("hint").GetString()));
		Assert.False(Directory.Exists(registry.RegistryDirectory));
		Assert.Equal("not a directory", await File.ReadAllTextAsync(
			blockingFile,
			TestContext.Current.CancellationToken));
		Assert.Empty(environment.StandardError);
	}

	[Fact]
	public async Task ExistingTerminalSettingsFileIsCheckedAsWritableFileDestination()
	{
		using var workspace = new TemporaryDirectory();
		var environment = new TestTerminalEnvironment();
		var appData = workspace.CreateDirectory("app-data");
		var services = new TerminalServiceFactory(() => appData)
			.Create(AppLanguage.En);
		await services.TerminalSettingsStore.SaveScreenModeAsync(
			TerminalScreenMode.Inline,
			TestContext.Current.CancellationToken);
		var settingsPath = services.TerminalSettingsStore.GetPath();
		var originalSettings = await File.ReadAllBytesAsync(
			settingsPath,
			TestContext.Current.CancellationToken);
		var registry = new DesktopInstanceRegistry(
			new DesktopControlPaths(() => Path.Combine(workspace.Path, "ipc")));

		var exitCode = await new DoctorCommandHandler(
				services,
				environment,
				registry,
				() => workspace.Path)
			.ExecuteAsync(json: true, TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		using var document = JsonDocument.Parse(environment.StandardOutput);
		var settingsCheck = Assert.Single(
			document.RootElement.GetProperty("checks").EnumerateArray(),
			static check => check.GetProperty("name").GetString() == "terminal-settings");
		Assert.Equal("pass", settingsCheck.GetProperty("status").GetString());
		Assert.Equal(
			originalSettings,
			await File.ReadAllBytesAsync(
				settingsPath,
				TestContext.Current.CancellationToken));
		Assert.Empty(Directory.EnumerateFiles(
			Path.GetDirectoryName(settingsPath)!,
			".devprojex-doctor-*.tmp",
			SearchOption.TopDirectoryOnly));
		Assert.Empty(environment.StandardError);
	}

	private static void AssertPath(
		IReadOnlyDictionary<string, JsonElement> checks,
		string name,
		string expected)
	{
		var actual = checks[name].GetProperty("path").GetString()!;
		Assert.Equal(
			Path.GetFullPath(expected).Replace('\\', '/'),
			actual,
			StringComparer.Ordinal);
	}
}
