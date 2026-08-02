using DevProjex.Avalonia.Services;
using AvaloniaPlatformTheme = Avalonia.Platform.PlatformThemeVariant;
using AvaloniaTheme = Avalonia.Styling.ThemeVariant;
using ThemePresetVariant = DevProjex.Infrastructure.ThemePresets.ThemeVariant;

namespace DevProjex.Tests.Unit.Avalonia;

public sealed class SystemThemeResolverTests
{
    public static TheoryData<
        AvaloniaPlatformTheme?,
        AvaloniaTheme?,
        AvaloniaTheme?,
        ThemePresetVariant?> ResolutionCases => new()
    {
        { AvaloniaPlatformTheme.Light, AvaloniaTheme.Dark, AvaloniaTheme.Default, ThemePresetVariant.Light },
        { AvaloniaPlatformTheme.Dark, AvaloniaTheme.Light, AvaloniaTheme.Default, ThemePresetVariant.Dark },
        { AvaloniaPlatformTheme.Light, AvaloniaTheme.Dark, AvaloniaTheme.Dark, ThemePresetVariant.Light },
        { AvaloniaPlatformTheme.Dark, AvaloniaTheme.Light, AvaloniaTheme.Light, ThemePresetVariant.Dark },
        { null, AvaloniaTheme.Light, AvaloniaTheme.Default, ThemePresetVariant.Light },
        { null, AvaloniaTheme.Dark, AvaloniaTheme.Default, ThemePresetVariant.Dark },
        { null, AvaloniaTheme.Default, AvaloniaTheme.Default, null },
        { null, null, AvaloniaTheme.Default, null },
        { null, AvaloniaTheme.Light, AvaloniaTheme.Dark, null },
        { null, AvaloniaTheme.Dark, AvaloniaTheme.Light, null },
        { null, AvaloniaTheme.Dark, null, null },
        { (AvaloniaPlatformTheme)999, AvaloniaTheme.Dark, AvaloniaTheme.Default, ThemePresetVariant.Dark },
        { (AvaloniaPlatformTheme)999, null, AvaloniaTheme.Default, null }
    };

    [Theory]
    [MemberData(nameof(ResolutionCases))]
    public void Resolve_UsesPlatformThenInheritedApplicationThemeThenUnknown(
        AvaloniaPlatformTheme? platformTheme,
        AvaloniaTheme? applicationTheme,
        AvaloniaTheme? requestedApplicationTheme,
        ThemePresetVariant? expected)
    {
        Assert.Equal(
            expected,
            SystemThemeResolver.Resolve(
                platformTheme,
                applicationTheme,
                requestedApplicationTheme));
    }
}
