using DevProjex.Application.Services;
using DevProjex.Avalonia.Services;
using DevProjex.Avalonia.Views;
using DevProjex.Infrastructure.TerminalCommands;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace DevProjex.Tests.Unit.Avalonia;

public sealed class McpConnectionUiTests
{
	private static readonly string[] RequiredLocalizationKeys =
	[
		"Menu.Mcp",
		"Menu.Mcp.LiveContext",
		"Menu.Mcp.Standard",
		"Menu.Mcp.Documentation",
		"Menu.Mcp.ClaudeCode",
		"Menu.Mcp.Codex",
		"Menu.Mcp.Json",
		"Dialog.McpPath.Title",
		"Dialog.McpPath.Body",
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

		var live = Assert.IsType<MenuItem>(view.FindControl<MenuItem>("McpLiveMenuItem"));
		var standard = Assert.IsType<MenuItem>(view.FindControl<MenuItem>("McpStandardMenuItem"));
		Assert.False(live.IsEnabled);
		Assert.False(standard.IsEnabled);
		viewModel.IsProjectLoaded = true;
		Assert.True(live.IsEnabled);
		Assert.True(standard.IsEnabled);

		McpConnectionRequestedEventArgs? requested = null;
		view.McpConnectionRequested += (_, args) => requested = args;
		var item = Assert.IsType<MenuItem>(view.FindControl<MenuItem>("McpLiveCodexMenuItem"));
		item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
		Assert.NotNull(requested);
		Assert.Equal(McpConnectionClient.Codex, requested.Client);
		Assert.Equal(McpConnectionMode.Live, requested.Mode);
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
				"Dialog.TerminalCommand.AddToPath",
				"Dialog.TerminalCommand.Setup",
				"Dialog.TerminalCommand.NotNow"
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
