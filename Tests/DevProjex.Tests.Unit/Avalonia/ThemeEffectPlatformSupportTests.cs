using DevProjex.Avalonia.Services;
using ThemeEffectMode = DevProjex.Infrastructure.ThemePresets.ThemeEffectMode;

namespace DevProjex.Tests.Unit.Avalonia;

public sealed class ThemeEffectPlatformSupportTests
{
    [Theory]
    [InlineData(false, 10, 0, 22631, false)]
    [InlineData(true, 10, 0, 19045, false)]
    [InlineData(true, 10, 0, 22000, true)]
    [InlineData(true, 10, 0, 26100, true)]
    public void IsMicaSupported_RequiresWindows11(
        bool isWindows,
        int major,
        int minor,
        int build,
        bool expected)
    {
        Assert.Equal(expected, ThemeEffectPlatformSupport.IsMicaSupported(
            isWindows,
            new Version(major, minor, build)));
    }

    [Theory]
    [InlineData(ThemeEffectMode.Mica, false, ThemeEffectMode.Acrylic)]
    [InlineData(ThemeEffectMode.Mica, true, ThemeEffectMode.Mica)]
    [InlineData(ThemeEffectMode.Acrylic, false, ThemeEffectMode.Acrylic)]
    [InlineData(ThemeEffectMode.Transparent, false, ThemeEffectMode.Transparent)]
    [InlineData(ThemeEffectMode.Solid, false, ThemeEffectMode.Solid)]
    public void Normalize_ChangesOnlyUnsupportedMica(
        ThemeEffectMode requested,
        bool isMicaSupported,
        ThemeEffectMode expected)
    {
        Assert.Equal(expected, ThemeEffectPlatformSupport.Normalize(requested, isMicaSupported));
    }
}
