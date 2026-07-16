using DevProjex.Infrastructure.ThemePresets;

namespace DevProjex.Tests.Integration;

public sealed class ThemePresetLifecycleIntegrationTests
{
    [Fact]
    public void EditSwitchReturnSolidAndRestart_PreservesEveryRequestedState()
    {
        using var temp = new TemporaryDirectory();
        var store = new UserSettingsStore(() => temp.Path);
        var session = new ThemePresetSession(store, store.Load());
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

        var reloadedStore = new UserSettingsStore(() => temp.Path);
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
            Theme = theme,
            Effect = effect,
            MaterialIntensity = seed,
            BlurRadius = seed + 1,
            PanelContrast = seed + 2,
            MenuChildIntensity = seed + 3,
            BorderStrength = seed + 4
        };
    }
}
