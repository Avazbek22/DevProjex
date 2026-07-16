using DevProjex.Infrastructure.ThemePresets;

namespace DevProjex.Avalonia.Coordinators;

internal readonly record struct ThemePalette(
    Color Background,
    Color Panel,
    Color MainMenuStrip,
    Color MainMenuPopup,
    Color Menu,
    Color MenuChild,
    Color MenuHover,
    Color MenuPressed,
    Color Border,
    Color Accent,
    Color TransparencyFallback);

internal static class ThemePaletteCalculator
{
    private static readonly Color DarkBackground = Color.Parse("#121214");
    private static readonly Color LightBackground = Color.Parse("#FFFFFF");
    private static readonly Color DarkPanel = Color.Parse("#17171A");
    private static readonly Color LightPanel = Color.Parse("#F3F3F3");
    private static readonly Color LightMenu = Color.Parse("#F8FBFF");
    private static readonly Color LightMenuChild = Color.Parse("#F2F7FD");
    private static readonly Color DarkMenuHover = Color.Parse("#343B46");
    private static readonly Color LightMenuHover = Color.Parse("#DCE7F4");
    private static readonly Color DarkMenuPressed = Color.Parse("#3B4452");
    private static readonly Color LightMenuPressed = Color.Parse("#CFDDF0");
    private static readonly Color DarkBorder = Color.Parse("#505050");
    private static readonly Color LightBorder = Color.Parse("#C0C0C0");
    private static readonly Color DarkAccent = Color.Parse("#2D8CFF");
    private static readonly Color LightAccent = Color.Parse("#0078D4");

    public static ThemePalette Calculate(
        bool isDark,
        ThemeEffectMode effect,
        double backgroundTransparency,
        double panelContrast,
        double menuTransparency,
        double borderVisibility)
    {
        var material = Math.Clamp(backgroundTransparency / 100.0, 0.0, 1.0);
        var contrast = Math.Clamp(panelContrast / 100.0, 0.0, 1.0);
        var normalizedBorderVisibility = Math.Clamp(borderVisibility / 100.0, 0.0, 1.0);
        var normalizedMenuTransparency = Math.Clamp(menuTransparency / 100.0, 0.0, 1.0);
        var backgroundBase = isDark ? DarkBackground : LightBackground;
        var panelBase = isDark ? DarkPanel : LightPanel;
        var menuBase = panelBase;
        var menuChildBase = panelBase;
        byte backgroundAlpha;
        byte panelAlpha;
        byte mainMenuStripAlpha;
        byte mainMenuPopupAlpha;
        byte menuAlpha;
        byte menuChildAlpha;

        switch (effect)
        {
            case ThemeEffectMode.Solid:
                backgroundAlpha = 255;
                panelAlpha = 255;
                mainMenuStripAlpha = 255;
                mainMenuPopupAlpha = 255;
                menuAlpha = 255;
                menuChildAlpha = 255;
                break;
            case ThemeEffectMode.Mica:
                // Mica is the native window base. Only content surfaces are tinted here.
                backgroundAlpha = 0;
                backgroundBase = Colors.Transparent;
                panelAlpha = isDark ? (byte)112 : (byte)150;
                mainMenuStripAlpha = CalculateMainMenuStripAlpha(panelAlpha, contrast);
                menuAlpha = (byte)Math.Clamp(panelAlpha + (isDark ? 28 : 12), 96, 255);
                mainMenuPopupAlpha = CalculateMainMenuPopupAlpha(menuAlpha, normalizedMenuTransparency);
                menuChildAlpha = (byte)Math.Clamp(menuAlpha - 12, 72, 255);
                break;
            case ThemeEffectMode.Acrylic:
            case ThemeEffectMode.Transparent:
            default:
                backgroundAlpha = CalculateAlpha(byte.MaxValue, 90, material);
                const int minAlphaGap = 12;
                panelAlpha = (byte)Math.Max(60, backgroundAlpha - minAlphaGap);
                mainMenuStripAlpha = CalculateMainMenuStripAlpha(panelAlpha, contrast);

                if (isDark)
                {
                    menuAlpha = (byte)Math.Clamp(panelAlpha + 28, 120, 255);
                    menuChildAlpha = (byte)Math.Clamp(menuAlpha - 10, 45, 255);
                }
                else
                {
                    menuAlpha = (byte)Math.Clamp(panelAlpha + 12, 96, 215);
                    menuChildAlpha = (byte)Math.Clamp(menuAlpha - 12, 72, 205);
                }

                mainMenuPopupAlpha = CalculateMainMenuPopupAlpha(menuAlpha, normalizedMenuTransparency);
                break;
        }

        if (effect != ThemeEffectMode.Solid && !isDark)
        {
            // A subtle cool tint keeps light popup materials visible over bright content.
            menuBase = LightMenu;
            menuChildBase = LightMenuChild;
        }

        var borderAlpha = (byte)Math.Round(255 * normalizedBorderVisibility);
        var borderBase = isDark ? DarkBorder : LightBorder;

        return new ThemePalette(
            Color.FromArgb(backgroundAlpha, backgroundBase.R, backgroundBase.G, backgroundBase.B),
            Color.FromArgb(panelAlpha, panelBase.R, panelBase.G, panelBase.B),
            Color.FromArgb(mainMenuStripAlpha, panelBase.R, panelBase.G, panelBase.B),
            Color.FromArgb(mainMenuPopupAlpha, menuBase.R, menuBase.G, menuBase.B),
            Color.FromArgb(menuAlpha, menuBase.R, menuBase.G, menuBase.B),
            Color.FromArgb(menuChildAlpha, menuChildBase.R, menuChildBase.G, menuChildBase.B),
            isDark ? DarkMenuHover : LightMenuHover,
            isDark ? DarkMenuPressed : LightMenuPressed,
            Color.FromArgb(borderAlpha, borderBase.R, borderBase.G, borderBase.B),
            isDark ? DarkAccent : LightAccent,
            isDark ? DarkBackground : LightBackground);
    }

    private static byte CalculateMainMenuStripAlpha(byte baseSurfaceAlpha, double contrast)
    {
        return CalculateAlpha(baseSurfaceAlpha, byte.MaxValue, contrast);
    }

    private static byte CalculateMainMenuPopupAlpha(byte baseMenuAlpha, double transparency)
    {
        const byte minimumMenuAlpha = 72;
        return CalculateAlpha(baseMenuAlpha, minimumMenuAlpha, transparency);
    }

    private static byte CalculateAlpha(byte start, byte end, double progress)
        => (byte)Math.Round(start + ((end - start) * progress));
}
