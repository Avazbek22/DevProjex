using System.Diagnostics;
using DevProjex.Terminal.DesktopControl;

namespace DevProjex.Tests.Terminal;

public sealed class DoctorCommandContractTests
{
	[Fact]
	public async Task JsonDoctorDocumentContainsStableOperationalChecks()
	{
		using var workspace = new TemporaryDirectory();
		workspace.WriteFile("app.cs", "class App {}");
		var environment = new TestTerminalEnvironment
		{
			IsCi = true,
			IsNoColor = true,
			Variables = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
			{
				["CI"] = "true",
				["NO_COLOR"] = "1",
				["TERM"] = "xterm-256color"
			}
		};
		var services = new TerminalServiceFactory(() => workspace.CreateDirectory("app-data"))
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
		Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
		Assert.Equal("devprojex-doctor", document.RootElement.GetProperty("kind").GetString());
		var names = document.RootElement.GetProperty("checks")
			.EnumerateArray()
			.Select(static check => check.GetProperty("name").GetString())
			.ToHashSet(StringComparer.Ordinal);
		Assert.Contains("terminal-launcher", names);
		Assert.Contains("terminal-capabilities", names);
		Assert.Contains("tracked-git-mode", names);
		Assert.Contains("profile-store", names);
		Assert.Contains("cache-directory", names);
		Assert.Contains("desktop-ipc", names);
		Assert.Contains("environment", names);
		Assert.DoesNotContain("\u001b", environment.StandardOutput, StringComparison.Ordinal);
		Assert.Empty(environment.StandardError);
	}

	[Fact]
	public async Task DoctorReportsButDoesNotDeleteStaleDesktopRegistration()
	{
		using var workspace = new TemporaryDirectory();
		var paths = new DesktopControlPaths(() => workspace.CreateDirectory("ipc"));
		var registry = new DesktopInstanceRegistry(paths);
		using var process = Process.GetCurrentProcess();
		var stale = new DesktopInstanceRegistration(
			DesktopProtocol.CurrentVersion,
			"stale-doctor",
			process.Id,
			process.StartTime.ToUniversalTime().Ticks - TimeSpan.FromHours(1).Ticks,
			workspace.Path,
			DateTimeOffset.UtcNow,
			OperatingSystem.IsWindows() ? "pipe" : "unix",
			OperatingSystem.IsWindows()
				? "devprojex-stale-doctor"
				: paths.GetSocketPath("stale-doctor"));
		await registry.RegisterAsync(stale, TestContext.Current.CancellationToken);
		var environment = new TestTerminalEnvironment();
		var services = new TerminalServiceFactory(() => workspace.CreateDirectory("app-data"))
			.Create(AppLanguage.En);

		var exitCode = await new DoctorCommandHandler(
				services,
				environment,
				registry,
				() => workspace.Path)
			.ExecuteAsync(json: true, TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		using var document = JsonDocument.Parse(environment.StandardOutput);
		var ipc = Assert.Single(
			document.RootElement.GetProperty("checks").EnumerateArray(),
			static check => check.GetProperty("name").GetString() == "desktop-ipc");
		Assert.Contains("stale=1", ipc.GetProperty("detail").GetString(), StringComparison.Ordinal);
		Assert.True(File.Exists(paths.GetRegistrationPath(stale.InstanceId)));
	}
}
