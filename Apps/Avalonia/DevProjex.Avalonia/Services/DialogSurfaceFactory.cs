namespace DevProjex.Avalonia.Services;

internal sealed record DialogSurfaceBrushes(
    IBrush? Background,
    IBrush? Panel,
    IBrush? Border);

internal static class DialogSurfaceFactory
{
    private static readonly WindowTransparencyLevel[] DialogTransparencyHints =
    [
        WindowTransparencyLevel.AcrylicBlur,
        WindowTransparencyLevel.Blur,
        WindowTransparencyLevel.None
    ];

    private static readonly CornerRadius CardCornerRadius = new(12);
    private static readonly BoxShadows CardShadow = BoxShadows.Parse("0 6 20 0 #50000000");

    public static ThemeVariant ResolveThemeVariant(Window? owner)
    {
        return owner?.ActualThemeVariant
               ?? global::Avalonia.Application.Current?.ActualThemeVariant
               ?? ThemeVariant.Default;
    }

    public static DialogSurfaceBrushes ResolveBrushes(Window? owner, ThemeVariant themeVariant)
    {
        var app = global::Avalonia.Application.Current;
        var appBackground = TryGetThemeBrush(app, themeVariant, "AppBackgroundBrush");
        var appPanel = TryGetThemeBrush(app, themeVariant, "AppPanelBrush");
        var menuPanel = TryGetThemeBrush(app, themeVariant, "MenuPopupBrush");
        var appBorder = TryGetThemeBrush(app, themeVariant, "AppBorderBrush");

        return new DialogSurfaceBrushes(
            TryGetThemeColorBrush(app, themeVariant, "AppBackgroundColor") ??
            appBackground ??
            owner?.Background ??
            CreateDefaultFallbackBrush(themeVariant),
            menuPanel ?? appPanel,
            TryGetThemeColorBrush(app, themeVariant, "AppBorderColor") ?? appBorder);
    }

    public static Window CreateWindow(
        string title,
        ThemeVariant themeVariant,
        DialogSurfaceBrushes brushes,
        Control content,
        double width,
        double height,
        double? minWidth = null,
        double? minHeight = null)
    {
        var dialog = new Window
        {
            Title = title,
            Width = width,
            Height = height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            ShowInTaskbar = false,
            RequestedThemeVariant = themeVariant,
            WindowDecorations = WindowDecorations.None,
            TransparencyLevelHint = DialogTransparencyHints,
            TransparencyBackgroundFallback = brushes.Background ?? Brushes.Transparent,
            Background = Brushes.Transparent,
            Content = CreateCard(content, brushes)
        };

        if (minWidth is not null)
            dialog.MinWidth = minWidth.Value;
        if (minHeight is not null)
            dialog.MinHeight = minHeight.Value;

        ApplyResources(dialog, brushes);
        return dialog;
    }

    private static Border CreateCard(Control content, DialogSurfaceBrushes brushes)
    {
        return new Border
        {
            Background = brushes.Panel ?? brushes.Background,
            BorderBrush = brushes.Border,
            BorderThickness = new Thickness(1),
            CornerRadius = CardCornerRadius,
            ClipToBounds = true,
            BoxShadow = CardShadow,
            Child = content
        };
    }

    private static void ApplyResources(Window dialog, DialogSurfaceBrushes brushes)
    {
        if (brushes.Background is not null)
            dialog.Resources["AppBackgroundBrush"] = brushes.Background;
        if (brushes.Panel is not null)
        {
            dialog.Resources["AppPanelBrush"] = brushes.Panel;
            dialog.Resources["MenuPopupBrush"] = brushes.Panel;
        }
        if (brushes.Border is not null)
            dialog.Resources["AppBorderBrush"] = brushes.Border;
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

    private static IBrush CreateDefaultFallbackBrush(ThemeVariant themeVariant)
    {
        return new SolidColorBrush(themeVariant == ThemeVariant.Light ? Colors.White : Color.Parse("#121214"));
    }
}
