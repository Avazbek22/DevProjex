using Avalonia.Automation;

namespace DevProjex.Avalonia.Services;

internal sealed record McpManualConfigurationDialogContent(
    string Title,
    string Reason,
    string ConfigurationLabel,
    string Configuration,
    string PathsLabel,
    IReadOnlyList<string> SuggestedConfigPaths,
    string CopyButton,
    string CloseButton);

internal static class McpManualConfigurationDialog
{
    public static McpManualConfigurationDialogContent CreateContent(
        LocalizationService localization,
        McpConnectionResult result,
        string fallbackConfiguration)
    {
        ArgumentNullException.ThrowIfNull(localization);
        ArgumentNullException.ThrowIfNull(result);

        var configuration = string.IsNullOrWhiteSpace(result.ManualConfiguration)
            ? fallbackConfiguration
            : result.ManualConfiguration;
        if (string.IsNullOrWhiteSpace(configuration))
            configuration = result.CommandOutput ?? string.Empty;

        return new McpManualConfigurationDialogContent(
            localization["Dialog.McpManual.Title"],
            NormalizeSingleLine(result.UserMessage),
            localization["Dialog.McpManual.Configuration"],
            configuration,
            localization["Dialog.McpManual.Paths"],
            result.SuggestedConfigPaths?
                .Where(static path => !string.IsNullOrWhiteSpace(path))
                .ToArray() ?? [],
            localization["Dialog.TerminalCommand.CopyCommand"],
            localization["Dialog.OK"]);
    }

    public static async Task ShowAsync(
        Window owner,
        McpManualConfigurationDialogContent content)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(content);
        var dialog = CreateDialogWindow(owner, content);
        await dialog.ShowDialog(owner);
    }

    internal static Window CreateDialogWindow(
        Window owner,
        McpManualConfigurationDialogContent content)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(content);
        var theme = DialogSurfaceFactory.ResolveThemeVariant(owner);
        var brushes = DialogSurfaceFactory.ResolveBrushes(owner, theme);
        return DialogSurfaceFactory.CreateWindow(
            content.Title,
            theme,
            brushes,
            BuildContent(owner, content),
            width: 660,
            height: 430,
            minWidth: 520,
            minHeight: 340);
    }

    private static Control BuildContent(
        Window owner,
        McpManualConfigurationDialogContent content)
    {
        var grid = new Grid
        {
            Margin = new Thickness(18),
            RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto")
        };

        var reason = new TextBlock
        {
            Name = "McpManualConfigurationReason",
            Text = content.Reason,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        };
        AutomationProperties.SetName(reason, content.Reason);
        grid.Children.Add(reason);

        var details = new StackPanel
        {
            Spacing = 6,
            Margin = new Thickness(0, 0, 0, 8)
        };
        Grid.SetRow(details, 1);
        if (content.SuggestedConfigPaths.Count > 0)
        {
            var pathsLabel = new TextBlock
            {
                Text = content.PathsLabel,
                FontWeight = FontWeight.SemiBold
            };
            AutomationProperties.SetName(pathsLabel, content.PathsLabel);
            details.Children.Add(pathsLabel);

            var paths = new SelectableTextBlock
            {
                Name = "McpManualConfigurationPaths",
                Text = string.Join(Environment.NewLine, content.SuggestedConfigPaths),
                TextWrapping = TextWrapping.Wrap,
                FontFamily = FontFamily.Parse("Consolas,Menlo,Monospace"),
                FontSize = 13
            };
            AutomationProperties.SetName(paths, content.PathsLabel);
            details.Children.Add(paths);
        }

        var configurationLabel = new TextBlock
        {
            Text = content.ConfigurationLabel,
            FontWeight = FontWeight.SemiBold
        };
        AutomationProperties.SetName(configurationLabel, content.ConfigurationLabel);
        details.Children.Add(configurationLabel);
        grid.Children.Add(details);

        var configuration = new TextBox
        {
            Name = "McpManualConfigurationText",
            Text = content.Configuration,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = FontFamily.Parse("Consolas,Menlo,Monospace"),
            FontSize = 13
        };
        configuration.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
        configuration.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
        AutomationProperties.SetName(configuration, content.ConfigurationLabel);
        Grid.SetRow(configuration, 2);
        grid.Children.Add(configuration);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 12, 0, 0)
        };
        Grid.SetRow(buttons, 3);

        var copy = CreateButton(content.CopyButton);
        copy.IsEnabled = !string.IsNullOrWhiteSpace(content.Configuration);
        copy.Click += async (_, _) =>
        {
            try
            {
                await owner.Clipboard!.SetTextAsync(content.Configuration);
            }
            catch
            {
                // Clipboard access can be unavailable in headless or restricted sessions.
            }
        };
        buttons.Children.Add(copy);

        var close = CreateButton(content.CloseButton);
        close.Classes.Add("primary-action");
        close.Click += (_, _) =>
            (TopLevel.GetTopLevel(buttons) as Window)?.Close();
        buttons.Children.Add(close);
        grid.Children.Add(buttons);
        return grid;
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

    private static string NormalizeSingleLine(string value) =>
        value.Replace('\r', ' ').Replace('\n', ' ').Trim();
}
