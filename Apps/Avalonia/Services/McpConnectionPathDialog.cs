using Avalonia.Automation;
using DevProjex.Infrastructure.TerminalCommands;

namespace DevProjex.Avalonia.Services;

internal enum McpConnectionPathAction
{
    None,
    InstallOrRepair,
    ConfigurePath
}

internal sealed record McpConnectionPathDialogContent(
    string Title,
    string Body,
    string Command,
    string CopyButton,
    string PrimaryButton,
    string CloseButton,
    McpConnectionPathAction Action);

internal static class McpConnectionPathPromptPolicy
{
    public static bool ShouldShow(
        TerminalCommandSetupState state,
        TerminalCommandHostPlatform platform) =>
        platform is (TerminalCommandHostPlatform.Windows or
            TerminalCommandHostPlatform.Linux or
            TerminalCommandHostPlatform.MacOS) &&
        state is (TerminalCommandSetupState.NotInstalled or
            TerminalCommandSetupState.InstalledPathMissing or
            TerminalCommandSetupState.Stale);

    public static McpConnectionPathDialogContent Create(
        LocalizationService localization,
        TerminalCommandSetupSnapshot snapshot,
        TerminalCommandHostPlatform platform)
    {
        ArgumentNullException.ThrowIfNull(localization);
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!ShouldShow(snapshot.State, platform))
            throw new ArgumentException("This terminal command state does not require the MCP PATH prompt.", nameof(snapshot));

        var action = platform == TerminalCommandHostPlatform.Windows
            ? snapshot.State == TerminalCommandSetupState.InstalledPathMissing
                ? McpConnectionPathAction.ConfigurePath
                : McpConnectionPathAction.InstallOrRepair
            : McpConnectionPathAction.None;
        return new McpConnectionPathDialogContent(
            localization["Dialog.McpPath.Title"],
            localization["Dialog.McpPath.Body"],
            platform is TerminalCommandHostPlatform.Linux or TerminalCommandHostPlatform.MacOS
                ? snapshot.PathSetupCommand ?? string.Empty
                : string.Empty,
            localization["Dialog.TerminalCommand.CopyPathCommand"],
            action == McpConnectionPathAction.ConfigurePath
                ? localization["Dialog.TerminalCommand.AddToPath"]
                : localization["Dialog.TerminalCommand.Setup"],
            localization["Dialog.TerminalCommand.NotNow"],
            action);
    }
}

internal static class McpConnectionPathDialog
{
    public static async Task<McpConnectionPathAction> ShowAsync(
        Window owner,
        McpConnectionPathDialogContent content)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(content);
        var completion = new TaskCompletionSource<McpConnectionPathAction>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var theme = DialogSurfaceFactory.ResolveThemeVariant(owner);
        var brushes = DialogSurfaceFactory.ResolveBrushes(owner, theme);
        var dialog = DialogSurfaceFactory.CreateWindow(
            content.Title,
            theme,
            brushes,
            BuildContent(owner, content, completion),
            width: 540,
            height: string.IsNullOrWhiteSpace(content.Command) ? 190 : 250,
            minWidth: 440,
            minHeight: 180);
        dialog.Closed += (_, _) => completion.TrySetResult(McpConnectionPathAction.None);
        _ = dialog.ShowDialog(owner);
        return await completion.Task.ConfigureAwait(false);
    }

    private static Control BuildContent(
        Window owner,
        McpConnectionPathDialogContent content,
        TaskCompletionSource<McpConnectionPathAction> completion)
    {
        var stack = new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 12
        };
        var body = new TextBlock
        {
            Text = content.Body,
            TextWrapping = TextWrapping.Wrap
        };
        AutomationProperties.SetName(body, content.Body);
        stack.Children.Add(body);

        if (!string.IsNullOrWhiteSpace(content.Command))
        {
            var command = new SelectableTextBlock
            {
                Text = content.Command,
                TextWrapping = TextWrapping.Wrap,
                FontFamily = FontFamily.Parse("Consolas,Menlo,Monospace"),
                FontSize = 13
            };
            AutomationProperties.SetName(command, content.Command);
            stack.Children.Add(command);
        }

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8
        };
        if (!string.IsNullOrWhiteSpace(content.Command))
        {
            var copy = CreateButton(content.CopyButton);
            copy.Click += async (_, _) =>
            {
                try
                {
                    await owner.Clipboard!.SetTextAsync(content.Command);
                }
                catch
                {
                    // Clipboard access can be unavailable in headless or restricted sessions.
                }
            };
            buttons.Children.Add(copy);
        }

        if (content.Action != McpConnectionPathAction.None)
        {
            var primary = CreateButton(content.PrimaryButton);
            primary.Classes.Add("primary-action");
            primary.Click += (_, _) =>
            {
                completion.TrySetResult(content.Action);
                (TopLevel.GetTopLevel(buttons) as Window)?.Close();
            };
            buttons.Children.Add(primary);
        }

        var close = CreateButton(content.CloseButton);
        close.Click += (_, _) =>
        {
            completion.TrySetResult(McpConnectionPathAction.None);
            (TopLevel.GetTopLevel(buttons) as Window)?.Close();
        };
        buttons.Children.Add(close);
        stack.Children.Add(buttons);
        return stack;
    }

    private static Button CreateButton(string label)
    {
        var button = new Button
        {
            Content = label,
            MinWidth = 112,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetName(button, label);
        return button;
    }
}
