using AvaloniaPlatformTheme = Avalonia.Platform.PlatformThemeVariant;
using AvaloniaTheme = Avalonia.Styling.ThemeVariant;
using ThemePresetVariant = DevProjex.Infrastructure.ThemePresets.ThemeVariant;

namespace DevProjex.Avalonia.Services;

internal static class SystemThemeResolver
{
    internal static ThemePresetVariant? Resolve(
        AvaloniaPlatformTheme? platformTheme,
        AvaloniaTheme? applicationTheme,
        AvaloniaTheme? requestedApplicationTheme)
    {
        if (platformTheme == AvaloniaPlatformTheme.Light)
            return ThemePresetVariant.Light;

        if (platformTheme == AvaloniaPlatformTheme.Dark)
        {
            return ThemePresetVariant.Dark;
        }

        // ActualThemeVariant is a trustworthy system signal only while the application
        // inherits the platform theme. In an explicit mode it merely reflects our override.
        if (requestedApplicationTheme != AvaloniaTheme.Default)
            return null;

        return applicationTheme switch
        {
            var theme when theme == AvaloniaTheme.Light => ThemePresetVariant.Light,
            var theme when theme == AvaloniaTheme.Dark => ThemePresetVariant.Dark,
            _ => null
        };
    }
}
