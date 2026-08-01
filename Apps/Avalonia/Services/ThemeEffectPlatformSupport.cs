using ThemeEffectMode = DevProjex.Infrastructure.ThemePresets.ThemeEffectMode;

namespace DevProjex.Avalonia.Services;

internal static class ThemeEffectPlatformSupport
{
    private const int Windows11MinimumBuild = 22000;

    internal static bool IsMicaSupportedOnCurrentPlatform() =>
        IsMicaSupported(OperatingSystem.IsWindows(), Environment.OSVersion.Version);

    internal static bool IsMicaSupported(bool isWindows, Version osVersion) =>
        isWindows && osVersion.Major >= 10 && osVersion.Build >= Windows11MinimumBuild;

    internal static ThemeEffectMode Normalize(ThemeEffectMode requested, bool isMicaSupported) =>
        requested == ThemeEffectMode.Mica && !isMicaSupported
            ? ThemeEffectMode.Acrylic
            : requested;

    internal static ThemeEffectMode ResolveActual(
        ThemeEffectMode requested,
        WindowTransparencyLevel actual) => requested switch
    {
        ThemeEffectMode.Mica when actual == WindowTransparencyLevel.Mica => ThemeEffectMode.Mica,
        ThemeEffectMode.Mica when IsBlurLevel(actual) => ThemeEffectMode.Acrylic,
        ThemeEffectMode.Acrylic when actual == WindowTransparencyLevel.Mica => ThemeEffectMode.Mica,
        ThemeEffectMode.Acrylic when IsBlurLevel(actual) => ThemeEffectMode.Acrylic,
        ThemeEffectMode.Transparent when actual == WindowTransparencyLevel.Transparent => ThemeEffectMode.Transparent,
        _ => ThemeEffectMode.Solid
    };

    internal static bool IsBlurLevel(WindowTransparencyLevel level) =>
        level == WindowTransparencyLevel.AcrylicBlur || level == WindowTransparencyLevel.Blur;
}
