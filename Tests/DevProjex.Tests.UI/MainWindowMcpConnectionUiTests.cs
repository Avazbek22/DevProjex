using Avalonia.Automation;
using Avalonia.VisualTree;
using DevProjex.Application.Services;
using DevProjex.Infrastructure.TerminalCommands;
using DevProjex.Kernel.Abstractions;

namespace DevProjex.Tests.UI;

[Collection(UiWorkspaceCollection.Name)]
public sealed class MainWindowMcpConnectionUiTests(UiWorkspaceFixture workspace)
{
	[AvaloniaFact]
	public async Task OpenMenus_PassLiveAndStandardModesAndOpenClientWithoutSuccessToast()
	{
		var service = new RecordingMcpConnectionService(request => new McpConnectionResult(
			McpConnectionStatus.Connected,
			$"{request.Client} connected"));
		var launcher = new RecordingMcpClientLaunchService(_ => new McpClientLaunchResult(
			McpClientLaunchStatus.Opened));
		var terminalCommand = new StubTerminalCommandSetupService(
			CreateTerminalSnapshot(workspace.Project.RootPath, TerminalCommandSetupState.Installed));
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(
			workspace.Project,
			configureServices: services => services with
			{
				McpConnectionService = service,
				McpClientLaunchService = launcher,
				TerminalCommandSetupService = terminalCommand
			});

		try
		{
			var cursor = UiTestDriver.GetRequiredTopMenuControl<MenuItem>(
				window,
				"McpConnectCursorMenuItem");
			await UiTestDriver.RaiseMenuItemClickAsync(cursor);
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => service.Requests.Count == 1 && launcher.Requests.Count == 1,
				"the MCP client launch request");

			var firstRequest = Assert.Single(service.Requests);
			Assert.Equal(McpConnectionClient.Cursor, firstRequest.Client);
			Assert.Equal(McpConnectionMode.Live, firstRequest.Mode);
			Assert.Equal(Path.GetFullPath(workspace.Project.RootPath), firstRequest.ProjectRoot);
			var launchRequest = Assert.Single(launcher.Requests);
			Assert.Equal(McpConnectionClient.Cursor, launchRequest.Client);
			Assert.Equal(firstRequest.ProjectRoot, launchRequest.ProjectRoot);
			Assert.Empty(UiTestDriver.GetToastService(window).Items);
			Assert.Empty(window.OwnedWindows);

			var standardCursor = UiTestDriver.GetRequiredTopMenuControl<MenuItem>(
				window,
				"McpStandardConnectCursorMenuItem");
			await UiTestDriver.RaiseMenuItemClickAsync(standardCursor);
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => service.Requests.Count == 2 && launcher.Requests.Count == 2,
				"the standard MCP client launch request");

			Assert.Equal(McpConnectionMode.Standard, service.Requests[1].Mode);
			Assert.Equal(McpConnectionClient.Cursor, service.Requests[1].Client);
			Assert.Empty(UiTestDriver.GetToastService(window).Items);
			Assert.Empty(window.OwnedWindows);
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	[AvaloniaTheory]
	[InlineData((int)TerminalCommandSetupState.NotInstalled)]
	[InlineData((int)TerminalCommandSetupState.InstalledPathMissing)]
	[InlineData((int)TerminalCommandSetupState.Stale)]
	public async Task SuccessfulConnectionAndOpen_ShowsPathPromptForActionableState(int stateValue)
	{
		var service = new RecordingMcpConnectionService(_ => new McpConnectionResult(
			McpConnectionStatus.Connected,
			"Cursor connected"));
		var launcher = new RecordingMcpClientLaunchService(_ => new McpClientLaunchResult(
			McpClientLaunchStatus.Opened));
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(
			workspace.Project,
			configureServices: services => services with
			{
				McpConnectionService = service,
				McpClientLaunchService = launcher,
				TerminalCommandSetupService = new StubTerminalCommandSetupService(
					CreateTerminalSnapshot(
						workspace.Project.RootPath,
						(TerminalCommandSetupState)stateValue))
			});

		try
		{
			var cursor = UiTestDriver.GetRequiredTopMenuControl<MenuItem>(
				window,
				"McpConnectCursorMenuItem");
			await UiTestDriver.RaiseMenuItemClickAsync(cursor);
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => window.OwnedWindows.Count == 1 &&
					string.Equals(window.OwnedWindows[0].Title, "Terminal command", StringComparison.Ordinal),
				"the MCP PATH prompt");

			var prompt = Assert.Single(window.OwnedWindows);
			Assert.Equal("Terminal command", prompt.Title);
			Assert.Empty(UiTestDriver.GetToastService(window).Items);
			prompt.Close();
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => window.OwnedWindows.Count == 0,
				"the MCP PATH prompt to close");
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	[AvaloniaFact]
	public async Task CodexConnectionForAnotherProjectRequiresConfirmationAndReportsReplacement()
	{
		var previousProject = Path.GetFullPath(Path.Combine(workspace.Project.RootPath, "..", "previous-project"));
		var service = new ReplacementMcpConnectionService(previousProject);
		var launcher = new RecordingMcpClientLaunchService(_ => new McpClientLaunchResult(
			McpClientLaunchStatus.Opened));
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(
			workspace.Project,
			configureServices: services => services with
			{
				McpConnectionService = service,
				McpClientLaunchService = launcher,
				TerminalCommandSetupService = new StubTerminalCommandSetupService(
					CreateTerminalSnapshot(workspace.Project.RootPath, TerminalCommandSetupState.Installed))
			});

		try
		{
			var codex = UiTestDriver.GetRequiredTopMenuControl<MenuItem>(window, "McpConnectCodexMenuItem");
			await UiTestDriver.RaiseMenuItemClickAsync(codex);
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => window.OwnedWindows.Count == 1,
				"the Codex replacement confirmation");

			var confirmation = Assert.Single(window.OwnedWindows);
			var message = string.Join(
				' ',
				confirmation.GetVisualDescendants().OfType<TextBlock>().Select(static item => item.Text));
			Assert.Contains(previousProject, message, StringComparison.Ordinal);
			Assert.Contains(Path.GetFullPath(workspace.Project.RootPath), message, StringComparison.Ordinal);
			var replace = Assert.Single(
				confirmation.GetVisualDescendants().OfType<Button>(),
				static button => Equals(button.Content, "Replace connection"));
			await UiTestDriver.RaiseButtonClickAsync(replace);
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => service.ReplaceRequests.Count == 1 && launcher.Requests.Count == 1,
				"the confirmed Codex replacement");

			Assert.Empty(service.ConnectRequests);
			Assert.Equal(previousProject, Assert.Single(service.ExpectedRoots));
			Assert.Contains(
				UiTestDriver.GetToastService(window).Items,
				static item => item.Message == "Codex connection replaced");
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	[AvaloniaFact]
	public async Task SuccessfulConnectionWithLaunchFailure_ShowsManualLaunchDialog()
	{
		var service = new RecordingMcpConnectionService(_ => new McpConnectionResult(
			McpConnectionStatus.Connected,
			"Cursor connected"));
		var launcher = new RecordingMcpClientLaunchService(_ => new McpClientLaunchResult(
			McpClientLaunchStatus.Failed,
			"URL handler unavailable.",
			"cursor \"C:/Проекты/Мой проект\""));
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(
			workspace.Project,
			configureServices: services => services with
			{
				McpConnectionService = service,
				McpClientLaunchService = launcher,
				TerminalCommandSetupService = new StubTerminalCommandSetupService(
					CreateTerminalSnapshot(workspace.Project.RootPath, TerminalCommandSetupState.Stale))
			});

		try
		{
			var cursor = UiTestDriver.GetRequiredTopMenuControl<MenuItem>(
				window,
				"McpConnectCursorMenuItem");
			await UiTestDriver.RaiseMenuItemClickAsync(cursor);
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => window.OwnedWindows.Count == 1,
				"the MCP launch failure dialog");

			var dialog = Assert.Single(window.OwnedWindows);
			var reason = Assert.Single(
				dialog.GetVisualDescendants().OfType<TextBlock>(),
				static control => control.Name == "McpManualConfigurationReason");
			var reasonText = Assert.IsType<string>(reason.Text);
			Assert.Contains("server is connected", reasonText, StringComparison.OrdinalIgnoreCase);
			Assert.Contains("URL handler unavailable", reasonText, StringComparison.Ordinal);
			var command = Assert.Single(
				dialog.GetVisualDescendants().OfType<TextBox>(),
				static control => control.Name == "McpManualConfigurationText");
			Assert.StartsWith("cursor", Assert.IsType<string>(command.Text), StringComparison.Ordinal);
			Assert.Equal("Command", AutomationProperties.GetName(command));
			Assert.Empty(UiTestDriver.GetToastService(window).Items);
			dialog.Close();
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => window.OwnedWindows.Count == 1 &&
					string.Equals(window.OwnedWindows[0].Title, "Terminal command", StringComparison.Ordinal),
				"the MCP PATH prompt after closing the launch failure dialog");

			var prompt = Assert.Single(window.OwnedWindows);
			Assert.Equal("Terminal command", prompt.Title);
			prompt.Close();
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => window.OwnedWindows.Count == 0,
				"the MCP PATH prompt to close");
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
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

	private sealed class ReplacementMcpConnectionService(string existingProjectRoot)
		: IMcpConnectionService, IMcpConnectionReplacementService
	{
		public List<McpConnectionRequest> ConnectRequests { get; } = [];
		public List<McpConnectionRequest> ReplaceRequests { get; } = [];
		public List<string> ExpectedRoots { get; } = [];

		public Task<McpConnectionInspection> InspectAsync(
			McpConnectionRequest request,
			CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			return Task.FromResult(new McpConnectionInspection(
				true,
				existingProjectRoot,
				RequiresProjectReplacement: true));
		}

		public Task<McpConnectionResult> ReplaceAsync(
			McpConnectionRequest request,
			string expectedExistingProjectRoot,
			CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			ReplaceRequests.Add(request);
			ExpectedRoots.Add(expectedExistingProjectRoot);
			return Task.FromResult(new McpConnectionResult(
				McpConnectionStatus.Updated,
				"Codex connection replaced",
				Replaced: true));
		}

		public Task<McpConnectionResult> ConnectAsync(
			McpConnectionRequest request,
			CancellationToken cancellationToken = default)
		{
			ConnectRequests.Add(request);
			return Task.FromResult(new McpConnectionResult(
				McpConnectionStatus.Connected,
				"connected"));
		}

		public string CreatePrintableConfiguration(McpConnectionRequest request) => "{}";
	}

	private sealed class StubTerminalCommandSetupService(TerminalCommandSetupSnapshot snapshot)
		: ITerminalCommandSetupService
	{
		public TerminalCommandSetupSnapshot Probe() => snapshot;

		public TerminalCommandInstallResult InstallOrRepair() => throw new NotSupportedException();

		public TerminalCommandPathSetupResult ConfigurePath() => throw new NotSupportedException();

		public TerminalCommandInstallResult Reinstall() => throw new NotSupportedException();
	}

	private sealed class RecordingMcpClientLaunchService(
		Func<McpClientLaunchRequest, McpClientLaunchResult> resultFactory) : IMcpClientLaunchService
	{
		public List<McpClientLaunchRequest> Requests { get; } = [];

		public Task<McpClientLaunchResult> OpenAsync(
			McpClientLaunchRequest request,
			CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			Requests.Add(request);
			return Task.FromResult(resultFactory(request));
		}
	}
}
