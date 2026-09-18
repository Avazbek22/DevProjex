using Avalonia.Automation;
using Avalonia.VisualTree;
using DevProjex.Application.Services;
using DevProjex.Infrastructure.ThemePresets;
using DevProjex.Kernel.Abstractions;
using System.Text.Json;

namespace DevProjex.Tests.UI;

[Collection(UiWorkspaceCollection.Name)]
public sealed class MainWindowMcpConnectionUiTests(UiWorkspaceFixture workspace)
{
	[AvaloniaFact]
	public async Task ConnectMenu_UsesPersistedLiveModeAndShowsSuccessToast()
	{
		var appDataPath = Path.Combine(
			workspace.Project.AppDataPath,
			"mcp-connect-ui",
			Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(appDataPath);
		var service = new RecordingMcpConnectionService(request => new McpConnectionResult(
			McpConnectionStatus.Connected,
			$"{request.Client} connected"));
		var terminalCommand = new StubTerminalCommandSetupService(
			CreateTerminalSnapshot(workspace.Project.RootPath, TerminalCommandSetupState.Installed));
		var firstWindow = await UiTestDriver.CreateLoadedMainWindowAsync(
			workspace.Project,
			appDataPathOverride: appDataPath,
			configureServices: services => services with
			{
				McpConnectionService = service,
				TerminalCommandSetupService = terminalCommand
			});

		try
		{
			var cursor = UiTestDriver.GetRequiredTopMenuControl<MenuItem>(
				firstWindow,
				"McpConnectCursorMenuItem");
			await UiTestDriver.RaiseMenuItemClickAsync(cursor);
			await UiTestDriver.WaitForConditionAsync(
				firstWindow,
				() => service.Requests.Count == 1 &&
					  UiTestDriver.GetToastService(firstWindow).Items.Any(
						  static toast => toast.Message == "Cursor connected"),
				"the successful MCP connection toast");

			var firstRequest = Assert.Single(service.Requests);
			Assert.Equal(McpConnectionClient.Cursor, firstRequest.Client);
			Assert.Equal(McpConnectionMode.Live, firstRequest.Mode);
			Assert.Equal(Path.GetFullPath(workspace.Project.RootPath), firstRequest.ProjectRoot);

			var live = UiTestDriver.GetRequiredTopMenuControl<MenuItem>(
				firstWindow,
				"McpLiveContextMenuItem");
			Assert.True(Assert.IsType<CheckBox>(live.Header).IsChecked);
			await UiTestDriver.RaiseMenuItemClickAsync(live);
			Assert.False(UiTestDriver.GetViewModel(firstWindow).IsMcpLiveContextEnabled);
			Assert.False(Assert.IsType<CheckBox>(live.Header).IsChecked);

			using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(
				appDataPath,
				"DevProjex",
				"user-settings.json")));
			Assert.False(document.RootElement
				.GetProperty("viewSettings")
				.GetProperty("isMcpLiveContextEnabled")
				.GetBoolean());
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(firstWindow, cleanupAppData: false);
		}

		var secondWindow = await UiTestDriver.CreateLoadedMainWindowAsync(
			workspace.Project,
			appDataPathOverride: appDataPath,
			configureServices: services => services with
			{
				McpConnectionService = service,
				TerminalCommandSetupService = terminalCommand
			});
		try
		{
			Assert.False(UiTestDriver.GetViewModel(secondWindow).IsMcpLiveContextEnabled);
			var codex = UiTestDriver.GetRequiredTopMenuControl<MenuItem>(
				secondWindow,
				"McpConnectCodexMenuItem");
			await UiTestDriver.RaiseMenuItemClickAsync(codex);
			await UiTestDriver.WaitForConditionAsync(
				secondWindow,
				() => service.Requests.Count == 2,
				"the second MCP connection request");
			Assert.Equal(McpConnectionMode.Standard, service.Requests[1].Mode);
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(secondWindow);
		}
	}

	[AvaloniaFact]
	public async Task FailedConnection_ShowsOnlyManualConfigurationDialogWithFallback()
	{
		const string configuration = "{\"mcpServers\":{\"devprojex\":{}}}";
		var service = new RecordingMcpConnectionService(_ => new McpConnectionResult(
			McpConnectionStatus.ClientNotFound,
			"Claude Code was not found.\nInstall it or use this configuration.",
			ManualConfiguration: configuration,
			SuggestedConfigPaths:
			[
				@"%APPDATA%\Claude\claude_desktop_config.json",
				"~/Library/Application Support/Claude/claude_desktop_config.json"
			]));
		var terminalCommand = new StubTerminalCommandSetupService(
			CreateTerminalSnapshot(workspace.Project.RootPath, TerminalCommandSetupState.NotInstalled));
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(
			workspace.Project,
			configureServices: services => services with
			{
				McpConnectionService = service,
				TerminalCommandSetupService = terminalCommand
			});

		try
		{
			var claude = UiTestDriver.GetRequiredTopMenuControl<MenuItem>(
				window,
				"McpConnectClaudeCodeMenuItem");
			await UiTestDriver.RaiseMenuItemClickAsync(claude);
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => window.OwnedWindows.Count == 1,
				"the MCP manual configuration dialog");

			var dialog = Assert.Single(window.OwnedWindows);
			Assert.Equal("Manual MCP configuration", dialog.Title);
			var configurationText = Assert.Single(
				dialog.GetVisualDescendants().OfType<TextBox>(),
				static control => control.Name == "McpManualConfigurationText");
			Assert.Equal(configuration, configurationText.Text);
			Assert.True(configurationText.IsReadOnly);
			Assert.Equal("Configuration", AutomationProperties.GetName(configurationText));
			var reason = Assert.Single(
				dialog.GetVisualDescendants().OfType<TextBlock>(),
				static control => control.Name == "McpManualConfigurationReason");
			var reasonText = Assert.IsType<string>(reason.Text);
			Assert.DoesNotContain('\n', reasonText);
			Assert.Contains("was not found", reasonText, StringComparison.Ordinal);
			Assert.Empty(UiTestDriver.GetToastService(window).Items);

			var copy = Assert.Single(
				dialog.GetVisualDescendants().OfType<Button>(),
				static button => Equals(button.Content, "Copy"));
			Assert.Equal("Copy", AutomationProperties.GetName(copy));

			dialog.Close();
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => window.OwnedWindows.Count == 0,
				"the manual configuration dialog to close");
			await UiTestDriver.WaitForSettledFramesAsync(frameCount: 4);
			Assert.Empty(window.OwnedWindows);
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	private static TerminalCommandSetupSnapshot CreateTerminalSnapshot(
		string root,
		TerminalCommandSetupState state)
	{
		var executable = Path.Combine(root, OperatingSystem.IsWindows() ? "devprojex.exe" : "devprojex");
		return new TerminalCommandSetupSnapshot(
			"devprojex",
			state,
			CommandPath: state == TerminalCommandSetupState.Installed ? executable : null,
			TargetExecutablePath: executable,
			InstalledTargetExecutablePath: state == TerminalCommandSetupState.Installed ? executable : null,
			UserBinDirectory: root,
			UserBinDirectoryIsInPath: state == TerminalCommandSetupState.Installed,
			CanInstall: state == TerminalCommandSetupState.NotInstalled,
			CanRepair: false,
			ShellProfileHint: null,
			PathSetupCommand: "export PATH=\"$HOME/.local/bin:$PATH\"");
	}

	private sealed class RecordingMcpConnectionService(
		Func<McpConnectionRequest, McpConnectionResult> resultFactory) : IMcpConnectionService
	{
		public List<McpConnectionRequest> Requests { get; } = [];

		public Task<McpConnectionResult> ConnectAsync(
			McpConnectionRequest request,
			CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			Requests.Add(request);
			return Task.FromResult(resultFactory(request));
		}

		public string CreatePrintableConfiguration(McpConnectionRequest request) =>
			"{\"mcpServers\":{\"devprojex\":{}}}";
	}

	private sealed class StubTerminalCommandSetupService(TerminalCommandSetupSnapshot snapshot)
		: ITerminalCommandSetupService
	{
		public TerminalCommandSetupSnapshot Probe() => snapshot;

		public TerminalCommandInstallResult InstallOrRepair() => throw new NotSupportedException();

		public TerminalCommandPathSetupResult ConfigurePath() => throw new NotSupportedException();

		public TerminalCommandInstallResult Reinstall() => throw new NotSupportedException();
	}
}
