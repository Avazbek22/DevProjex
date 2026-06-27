using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using DevProjex.Avalonia.Services;

namespace DevProjex.Tests.Unit.Avalonia;

[Collection("AvaloniaUI")]
public sealed class DialogSurfaceFactoryTests
{
    [AvaloniaFact]
    public void CreateWindow_UsesTransparentBlurSurfaceWithRoundedCard()
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
            Assert.Equal(WindowDecorations.None, window.WindowDecorations);
            Assert.False(window.ShowInTaskbar);
            Assert.Same(Brushes.Transparent, window.Background);
            Assert.Same(background, window.TransparencyBackgroundFallback);
            Assert.Equal(
                [WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Blur, WindowTransparencyLevel.None],
                window.TransparencyLevelHint);

            var card = Assert.IsType<Border>(window.Content);
            Assert.Same(content, card.Child);
            Assert.Same(panel, card.Background);
            Assert.Same(border, card.BorderBrush);
            Assert.Equal(new Thickness(1), card.BorderThickness);
            Assert.Equal(new CornerRadius(12), card.CornerRadius);
            Assert.True(card.ClipToBounds);
            Assert.True(card.BoxShadow.Count > 0);
            Assert.Equal(360, window.MinWidth);
            Assert.Equal(180, window.MinHeight);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void ResolveBrushes_UsesSolidFallbackAndMenuSurfaceBrush()
    {
        var app = global::Avalonia.Application.Current;
        Assert.NotNull(app);

        var resources = app!.Resources;
        var originalBackgroundBrush = CaptureResource(resources, "AppBackgroundBrush", out var hadBackgroundBrush);
        var originalBackgroundColor = CaptureResource(resources, "AppBackgroundColor", out var hadBackgroundColor);
        var originalPanelBrush = CaptureResource(resources, "AppPanelBrush", out var hadPanelBrush);
        var originalMenuBrush = CaptureResource(resources, "MenuPopupBrush", out var hadMenuBrush);

        var menuBrush = new SolidColorBrush(Colors.DarkSlateGray);

        try
        {
            resources["AppBackgroundBrush"] = Brushes.Transparent;
            resources["AppBackgroundColor"] = Colors.White;
            resources["AppPanelBrush"] = new SolidColorBrush(Colors.Red);
            resources["MenuPopupBrush"] = menuBrush;

            var brushes = DialogSurfaceFactory.ResolveBrushes(null, ThemeVariant.Default);
            var background = Assert.IsType<SolidColorBrush>(brushes.Background);

            Assert.Equal(Colors.White, background.Color);
            Assert.Same(menuBrush, brushes.Panel);
        }
        finally
        {
            RestoreResource(resources, "AppBackgroundBrush", originalBackgroundBrush, hadBackgroundBrush);
            RestoreResource(resources, "AppBackgroundColor", originalBackgroundColor, hadBackgroundColor);
            RestoreResource(resources, "AppPanelBrush", originalPanelBrush, hadPanelBrush);
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
