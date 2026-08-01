using DevProjex.Infrastructure.ThemePresets;

namespace DevProjex.Tests.Unit;

public sealed class ThemeSelectionPolicyTests
{
    public static TheoryData<ThemeSelectionMode, ThemeVariant?, ThemeVariant> EffectiveThemeCases => new()
    {
        { ThemeSelectionMode.System, ThemeVariant.Light, ThemeVariant.Light },
        { ThemeSelectionMode.System, ThemeVariant.Dark, ThemeVariant.Dark },
        { ThemeSelectionMode.System, null, ThemeVariant.Dark },
        { ThemeSelectionMode.Light, ThemeVariant.Dark, ThemeVariant.Light },
        { ThemeSelectionMode.Light, null, ThemeVariant.Light },
        { ThemeSelectionMode.Dark, ThemeVariant.Light, ThemeVariant.Dark },
        { ThemeSelectionMode.Dark, null, ThemeVariant.Dark }
    };

    [Theory]
    [MemberData(nameof(EffectiveThemeCases))]
    public void ResolveEffectiveTheme_ImplementsSystemAndExplicitOverrides(
        ThemeSelectionMode mode,
        ThemeVariant? systemTheme,
        ThemeVariant expected)
    {
        Assert.Equal(expected, ThemeSelectionPolicy.ResolveEffectiveTheme(mode, systemTheme));
    }

    [Theory]
    [InlineData(ThemeVariant.Light, ThemeEffectMode.Solid)]
    [InlineData(ThemeVariant.Dark, ThemeEffectMode.Acrylic)]
    public void GetFactoryEffect_UsesCalibratedEffectForEachPalette(
        ThemeVariant theme,
        ThemeEffectMode expected)
    {
        Assert.Equal(expected, ThemeSelectionPolicy.GetFactoryEffect(theme));
    }

    [Fact]
    public void ResolveEffectiveTheme_InvalidMode_UsesDarkFallback()
    {
        Assert.Equal(
            ThemeVariant.Dark,
            ThemeSelectionPolicy.ResolveEffectiveTheme(
                (ThemeSelectionMode)999,
                ThemeVariant.Light));
    }

    [Theory]
    [InlineData(ThemeVariant.Light, ThemeSelectionMode.Light)]
    [InlineData(ThemeVariant.Dark, ThemeSelectionMode.Dark)]
    public void GetExplicitMode_MapsEveryThemeVariant(
        ThemeVariant theme,
        ThemeSelectionMode expected)
    {
        Assert.Equal(expected, ThemeSelectionPolicy.GetExplicitMode(theme));
    }
}
