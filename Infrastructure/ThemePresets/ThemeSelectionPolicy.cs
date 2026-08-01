namespace DevProjex.Infrastructure.ThemePresets;

public static class ThemeSelectionPolicy
{
    public static ThemeVariant ResolveEffectiveTheme(
        ThemeSelectionMode selectionMode,
        ThemeVariant? systemTheme) => selectionMode switch
    {
        ThemeSelectionMode.Light => ThemeVariant.Light,
        ThemeSelectionMode.Dark => ThemeVariant.Dark,
        ThemeSelectionMode.System => systemTheme ?? ThemeVariant.Dark,
        _ => ThemeVariant.Dark
    };

    public static ThemeEffectMode GetFactoryEffect(ThemeVariant theme) => theme switch
    {
        ThemeVariant.Light => ThemeEffectMode.Solid,
        ThemeVariant.Dark => ThemeEffectMode.Acrylic,
        _ => ThemeEffectMode.Acrylic
    };

    public static ThemeSelectionMode GetExplicitMode(ThemeVariant theme) => theme switch
    {
        ThemeVariant.Light => ThemeSelectionMode.Light,
        ThemeVariant.Dark => ThemeSelectionMode.Dark,
        _ => ThemeSelectionMode.Dark
    };
}
