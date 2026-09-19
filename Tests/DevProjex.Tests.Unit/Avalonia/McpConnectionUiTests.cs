using DevProjex.Application.Services;
using DevProjex.Avalonia.Services;
using DevProjex.Avalonia.Views;
using DevProjex.Infrastructure.TerminalCommands;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;

namespace DevProjex.Tests.Unit.Avalonia;

public sealed class McpConnectionUiTests
{
	private static readonly string[] RequiredLocalizationKeys =
	[
		"Menu.Mcp",
		"Menu.Mcp.LiveContext",
		"Menu.Mcp.Documentation",
		"Menu.Mcp.OpenClaudeCode",
		"Menu.Mcp.OpenCodex",
		"Menu.Mcp.OpenCursor",
		"Menu.Mcp.OpenVsCode",
		"Menu.Mcp.OtherClients",
		"Dialog.McpPath.Title",
		"Dialog.McpPath.Body",
		"Dialog.McpManual.Title",
		"Dialog.McpManual.Configuration",
		"Dialog.McpManual.Command",
		"Dialog.McpManual.Paths",
		"Mcp.Connect.ClaudeCode.Connected",
		"Mcp.Connect.ClaudeCode.Updated",
		"Mcp.Connect.Codex.Connected",
		"Mcp.Connect.Codex.Updated",
		"Mcp.Connect.ClientNotFound",
		"Mcp.Connect.ProjectConfigurationFailed",
		"Mcp.Connect.UnknownError",
		"Mcp.Connect.ProjectConfigurationUpdated",
		"Mcp.Connect.RestartClient",
		"Mcp.Connect.ManualConfiguration",
		"Mcp.Connect.CommandTimedOut",
		"Mcp.Connect.CommandFailed",
		"Mcp.Connect.CommandFailedAfterRemoval",
		"Mcp.Connect.ManualFallbackHint",
		"Mcp.Connect.OutputIncomplete",
		"Mcp.Open.FailedAfterConnection",
		"Mcp.Open.Succeeded",
		"Mcp.Open.ClientNotFound",
		"Mcp.Open.TerminalNotFound",
		"Mcp.Open.ProcessStartFailed",
		"Mcp.Open.DispatcherTimedOut",
		"Mcp.Open.DispatcherExited",
		"Mcp.Open.TerminalStartFailed",
		"Mcp.Open.EditorStartFailed",
		"Mcp.Open.ManualJsonUnsupported",
		"Terminal.Command.McpConnect",
		"Terminal.Option.McpClient",
		"Terminal.Option.McpConnectionMode",
		"Terminal.Option.McpPrint",
		"Terminal.Option.McpOpen",
		"Terminal.Option.RelatedDepth",
		"Terminal.Validation.McpClient",
		"Terminal.Validation.McpConnectionMode",
		"Terminal.Validation.McpOpenJson",
		"Terminal.Validation.McpPrintOpenConflict",
		"Terminal.Validation.RelatedDepth",
		"Terminal.Tui.Command.Mcp.Description",
		"Terminal.Tui.Command.Mcp.Schema",
		"Terminal.Tui.Command.Related.Title",
		"Terminal.Tui.Command.Related.Description",
		"Terminal.Tui.Command.Related.Schema",
		"Dialog.LiveContext.Secrets.Title",
		"Dialog.LiveContext.Secrets.Message",
		"Dialog.LiveContext.Secrets.Apply"
	];

	[Fact]
	public void PathPromptPolicy_OnlyOffersTheThreeActionableStatesOnSupportedPlatforms()
	{
		foreach (var platform in Enum.GetValues<TerminalCommandHostPlatform>())
		{
			foreach (var state in Enum.GetValues<TerminalCommandSetupState>())
			{
				var expected = platform != TerminalCommandHostPlatform.Other &&
					state is TerminalCommandSetupState.NotInstalled or
						TerminalCommandSetupState.InstalledPathMissing or
						TerminalCommandSetupState.Stale;
				Assert.Equal(expected, McpConnectionPathPromptPolicy.ShouldShow(state, platform));
			}
		}
	}

	[Theory]
	[InlineData((int)TerminalCommandSetupState.NotInstalled, (int)McpConnectionPathAction.InstallOrRepair)]
	[InlineData((int)TerminalCommandSetupState.Stale, (int)McpConnectionPathAction.InstallOrRepair)]
	[InlineData((int)TerminalCommandSetupState.InstalledPathMissing, (int)McpConnectionPathAction.ConfigurePath)]
	public void PathPromptPolicy_WindowsUsesTheExistingSetupActions(int stateValue, int actionValue)
	{
		var state = (TerminalCommandSetupState)stateValue;

		var content = McpConnectionPathPromptPolicy.Create(
			CreateLocalization(),
			Snapshot(state),
			TerminalCommandHostPlatform.Windows);

		Assert.Equal((McpConnectionPathAction)actionValue, content.Action);
		Assert.Empty(content.Command);
	}

	[Theory]
	[InlineData((int)TerminalCommandHostPlatform.Linux)]
	[InlineData((int)TerminalCommandHostPlatform.MacOS)]
	public void PathPromptPolicy_UnixShowsTheShellProfileCommand(int platformValue)
	{
		var content = McpConnectionPathPromptPolicy.Create(
			CreateLocalization(),
			Snapshot(TerminalCommandSetupState.NotInstalled),
			(TerminalCommandHostPlatform)platformValue);

		Assert.Equal(McpConnectionPathAction.None, content.Action);
		Assert.Equal("export PATH=\"$HOME/.local/bin:$PATH\"", content.Command);
	}

	[AvaloniaFact]
	public void PathPromptDialog_SizesToLocalizedContent_WithAndWithoutCommand()
	{
		var owner = new Window();
		try
		{
			foreach (var command in new[] { string.Empty, "export PATH=\"$HOME/.local/bin:$PATH\"" })
			{
				var content = new McpConnectionPathDialogContent(
					"Lệnh thiết bị đầu cuối",
					"Đoạn kết nối đã được sao chép và sẵn sàng. Để dùng devprojex trong thiết bị đầu cuối, hãy thêm lệnh vào PATH.",
					command,
					"Sao chép lệnh",
					"Thiết lập",
					"Để sau",
					McpConnectionPathAction.None);
				var completion = new TaskCompletionSource<McpConnectionPathAction>(
					TaskCreationOptions.RunContinuationsAsynchronously);
				var window = McpConnectionPathDialog.CreateDialogWindow(owner, content, completion);

				try
				{
					Assert.Equal(SizeToContent.Height, window.SizeToContent);
					Assert.True(double.IsNaN(window.Height));
					var panel = Assert.IsType<StackPanel>(window.Content);
					panel.Measure(new Size(540, double.PositiveInfinity));
					Assert.True(panel.DesiredSize.Height > 0);
				}
				finally
				{
					window.Close();
				}
			}
		}
		finally
		{
			owner.Close();
		}
	}

	[AvaloniaFact]
	public void Menu_IsBetweenFileAndGitAndPublishesTheRequestedFormat()
	{
		var localization = CreateLocalization();
		var viewModel = new MainWindowViewModel(localization, new HelpContentProvider());
		var view = new TopMenuBarView { DataContext = viewModel };
		var menu = Assert.IsType<Menu>(view.FindControl<Menu>("MainMenu"));
		var items = menu.Items.OfType<MenuItem>().ToList();
		var fileIndex = items.IndexOf(Assert.IsType<MenuItem>(view.FindControl<MenuItem>("FileMenuItem")));
		var mcpIndex = items.IndexOf(Assert.IsType<MenuItem>(view.FindControl<MenuItem>("McpMenuItem")));
		var gitIndex = items.IndexOf(Assert.IsType<MenuItem>(view.FindControl<MenuItem>("GitMenuItem")));
		Assert.Equal(fileIndex + 1, mcpIndex);
		Assert.Equal(mcpIndex + 1, gitIndex);

		var live = Assert.IsType<MenuItem>(view.FindControl<MenuItem>("McpLiveContextMenuItem"));
		var documentation = Assert.IsType<MenuItem>(view.FindControl<MenuItem>("McpDocumentationMenuItem"));
		var topLevelMcpItems = Assert.IsType<MenuItem>(items[mcpIndex]).Items.OfType<MenuItem>().ToArray();
		Assert.Collection(
			topLevelMcpItems,
			item => Assert.Same(live, item),
			item => Assert.Same(documentation, item));
		Assert.Equal(viewModel.MenuMcpLiveContext, live.Header);
		Assert.Equal(viewModel.MenuMcpLiveContext, AutomationProperties.GetName(live));
		Assert.True(live.IsEnabled);
		Assert.True(documentation.IsEnabled);

		var connectItems = new[]
		{
			"McpConnectClaudeCodeMenuItem",
			"McpConnectCodexMenuItem",
			"McpConnectCursorMenuItem",
			"McpConnectVsCodeMenuItem",
			"McpOtherClientsMenuItem"
		}.Select(name => Assert.IsType<MenuItem>(view.FindControl<MenuItem>(name))).ToArray();
		Assert.All(connectItems, static item => Assert.False(item.IsEnabled));
		Assert.Equal(5, live.Items.OfType<MenuItem>().Count());
		Assert.Equal(6, live.Items.Count);
		viewModel.IsProjectLoaded = true;
		Assert.All(connectItems, static item => Assert.True(item.IsEnabled));

		McpConnectionRequestedEventArgs? requested = null;
		view.McpConnectionRequested += (_, args) => requested = args;
		var item = Assert.IsType<MenuItem>(view.FindControl<MenuItem>("McpConnectCursorMenuItem"));
		item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
		Assert.NotNull(requested);
		Assert.Equal(McpConnectionClient.Cursor, requested.Client);
		Assert.Equal(viewModel.MenuMcpOpenCursor, AutomationProperties.GetName(item));
	}

	[AvaloniaFact]
	public void ManualConfigurationDialog_PresentsReasonPayloadPathsAndAutomationNames()
	{
		var localization = CreateLocalization();
		var result = new McpConnectionResult(
			McpConnectionStatus.ClientNotFound,
			"Claude Code was not found.\nUse the configuration below.",
			ManualConfiguration: "{\"mcpServers\":{}}",
			SuggestedConfigPaths:
			[
				@"%APPDATA%\Claude\claude_desktop_config.json",
				"~/Library/Application Support/Claude/claude_desktop_config.json"
			]);
		var content = McpManualConfigurationDialog.CreateContent(
			localization,
			result,
			"fallback");
		var owner = new Window();
		var dialog = McpManualConfigurationDialog.CreateDialogWindow(owner, content);

		try
		{
			Assert.DoesNotContain('\n', content.Reason);
			Assert.Equal("{\"mcpServers\":{}}", content.Payload);
			var textBox = Assert.Single(
				dialog.GetLogicalDescendants().OfType<TextBox>(),
				static control => control.Name == "McpManualConfigurationText");
			Assert.True(textBox.IsReadOnly);
			Assert.Equal(content.Payload, textBox.Text);
			Assert.Equal(content.PayloadLabel, AutomationProperties.GetName(textBox));
			var paths = Assert.Single(
				dialog.GetLogicalDescendants().OfType<SelectableTextBlock>(),
				static control => control.Name == "McpManualConfigurationPaths");
			Assert.Contains("claude_desktop_config.json", paths.Text, StringComparison.Ordinal);
			Assert.All(
				dialog.GetLogicalDescendants().OfType<Button>(),
				static button => Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(button))));
		}
		finally
		{
			dialog.Close();
			owner.Close();
		}
	}

	[AvaloniaFact]
	public void ManualConfigurationDialog_LabelsManualLaunchPayloadAsCommand()
	{
		var localization = CreateLocalization();
		var result = new McpConnectionResult(
			McpConnectionStatus.ProcessFailed,
			"The server is connected, but the client could not be opened.",
			ManualConfiguration: "cursor \"C:/Projects/My project\"");
		var content = McpManualConfigurationDialog.CreateContent(
			localization,
			result,
			"fallback",
			McpManualPayloadPresentation.Command);
		var owner = new Window();
		var dialog = McpManualConfigurationDialog.CreateDialogWindow(owner, content);

		try
		{
			Assert.Equal("Dialog.McpManual.Command", content.PayloadLabel);
			var textBox = Assert.Single(
				dialog.GetLogicalDescendants().OfType<TextBox>(),
				static control => control.Name == "McpManualConfigurationText");
			Assert.Equal(result.ManualConfiguration, textBox.Text);
			Assert.Equal(content.PayloadLabel, AutomationProperties.GetName(textBox));
		}
		finally
		{
			dialog.Close();
			owner.Close();
		}
	}

	[Fact]
	public void LocalizationFiles_ContainEveryMcpMenuAndPathPromptLabel()
	{
		foreach (var file in Directory.GetFiles(GetLocalizationDirectory(), "*.json"))
		{
			using var document = JsonDocument.Parse(File.ReadAllText(file));
			foreach (var key in RequiredLocalizationKeys)
			{
				Assert.True(
					document.RootElement.TryGetProperty(key, out var value),
					$"Missing {key} in {Path.GetFileName(file)}.");
				Assert.False(
					string.IsNullOrWhiteSpace(value.GetString()),
					$"{key} is empty in {Path.GetFileName(file)}.");
			}
		}
	}

	[Fact]
	public void SecretProtectionMessages_StateThatMcpResponsesRemainRedacted()
	{
		foreach (var file in Directory.GetFiles(GetLocalizationDirectory(), "*.json"))
		{
			using var document = JsonDocument.Parse(File.ReadAllText(file));
			var message = document.RootElement
				.GetProperty("Dialog.LiveContext.Secrets.Message")
				.GetString();
			Assert.Contains("MCP", message, StringComparison.Ordinal);
		}
	}

	[Theory]
	[InlineData(true, false, true, true)]
	[InlineData(true, false, false, false)]
	[InlineData(true, true, true, false)]
	[InlineData(false, false, true, false)]
	[InlineData(false, true, true, false)]
	[InlineData(true, true, false, false)]
	public void SecretProtectionConfirmation_OnlyGuardsLiveEnabledToDisabledTransition(
		bool wasEnabled,
		bool willBeEnabled,
		bool hasLiveSession,
		bool expected)
	{
		Assert.Equal(
			expected,
			MainWindow.ShouldConfirmSecretProtectionDisable(
				wasEnabled,
				willBeEnabled,
				hasLiveSession));
	}

	private static LocalizationService CreateLocalization()
	{
		var values = RequiredLocalizationKeys
			.Concat(
			[
				"Dialog.TerminalCommand.CopyPathCommand",
				"Dialog.TerminalCommand.CopyCommand",
				"Dialog.TerminalCommand.AddToPath",
				"Dialog.TerminalCommand.Setup",
				"Dialog.TerminalCommand.NotNow",
				"Dialog.OK"
			])
			.ToDictionary(static key => key, static key => key, StringComparer.Ordinal);
		return new LocalizationService(
			new StubLocalizationCatalog(new Dictionary<AppLanguage, IReadOnlyDictionary<string, string>>
			{
				[AppLanguage.En] = values
			}),
			AppLanguage.En);
	}

	private static TerminalCommandSetupSnapshot Snapshot(TerminalCommandSetupState state) =>
		new(
			"devprojex",
			state,
			CommandPath: null,
			TargetExecutablePath: "/opt/DevProjex",
			InstalledTargetExecutablePath: null,
			UserBinDirectory: "/home/me/.local/bin",
			UserBinDirectoryIsInPath: false,
			CanInstall: state == TerminalCommandSetupState.NotInstalled,
			CanRepair: state == TerminalCommandSetupState.Stale,
			ShellProfileHint: "Add ~/.local/bin to PATH.",
			PathSetupCommand: "export PATH=\"$HOME/.local/bin:$PATH\"");

	private static string GetLocalizationDirectory()
	{
		var directory = AppContext.BaseDirectory;
		while (directory is not null)
		{
			var candidate = Path.Combine(directory, "Assets", "Localization");
			if (Directory.Exists(candidate))
				return candidate;
			directory = Directory.GetParent(directory)?.FullName;
		}

		throw new InvalidOperationException("Localization directory not found.");
	}
}
