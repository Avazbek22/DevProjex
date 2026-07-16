using DevProjex.Infrastructure.ThemePresets;

namespace DevProjex.Avalonia.Coordinators;

internal readonly record struct ThemePalette(
    Color Background,
    Color Panel,
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
        double materialIntensity,
        double panelContrast,
        double menuChildIntensity,
        double borderStrength)
    {
        var material = Math.Clamp(materialIntensity / 100.0, 0.0, 1.0);
        var contrast = Math.Clamp(panelContrast / 100.0, 0.0, 1.0);
        var normalizedBorderStrength = Math.Clamp(borderStrength / 100.0, 0.0, 1.0);
        var menuChild = Math.Clamp(menuChildIntensity / 100.0, 0.0, 1.0);
        var hasEffect = effect != ThemeEffectMode.Solid;

        var backgroundBase = isDark ? DarkBackground : LightBackground;
        var panelBase = isDark ? DarkPanel : LightPanel;
        var menuBase = panelBase;
        var menuChildBase = panelBase;
        byte backgroundAlpha;
        byte panelAlpha;
        byte menuAlpha;
        byte menuChildAlpha;

        switch (effect)
        {
            case ThemeEffectMode.Solid:
                backgroundAlpha = 255;
                panelAlpha = 255;
                menuAlpha = 255;
                menuChildAlpha = 255;
                break;
            case ThemeEffectMode.Mica:
                // Mica is the native window base. Only content surfaces are tinted here.
                backgroundAlpha = 0;
                backgroundBase = Colors.Transparent;
                panelAlpha = (byte)Math.Round(isDark
                    ? 112 + (contrast * 88)
                    : 150 + (contrast * 74));
                menuAlpha = (byte)Math.Clamp(panelAlpha + (isDark ? 28 : 12), 96, 255);
                menuChildAlpha = (byte)Math.Clamp(menuAlpha - (12 + (menuChild * 72)), 72, 255);
                break;
            case ThemeEffectMode.Acrylic:
            case ThemeEffectMode.Transparent:
            default:
                backgroundAlpha = (byte)Math.Round(255 * (1.0 - material));
                var panelBaseAlpha = 90 + (contrast * 130);
                panelAlpha = (byte)Math.Clamp(panelBaseAlpha, 70, 255);
                menuAlpha = (byte)Math.Clamp(panelAlpha + 45, 170, 255);
                menuChildAlpha = (byte)Math.Clamp(menuAlpha - (menuChild * 40), 150, 255);
                break;
        }

        if (hasEffect && effect != ThemeEffectMode.Mica)
        {
            // Keep the window surface denser than content islands for readable material layers.
            backgroundAlpha = (byte)Math.Clamp(backgroundAlpha + 22, 90, 255);
            const int minAlphaGap = 12;
            var maxPanelAlpha = Math.Max(60, backgroundAlpha - minAlphaGap);
            panelAlpha = (byte)Math.Clamp(panelAlpha, 60, maxPanelAlpha);

            if (isDark)
            {
                menuAlpha = (byte)Math.Clamp(panelAlpha + 28 + (contrast * 16), 120, 255);
                var submenuDelta = 10 + (menuChild * 80);
                menuChildAlpha = (byte)Math.Clamp(menuAlpha - submenuDelta, 45, 255);
            }
            else
            {
                menuAlpha = (byte)Math.Clamp(panelAlpha + 12 + (contrast * 8), 96, 215);
                var submenuDelta = 12 + (menuChild * 72);
                menuChildAlpha = (byte)Math.Clamp(menuAlpha - submenuDelta, 72, 205);
                // A subtle cool tint keeps light popup materials visible over bright content.
                menuBase = LightMenu;
                menuChildBase = LightMenuChild;
            }
        }
        else if (effect == ThemeEffectMode.Mica && !isDark)
        {
            menuBase = LightMenu;
            menuChildBase = LightMenuChild;
        }

        var borderAlpha = (byte)Math.Round(255 * normalizedBorderStrength);
        var borderBase = isDark ? DarkBorder : LightBorder;

        return new ThemePalette(
            Color.FromArgb(backgroundAlpha, backgroundBase.R, backgroundBase.G, backgroundBase.B),
            Color.FromArgb(panelAlpha, panelBase.R, panelBase.G, panelBase.B),
            Color.FromArgb(menuAlpha, menuBase.R, menuBase.G, menuBase.B),
            Color.FromArgb(menuChildAlpha, menuChildBase.R, menuChildBase.G, menuChildBase.B),
            isDark ? DarkMenuHover : LightMenuHover,
            isDark ? DarkMenuPressed : LightMenuPressed,
            Color.FromArgb(borderAlpha, borderBase.R, borderBase.G, borderBase.B),
            isDark ? DarkAccent : LightAccent,
            isDark ? DarkBackground : LightBackground);
    }
}
