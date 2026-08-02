using System.Diagnostics;
using DevProjex.Terminal.DesktopControl;
using DevProjex.Terminal.Rendering;

namespace DevProjex.Tests.Terminal;

public sealed class MachineSchemaContractTests
{
	[Fact]
	public async Task AnalysisJsonNormalizesDiagnosticPathsAndUsesStableDiagnosticTokens()
	{
		using var workspace = new TemporaryDirectory();
		workspace.WriteFile("src/App.cs", "internal sealed class App {}");
		var services = new TerminalServiceFactory(
				() => workspace.CreateDirectory("data"))
			.Create(AppLanguage.En);
		var selection = await services.SelectionResolver.ResolveAsync(
			workspace.Path,
			ProjectProfileReference.Standard,
			new ProjectSelectionSpec(),
			TestContext.Current.CancellationToken);
		var plan = await services.ContextPlanner.BuildAsync(
			new ProjectContextRequest(workspace.Path, selection),
			TestContext.Current.CancellationToken);
		plan = plan with
		{
			Diagnostics =
			[
				new ContextDiagnostic(
					"DPX-TEST-PATH",
					ContextDiagnosticSeverity.Warning,
					"Stable diagnostic.",
					@"C:\workspace\src\App.cs")
			]
		};
		var environment = new TestTerminalEnvironment();

		await new MachineOutputRenderer(environment).WriteAnalysisJsonAsync(
			plan,
			environment.Output,
			TestContext.Current.CancellationToken);

		using var document = JsonDocument.Parse(environment.StandardOutput);
		var diagnostic = Assert.Single(
			document.RootElement.GetProperty("diagnostics").EnumerateArray());
		Assert.Equal("DPX-TEST-PATH", diagnostic.GetProperty("code").GetString());
		Assert.Equal("warning", diagnostic.GetProperty("severity").GetString());
		Assert.Equal(
			"C:/workspace/src/App.cs",
			diagnostic.GetProperty("path").GetString());
	}

	[Fact]
	public async Task UiListJsonUsesVersionedStableEnvelope()
	{
		using var workspace = new TemporaryDirectory();
		var environment = new TestTerminalEnvironment();
		var paths = new DesktopControlPaths(() => workspace.CreateDirectory("desktop-control"));
		var client = new DesktopControlClient(new DesktopInstanceRegistry(paths));
		var handler = new DesktopCommandHandler(environment, client);

		var exitCode = await handler.ListAsync(
			json: true,
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		using var document = JsonDocument.Parse(environment.StandardOutput);
		Assert.Equal(JsonValueKind.Number, document.RootElement
			.GetProperty("schemaVersion").ValueKind);
		Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
		Assert.Equal(
			"devprojex-ui-instances",
			document.RootElement.GetProperty("kind").GetString());
		Assert.Empty(document.RootElement.GetProperty("instances").EnumerateArray());
		Assert.Empty(environment.StandardError);
		Assert.DoesNotContain("\u001b", environment.StandardOutput, StringComparison.Ordinal);
	}

	[Fact]
	public async Task UiListJsonNormalizesRegisteredProjectPaths()
	{
		using var workspace = new TemporaryDirectory();
		var paths = new DesktopControlPaths(() => workspace.CreateDirectory("desktop-control"));
		var registry = new DesktopInstanceRegistry(paths);
		using var process = Process.GetCurrentProcess();
		await registry.RegisterAsync(
			new DesktopInstanceRegistration(
				DesktopProtocol.CurrentVersion,
				"schema-test",
				Environment.ProcessId,
				process.StartTime.ToUniversalTime().Ticks,
				@"C:\workspace\Project",
				DateTimeOffset.UtcNow,
				OperatingSystem.IsWindows() ? "pipe" : "unix",
				"schema-test-endpoint"),
			TestContext.Current.CancellationToken);
		var environment = new TestTerminalEnvironment();

		var exitCode = await new DesktopCommandHandler(
				environment,
				new DesktopControlClient(registry))
			.ListAsync(json: true, TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		using var document = JsonDocument.Parse(environment.StandardOutput);
		var instance = Assert.Single(
			document.RootElement.GetProperty("instances").EnumerateArray());
		Assert.Equal(
			"C:/workspace/Project",
			instance.GetProperty("projectPath").GetString());
	}
}
