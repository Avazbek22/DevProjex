using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using DevProjex.Avalonia.Services;

namespace DevProjex.Tests.Unit.Avalonia;

[Collection("AvaloniaUI")]
public sealed class DialogSurfaceFactoryTests
{
    [AvaloniaFact]
    public void CreateWindow_UsesClassicWindowSurfaceWithoutEffects()
    {
        var content = new TextBlock { Text = "Dialog body" };
        var background = new SolidColorBrush(Colors.White);
        var panel = new SolidColorBrush(Colors.Black);
        var border = new SolidColorBrush(Colors.Gray);
        var brushes = new DialogSurfaceBrushes(background, panel, border);

        var window = DialogSurfaceFactory.CreateWindow(
            "Dialog",
            ThemeVariant.Default,
            brushes,
            content,
            width: 420,
            height: 200,
            minWidth: 360,
            minHeight: 180);

        try
        {
            Assert.Equal(WindowDecorations.Full, window.WindowDecorations);
            Assert.Same(background, window.Background);
            Assert.Equal(
                [WindowTransparencyLevel.None],
                window.TransparencyLevelHint);
            Assert.Same(content, window.Content);
            Assert.Equal(360, window.MinWidth);
            Assert.Equal(180, window.MinHeight);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void ResolveBrushes_UsesSolidFallbackAndOpaquePanelColor()
    {
        var app = global::Avalonia.Application.Current;
        Assert.NotNull(app);

        var resources = app!.Resources;
        var originalBackgroundBrush = CaptureResource(resources, "AppBackgroundBrush", out var hadBackgroundBrush);
        var originalBackgroundColor = CaptureResource(resources, "AppBackgroundColor", out var hadBackgroundColor);
        var originalPanelBrush = CaptureResource(resources, "AppPanelBrush", out var hadPanelBrush);
        var originalPanelColor = CaptureResource(resources, "AppPanelColor", out var hadPanelColor);
        var originalMenuBrush = CaptureResource(resources, "MenuPopupBrush", out var hadMenuBrush);

        var menuBrush = new SolidColorBrush(Colors.DarkSlateGray);

        try
        {
            resources["AppBackgroundBrush"] = Brushes.Transparent;
            resources["AppBackgroundColor"] = Colors.White;
            resources["AppPanelBrush"] = new SolidColorBrush(Colors.Red);
            resources["AppPanelColor"] = Colors.Black;
            resources["MenuPopupBrush"] = menuBrush;

            var brushes = DialogSurfaceFactory.ResolveBrushes(null, ThemeVariant.Default);
            var background = Assert.IsType<SolidColorBrush>(brushes.Background);
            var panel = Assert.IsType<SolidColorBrush>(brushes.Panel);

            Assert.Equal(Colors.White, background.Color);
            Assert.Equal(Colors.Black, panel.Color);
        }
        finally
        {
            RestoreResource(resources, "AppBackgroundBrush", originalBackgroundBrush, hadBackgroundBrush);
            RestoreResource(resources, "AppBackgroundColor", originalBackgroundColor, hadBackgroundColor);
            RestoreResource(resources, "AppPanelBrush", originalPanelBrush, hadPanelBrush);
            RestoreResource(resources, "AppPanelColor", originalPanelColor, hadPanelColor);
            RestoreResource(resources, "MenuPopupBrush", originalMenuBrush, hadMenuBrush);
        }
    }

    private static object? CaptureResource(IResourceDictionary resources, string key, out bool exists)
    {
        exists = resources.ContainsKey(key);
        return exists ? resources[key] : null;
    }

    private static void RestoreResource(IResourceDictionary resources, string key, object? value, bool exists)
    {
        if (exists)
            resources[key] = value;
        else
            resources.Remove(key);
    }
}
