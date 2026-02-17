using Avalonia.Layout;
using Avalonia.Styling;
using Avalonia.VisualTree;

namespace DevProjex.Avalonia.Services;

public static class MessageDialog
{
    public static async Task ShowAsync(Window owner, string title, string message)
    {
        var themeVariant = owner?.ActualThemeVariant
            ?? global::Avalonia.Application.Current?.ActualThemeVariant
            ?? ThemeVariant.Default;
        var app = global::Avalonia.Application.Current;
        var appBackground = TryGetThemeBrush(app, themeVariant, "AppBackgroundBrush");
        var appPanel = TryGetThemeBrush(app, themeVariant, "AppPanelBrush");
        var appBorder = TryGetThemeBrush(app, themeVariant, "AppBorderBrush");

        var baseBackground = TryGetThemeColorBrush(app, themeVariant, "AppBackgroundColor") ?? appBackground;
        var basePanel = TryGetThemeColorBrush(app, themeVariant, "AppPanelColor") ?? appPanel;
        var baseBorder = TryGetThemeColorBrush(app, themeVariant, "AppBorderColor") ?? appBorder;

        var dialog = new Window
        {
            Title = title,
            Width = 420,
            Height = 200,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            RequestedThemeVariant = themeVariant,
            TransparencyLevelHint = new[] { WindowTransparencyLevel.None },
            Background = baseBackground,
            Content = BuildContent(message)
        };

        if (baseBackground is not null)
            dialog.Resources["AppBackgroundBrush"] = baseBackground;
        if (basePanel is not null)
            dialog.Resources["AppPanelBrush"] = basePanel;
        if (baseBorder is not null)
            dialog.Resources["AppBorderBrush"] = baseBorder;

        if (owner is not null)
            await dialog.ShowDialog(owner);
        else
            dialog.Show();
    }

    private static Control BuildContent(string message)
    {
        var text = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(12),
            VerticalAlignment = VerticalAlignment.Center
        };

        var button = new Button
        {
            Content = "OK",
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(12),
            Width = 80
        };

        var panel = new DockPanel();
        DockPanel.SetDock(button, Dock.Bottom);

        panel.Children.Add(button);
        panel.Children.Add(text);

        button.Click += (_, _) =>
            (panel.GetVisualRoot() as Window)?.Close();

        return panel;
    }

    private static IBrush? TryGetThemeBrush(global::Avalonia.Application? app, ThemeVariant themeVariant, string key)
    {
        return app?.TryFindResource(key, themeVariant, out var resource) == true
            ? resource as IBrush
            : null;
    }

    private static IBrush? TryGetThemeColorBrush(global::Avalonia.Application? app, ThemeVariant themeVariant, string key)
    {
        if (app?.TryFindResource(key, themeVariant, out var resource) == true && resource is Color color)
            return new SolidColorBrush(color);
        return null;
    }
}
