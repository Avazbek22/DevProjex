using DevProjex.Infrastructure.ThemePresets;

namespace DevProjex.Tests.Integration;

public sealed class ThemePresetLifecycleIntegrationTests
{
    [Fact]
    public void SystemMode_CustomEffectsForBothPalettes_RoundTripWithoutBecomingExplicit()
    {
        using var temp = new TemporaryDirectory();
        var store = new ThemeSettingsStore(() => temp.Path);
        var session = new ThemePresetSession(store, store.Load(), ThemeVariant.Dark);

        var darkMica = session.SelectEffect(ThemeEffectMode.Mica, session.CurrentPreset);
        var lightSolid = session.SynchronizeSystemTheme(ThemeVariant.Light, darkMica);
        var lightAcrylic = session.SelectEffect(ThemeEffectMode.Acrylic, lightSolid);
        Assert.True(session.Persist(lightAcrylic));

        var restartedStore = new ThemeSettingsStore(() => temp.Path);
        var restartedLight = new ThemePresetSession(
            restartedStore,
            restartedStore.Load(),
            ThemeVariant.Light);
        var restartedDark = new ThemePresetSession(
            restartedStore,
            restartedStore.Load(),
            ThemeVariant.Dark);

        Assert.Equal(ThemeSelectionMode.System, restartedLight.CurrentMode);
        Assert.Equal(ThemeVariant.Light, restartedLight.CurrentTheme);
        Assert.Equal(ThemeEffectMode.Acrylic, restartedLight.CurrentEffect);
        Assert.Equal(ThemeSelectionMode.System, restartedDark.CurrentMode);
        Assert.Equal(ThemeVariant.Dark, restartedDark.CurrentTheme);
        Assert.Equal(ThemeEffectMode.Mica, restartedDark.CurrentEffect);
    }

    [Fact]
    public void EditSwitchReturnSolidAndRestart_PreservesEveryRequestedState()
    {
        using var temp = new TemporaryDirectory();
        var store = new ThemeSettingsStore(() => temp.Path);
        var session = new ThemePresetSession(store, store.Load());
        var current = session.CurrentPreset;
        session.Select(ThemeVariant.Dark, ThemeEffectMode.Transparent, current);
        var transparent = CreatePreset(ThemeVariant.Dark, ThemeEffectMode.Transparent, 12);

        session.Select(ThemeVariant.Dark, ThemeEffectMode.Mica, transparent);
        var mica = CreatePreset(ThemeVariant.Dark, ThemeEffectMode.Mica, 42);
        session.Select(ThemeVariant.Dark, ThemeEffectMode.Acrylic, mica);
        var acrylic = CreatePreset(ThemeVariant.Dark, ThemeEffectMode.Acrylic, 72);

        var restoredMica = session.Select(ThemeVariant.Dark, ThemeEffectMode.Mica, acrylic);
        var restoredTransparent = session.Select(ThemeVariant.Dark, ThemeEffectMode.Transparent, restoredMica);
        session.Select(ThemeVariant.Dark, ThemeEffectMode.Solid, restoredTransparent);
        var solid = CreatePreset(ThemeVariant.Dark, ThemeEffectMode.Solid, 84);

        Assert.Equal(mica, restoredMica);
        Assert.Equal(transparent, restoredTransparent);
        Assert.True(session.Persist(solid));

        var reloadedStore = new ThemeSettingsStore(() => temp.Path);
        var reloadedSession = new ThemePresetSession(reloadedStore, reloadedStore.Load());
        Assert.Equal(ThemeVariant.Dark, reloadedSession.CurrentTheme);
        Assert.Equal(ThemeEffectMode.Solid, reloadedSession.CurrentEffect);
        Assert.Equal(solid, reloadedSession.CurrentPreset);
        Assert.Equal(transparent, reloadedSession.Database.Presets["Dark.Transparent"]);
        Assert.Equal(mica, reloadedSession.Database.Presets["Dark.Mica"]);
        Assert.Equal(acrylic, reloadedSession.Database.Presets["Dark.Acrylic"]);
    }

    private static ThemePreset CreatePreset(ThemeVariant theme, ThemeEffectMode effect, double seed)
    {
        return new ThemePreset
        {
            BackgroundTransparency = seed,
            PanelContrast = seed + 1,
            MenuTransparency = seed + 2,
            BorderVisibility = seed + 3
        };
    }
}
