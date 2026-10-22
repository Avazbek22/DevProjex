using Avalonia.VisualTree;
using DevProjex.Application.Services;
using DevProjex.Infrastructure.TerminalCommands;
using DevProjex.Kernel.Abstractions;

namespace DevProjex.Tests.UI;

[Collection(UiWorkspaceCollection.Name)]
public sealed class McpExistingEntryConflictUiTests(UiWorkspaceFixture workspace)
{
	private const string ExistingCommand = "npx -y devprojex mcp --root /home/person/old --hide-private-data";
	private const string ProposedCommand = "/opt/DevProjex mcp --root /home/person/project";
	private const string ManualCommand = "claude mcp add --scope local devprojex -- /opt/DevProjex mcp --root /home/person/project";

	[AvaloniaTheory]
	[InlineData("McpConnectClaudeCodeMenuItem", "Claude Code")]
	[InlineData("McpConnectCodexMenuItem", "Codex")]
	public async Task TheQuestionShowsBothCommandsAndClosingItKeepsTheEntry(string menuName, string clientName)
	{
		var (window, service, launcher) = await OpenAsync(TerminalCommandSetupState.Installed);
		try
		{
			var dialog = await ClickAndWaitForQuestionAsync(window, menuName);
			var text = DialogText(dialog);

			Assert.Equal("Existing devprojex entry", dialog.Title);
			Assert.Contains(clientName + " already has a devprojex entry that starts DevProjex another way", text, StringComparison.Ordinal);
			Assert.Contains(ExistingCommand, text, StringComparison.Ordinal);
			Assert.Contains(ProposedCommand, text, StringComparison.Ordinal);
			Assert.Contains("Not kept after replacing: --hide-private-data", text, StringComparison.Ordinal);
			Assert.Equal(
				new[] { "Keep my entry", "Copy command", "Replace" },
				dialog.GetVisualDescendants().OfType<Button>()
					.Where(static button => button is not RepeatButton)
					.Select(static button => button.Content as string));

			await UiTestDriver.CloseTopLevelWindowAsync(dialog);
			await UiTestDriver.WaitForConditionAsync(window, () => window.OwnedWindows.Count == 0, "the closed question");

			Assert.Single(service.Requests);
			Assert.Empty(launcher.Requests);
		}
		finally
		{
			await CloseAsync(window);
		}
	}

	[AvaloniaFact]
	public async Task KeepLeavesTheEntryWithoutAnotherDialog()
	{
		var (window, service, launcher) = await OpenAsync(TerminalCommandSetupState.Installed);
		try
		{
			var dialog = await ClickAndWaitForQuestionAsync(window, "McpConnectClaudeCodeMenuItem");
			await UiTestDriver.RaiseButtonClickAsync(Button(dialog, "Keep my entry"));
			await UiTestDriver.WaitForConditionAsync(window, () => window.OwnedWindows.Count == 0, "the kept entry");

			Assert.Single(service.Requests);
			Assert.Empty(launcher.Requests);
			Assert.Empty(window.OwnedWindows);
		}
		finally
		{
			await CloseAsync(window);
		}
	}

	[AvaloniaFact]
	public async Task ReplaceConfirmsWithTheFingerprintAndThenOpensTheClient()
	{
		var (window, service, launcher) = await OpenAsync(TerminalCommandSetupState.Installed);
		try
		{
			var dialog = await ClickAndWaitForQuestionAsync(window, "McpConnectClaudeCodeMenuItem");
			await UiTestDriver.RaiseButtonClickAsync(Button(dialog, "Replace"));
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => service.Requests.Count == 2 && launcher.Requests.Count == 1,
				"the confirmed replacement");

			Assert.False(service.Requests[0].ReplaceExistingFields);
			Assert.True(service.Requests[1].ReplaceExistingFields);
			Assert.Equal("entry-fingerprint", service.Requests[1].ExpectedExistingEntryFingerprint);
			Assert.Equal(service.Requests[0] with { ReplaceExistingFields = true, ExpectedExistingEntryFingerprint = "entry-fingerprint" },
				service.Requests[1]);
		}
		finally
		{
			await CloseAsync(window);
		}
	}

	[AvaloniaFact]
	public async Task CopyPutsTheManualCommandOnTheClipboardAndChangesNothing()
	{
		var (window, service, launcher) = await OpenAsync(TerminalCommandSetupState.Installed);
		try
		{
			await window.Clipboard!.SetTextAsync("previous clipboard");
			var dialog = await ClickAndWaitForQuestionAsync(window, "McpConnectClaudeCodeMenuItem");
			await UiTestDriver.RaiseButtonClickAsync(Button(dialog, "Copy command"));
			await UiTestDriver.WaitForConditionAsync(window, () => window.OwnedWindows.Count == 0, "the copy choice");
			Dispatcher.UIThread.RunJobs();

			Assert.Equal(ManualCommand, await ClipboardExtensions.TryGetTextAsync(window.Clipboard!));
			Assert.Contains(UiTestDriver.GetToastService(window).Items,
				toast => toast.Message.Contains("Connection command copied", StringComparison.Ordinal));
			Assert.Single(service.Requests);
			Assert.Empty(launcher.Requests);
		}
		finally
		{
			await CloseAsync(window);
		}
	}

	[AvaloniaFact]
	public async Task AKeptEquivalentEntryOpensTheClientWithoutOfferingThePathCommand()
	{
		var service = new RecordingConnectionService(_ => new McpConnectionResult(
			McpConnectionStatus.Connected,
			"Claude Code already starts DevProjex for this project with its existing entry.",
			NextCommand: "claude",
			KeptExistingEntry: true));
		var launcher = new RecordingLaunchService();
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(
			workspace.Project,
			configureServices: services => services with
			{
				McpConnectionService = service,
				McpClientLaunchService = launcher,
				TerminalCommandSetupService = new StubTerminalCommandSetupService(
					Snapshot(workspace.Project.RootPath, TerminalCommandSetupState.NotInstalled))
			});
		try
		{
			await UiTestDriver.RaiseMenuItemClickAsync(
				UiTestDriver.GetRequiredTopMenuControl<MenuItem>(window, "McpConnectClaudeCodeMenuItem"));
			await UiTestDriver.WaitForConditionAsync(window, () => launcher.Requests.Count == 1, "the opened client");
			Dispatcher.UIThread.RunJobs();

			Assert.Empty(window.OwnedWindows);
		}
		finally
		{
			await CloseAsync(window);
		}
	}

	private async Task<(MainWindow Window, RecordingConnectionService Service, RecordingLaunchService Launcher)> OpenAsync(
		TerminalCommandSetupState terminalState)
	{
		var service = new RecordingConnectionService(request =>
			request.ReplaceExistingFields
				? new McpConnectionResult(McpConnectionStatus.Updated, "updated", NextCommand: "claude", Replaced: true)
				: new McpConnectionResult(
					McpConnectionStatus.InvalidConfiguration,
					"confirmation required",
					ManualConfiguration: ManualCommand,
					ExistingEntryFingerprint: "entry-fingerprint",
					ExistingEntryConflict: new McpExistingEntryConflict(
						ExistingCommand,
						ProposedCommand,
						["--hide-private-data"],
						IsDevProjexLauncher: true)));
		var launcher = new RecordingLaunchService();
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(
			workspace.Project,
			configureServices: services => services with
			{
				McpConnectionService = service,
				McpClientLaunchService = launcher,
				TerminalCommandSetupService = new StubTerminalCommandSetupService(
					Snapshot(workspace.Project.RootPath, terminalState))
			});
		return (window, service, launcher);
	}

	private static async Task<Window> ClickAndWaitForQuestionAsync(MainWindow window, string menuName)
	{
		await UiTestDriver.RaiseMenuItemClickAsync(UiTestDriver.GetRequiredTopMenuControl<MenuItem>(window, menuName));
		await UiTestDriver.WaitForConditionAsync(window, () => window.OwnedWindows.Count == 1, "the existing entry question");
		return Assert.Single(window.OwnedWindows);
	}

	private static string DialogText(Window dialog) =>
		string.Join('\n', dialog.GetVisualDescendants().OfType<TextBlock>().Select(static block => block.Text));

	private static Button Button(Window dialog, string label) =>
		Assert.Single(dialog.GetVisualDescendants().OfType<Button>(), button => Equals(button.Content, label));

	private static async Task CloseAsync(MainWindow window)
	{
		foreach (var dialog in window.OwnedWindows.ToArray())
			dialog.Close();
		await UiTestDriver.CloseWindowAsync(window);
	}

	private static TerminalCommandSetupSnapshot Snapshot(string root, TerminalCommandSetupState state)
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

	private sealed class RecordingConnectionService(Func<McpConnectionRequest, McpConnectionResult> resultFactory)
		: IMcpConnectionService
	{
		public List<McpConnectionRequest> Requests { get; } = [];

		public Task<McpConnectionResult> ConnectAsync(McpConnectionRequest request, CancellationToken cancellationToken = default)
		{
			Requests.Add(request);
			return Task.FromResult(resultFactory(request));
		}

		public string CreatePrintableConfiguration(McpConnectionRequest request) => ManualCommand;
	}

	private sealed class RecordingLaunchService : IMcpClientLaunchService
	{
		public List<McpClientLaunchRequest> Requests { get; } = [];

		public Task<McpClientLaunchResult> OpenAsync(McpClientLaunchRequest request, CancellationToken cancellationToken = default)
		{
			Requests.Add(request);
			return Task.FromResult(new McpClientLaunchResult(McpClientLaunchStatus.Opened));
		}
	}

	private sealed class StubTerminalCommandSetupService(TerminalCommandSetupSnapshot snapshot) : ITerminalCommandSetupService
	{
		public TerminalCommandSetupSnapshot Probe() => snapshot;

		public TerminalCommandInstallResult InstallOrRepair() => throw new NotSupportedException();

		public TerminalCommandPathSetupResult ConfigurePath() => throw new NotSupportedException();

		public TerminalCommandInstallResult Reinstall() => throw new NotSupportedException();
	}
}
