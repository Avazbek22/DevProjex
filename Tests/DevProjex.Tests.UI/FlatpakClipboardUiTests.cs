using Avalonia.VisualTree;
using DevProjex.Application.Services;

namespace DevProjex.Tests.UI;

[Collection(UiWorkspaceCollection.Name)]
public sealed class FlatpakClipboardUiTests(UiWorkspaceFixture workspace)
{
	[AvaloniaTheory]
	[InlineData("McpStandardConnectClaudeCodeMenuItem", "claude")]
	[InlineData("McpStandardConnectCodexMenuItem", "codex")]
	[InlineData("McpStandardConnectVsCodeMenuItem", "code")]
	public async Task ClientActionCopiesItsHostCommandAndExplainsIt(string menuName, string command)
	{
		using var sandbox = FlatpakEnvironment.UseForTesting(new(true, "io.github.Avazbek22.DevProjex"));
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project,
			configureServices: services => services with
			{
				McpConnectionService = new ConnectionService(),
				McpClientLaunchService = new DevProjex.Infrastructure.TerminalCommands.McpClientLaunchService(services.Localization)
			});
		try
		{
			var menu = UiTestDriver.GetRequiredTopMenuControl<MenuItem>(window, menuName);
			Assert.Contains("Copy", menu.Header?.ToString());
			await UiTestDriver.RaiseMenuItemClickAsync(menu);
			await UiTestDriver.WaitForConditionAsync(window, () => window.OwnedWindows.Count != 0,
				"the host-command explanation");
			var expected = command == "code" ? "code " + FlatpakEnvironment.QuoteArgument(workspace.Project.RootPath) :
				"cd " + FlatpakEnvironment.QuoteArgument(workspace.Project.RootPath) + " && " + command;
			Assert.Equal(expected, await ClipboardExtensions.TryGetTextAsync(window.Clipboard!));
			Assert.Contains(Assert.Single(window.OwnedWindows).GetVisualDescendants().OfType<TextBlock>(),
				block => block.Text?.Contains("Run this command in your own terminal.", StringComparison.Ordinal) == true);
		}
		finally
		{
			foreach (var dialog in window.OwnedWindows.ToArray()) dialog.Close();
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	[AvaloniaTheory]
	[InlineData("McpStandardConnectClaudeCodeMenuItem", "claude")]
	[InlineData("McpStandardConnectCodexMenuItem", "codex")]
	public async Task ManualRegistrationCopiesTheRegistrationCommandTheDialogShows(string menuName, string command)
	{
		using var sandbox = FlatpakEnvironment.UseForTesting(new(true, "io.github.Avazbek22.DevProjex"));
		var connection = new ManualConnectionService();
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project,
			configureServices: services => services with
			{
				McpConnectionService = connection,
				McpClientLaunchService = new DevProjex.Infrastructure.TerminalCommands.McpClientLaunchService(services.Localization)
			});
		try
		{
			await window.Clipboard!.SetTextAsync("previous clipboard");
			await UiTestDriver.RaiseMenuItemClickAsync(UiTestDriver.GetRequiredTopMenuControl<MenuItem>(window, menuName));
			await UiTestDriver.WaitForConditionAsync(window, () => window.OwnedWindows.Count != 0,
				"the manual registration dialog");
			var dialog = Assert.Single(window.OwnedWindows);
			var registration = McpConnectionFragmentGenerator.GenerateHostClientCommand(Assert.Single(connection.Requests));
			// Pasting the clipboard must register DevProjex, not start a client that has no DevProjex server.
			Assert.Equal(registration, await ClipboardExtensions.TryGetTextAsync(window.Clipboard!));
			Assert.Contains(command + " mcp add", registration, StringComparison.Ordinal);
			Assert.Contains("flatpak run io.github.Avazbek22.DevProjex mcp --root", registration, StringComparison.Ordinal);
			Assert.Contains(dialog.GetVisualDescendants().OfType<TextBox>(),
				box => string.Equals(box.Text, registration, StringComparison.Ordinal));
			Assert.Equal("Manual MCP configuration", dialog.Title);
		}
		finally
		{
			foreach (var dialog in window.OwnedWindows.ToArray()) dialog.Close();
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	private sealed class ManualConnectionService : IMcpConnectionService
	{
		public List<McpConnectionRequest> Requests { get; } = [];

		public Task<McpConnectionResult> ConnectAsync(McpConnectionRequest request, CancellationToken cancellationToken = default)
		{
			Requests.Add(request);
			return Task.FromResult(new McpConnectionResult(McpConnectionStatus.ManualConfiguration, "Run this command in your own terminal.",
				ManualConfiguration: McpConnectionFragmentGenerator.GenerateHostClientCommand(request)));
		}
		public string CreatePrintableConfiguration(McpConnectionRequest request) => McpConnectionFragmentGenerator.GenerateHostClientCommand(request);
	}

	private sealed class ConnectionService : IMcpConnectionService
	{
		public Task<McpConnectionResult> ConnectAsync(McpConnectionRequest request, CancellationToken cancellationToken = default) =>
			Task.FromResult(new McpConnectionResult(McpConnectionStatus.Connected, "Connected"));
		public string CreatePrintableConfiguration(McpConnectionRequest request) => "{}";
	}
}
