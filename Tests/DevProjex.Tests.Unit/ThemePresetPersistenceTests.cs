using DevProjex.Infrastructure.ThemePresets;

namespace DevProjex.Tests.Unit;

public sealed class ThemePresetPersistenceTests
{
    [Theory]
    [InlineData(ThemeVariant.Light, ThemeVariant.Light, ThemeEffectMode.Solid)]
    [InlineData(ThemeVariant.Dark, ThemeVariant.Dark, ThemeEffectMode.Acrylic)]
    [InlineData(null, ThemeVariant.Dark, ThemeEffectMode.Acrylic)]
    public void Session_SystemMode_UsesSystemThemeAndDarkFallback(
        ThemeVariant? systemTheme,
        ThemeVariant expectedTheme,
        ThemeEffectMode expectedEffect)
    {
        using var temp = new TemporaryDirectory();
        var store = new ThemeSettingsStore(() => temp.Path);

        var session = new ThemePresetSession(store, store.Load(), systemTheme);

        Assert.Equal(ThemeSelectionMode.System, session.CurrentMode);
        Assert.Equal(expectedTheme, session.CurrentTheme);
        Assert.Equal(expectedEffect, session.CurrentEffect);
    }

    [Fact]
    public void Session_SystemThemeChange_UsesIndependentRememberedEffectAndPreservesMode()
    {
        using var temp = new TemporaryDirectory();
        var store = new ThemeSettingsStore(() => temp.Path);
        var session = new ThemePresetSession(store, store.Load(), ThemeVariant.Dark);

        session.SelectEffect(ThemeEffectMode.Mica, session.CurrentPreset);
        var lightPreset = session.SynchronizeSystemTheme(ThemeVariant.Light, session.CurrentPreset);

        Assert.Equal(ThemeSelectionMode.System, session.CurrentMode);
        Assert.Equal(ThemeVariant.Light, session.CurrentTheme);
        Assert.Equal(ThemeEffectMode.Solid, session.CurrentEffect);
        Assert.Equal(session.Database.Presets["Light.Solid"], lightPreset);

        session.SynchronizeSystemTheme(ThemeVariant.Dark, lightPreset);

        Assert.Equal(ThemeSelectionMode.System, session.CurrentMode);
        Assert.Equal(ThemeVariant.Dark, session.CurrentTheme);
        Assert.Equal(ThemeEffectMode.Mica, session.CurrentEffect);
    }

    [Fact]
    public void Session_ExplicitMode_IgnoresSubsequentSystemThemeChanges()
    {
        using var temp = new TemporaryDirectory();
        var store = new ThemeSettingsStore(() => temp.Path);
        var session = new ThemePresetSession(store, store.Load(), ThemeVariant.Dark);
        session.SelectMode(ThemeSelectionMode.Light, ThemeVariant.Dark, session.CurrentPreset);

        session.SynchronizeSystemTheme(ThemeVariant.Dark, session.CurrentPreset);

        Assert.Equal(ThemeSelectionMode.Light, session.CurrentMode);
        Assert.Equal(ThemeVariant.Light, session.CurrentTheme);
        Assert.Equal(ThemeEffectMode.Solid, session.CurrentEffect);
    }

    [Fact]
    public void Session_EditSwitchReturn_PreservesEveryPresetWithoutPrematureIo()
    {
        using var temp = new TemporaryDirectory();
        var store = new ThemeSettingsStore(() => temp.Path);
        var session = new ThemePresetSession(store, store.Load());
        var editedAcrylic = CreatePreset(12);

        var transparent = session.Select(ThemeVariant.Dark, ThemeEffectMode.Transparent, editedAcrylic);
        var editedTransparent = CreatePreset(32);
        var mica = session.Select(ThemeVariant.Dark, ThemeEffectMode.Mica, editedTransparent);
        var editedMica = CreatePreset(52);
        session.Select(ThemeVariant.Light, ThemeEffectMode.Solid, editedMica);
        var restoredTransparent = session.Select(
            ThemeVariant.Dark,
            ThemeEffectMode.Transparent,
            CreatePreset(72));
        var restoredMica = session.Select(ThemeVariant.Dark, ThemeEffectMode.Mica, restoredTransparent);
        var restoredAcrylic = session.Select(ThemeVariant.Dark, ThemeEffectMode.Acrylic, restoredMica);

        Assert.NotEqual(editedAcrylic, transparent);
        Assert.NotEqual(editedTransparent, mica);
        Assert.Equal(editedTransparent, restoredTransparent);
        Assert.Equal(editedMica, restoredMica);
        Assert.Equal(editedAcrylic, restoredAcrylic);
        Assert.False(File.Exists(store.GetPath()));
    }

    [Fact]
    public void Session_AllThemeEffectTransitions_PreserveIndependentValuesAndRoundTrip()
    {
        using var temp = new TemporaryDirectory();
        var store = new ThemeSettingsStore(() => temp.Path);
        var session = new ThemePresetSession(store, store.Load());
        var selections = (
            from theme in Enum.GetValues<ThemeVariant>()
            from effect in Enum.GetValues<ThemeEffectMode>()
            select (Theme: theme, Effect: effect)).ToArray();
        var expected = new Dictionary<(ThemeVariant, ThemeEffectMode), ThemePreset>();
        var current = session.CurrentPreset;
        var initialIndex = Array.FindIndex(
            selections,
            selection => selection == (session.CurrentTheme, session.CurrentEffect));
        var orderedSelections = selections[initialIndex..]
            .Concat(selections[..initialIndex])
            .ToArray();

        for (var index = 0; index < orderedSelections.Length; index++)
        {
            var source = (session.CurrentTheme, session.CurrentEffect);
            var edited = CreatePreset(10 + (index * 9));
            expected[source] = edited;
            var target = orderedSelections[(index + 1) % orderedSelections.Length];
            current = session.Select(target.Theme, target.Effect, edited);
        }

        foreach (var selection in selections)
        {
            current = session.Select(selection.Theme, selection.Effect, current);
            Assert.Equal(expected[selection], current);
        }

        Assert.True(session.Persist(current));
        var reloaded = new ThemePresetSession(
            new ThemeSettingsStore(() => temp.Path),
            new ThemeSettingsStore(() => temp.Path).Load());

        Assert.Equal(session.CurrentTheme, reloaded.CurrentTheme);
        Assert.Equal(session.CurrentEffect, reloaded.CurrentEffect);
        foreach (var selection in selections)
            Assert.Equal(expected[selection], reloaded.Database.Presets[$"{selection.Theme}.{selection.Effect}"]);
    }

    [Fact]
    public void Session_SelectionOnly_PersistsSelectionWithoutOverwritingUnchangedPreset()
    {
        using var temp = new TemporaryDirectory();
        var store = new ThemeSettingsStore(() => temp.Path);
        var initial = store.Load();
        var originalAcrylic = initial.Presets["Dark.Acrylic"];
        var session = new ThemePresetSession(store, initial);

        session.Select(ThemeVariant.Light, ThemeEffectMode.Mica, session.CurrentPreset);
        Assert.True(session.Persist(session.CurrentPreset));

        var reloaded = store.Load();
        Assert.Equal("Light.Mica", reloaded.SelectedPreset);
        Assert.Equal(ThemeSelectionMode.Light, reloaded.SelectedThemeMode);
        Assert.Equal(ThemeEffectMode.Mica, reloaded.LightThemeEffect);
        Assert.Equal(originalAcrylic, reloaded.Presets["Dark.Acrylic"]);
    }

    [Fact]
    public void Session_PersistWhenClean_PerformsNoIo()
    {
        using var temp = new TemporaryDirectory();
        var store = new ThemeSettingsStore(() => temp.Path);
        var session = new ThemePresetSession(store, store.Load());

        Assert.True(session.Persist(session.CurrentPreset));
        Assert.False(File.Exists(store.GetPath()));
    }

    [Fact]
    public void Session_FailedPersist_RetainsDirtyStateForRetry()
    {
        var store = new ThemeSettingsStore(() => throw new IOException("Unavailable app-data path."));
        var session = new ThemePresetSession(store, new ThemeSettingsDocument
        {
            SchemaVersion = ThemeSettingsStore.CurrentSchemaVersion,
            DefaultsRevision = ThemeSettingsStore.CurrentDefaultsRevision,
            SelectedPreset = "Dark.Acrylic"
        });
        session.MarkDirty();

        var saved = session.Persist(CreatePreset(30));

        Assert.False(saved);
        Assert.True(session.IsDirty);
    }

    [Fact]
    public void Session_StaleInstancesPersistingDifferentPresets_MergeBothEdits()
    {
        using var temp = new TemporaryDirectory();
        var storeA = new ThemeSettingsStore(() => temp.Path);
        var storeB = new ThemeSettingsStore(() => temp.Path);
        var sessionA = new ThemePresetSession(storeA, storeA.Load());
        var sessionB = new ThemePresetSession(storeB, storeB.Load());
        var editedAcrylic = CreatePreset(23);
        var editedMica = CreatePreset(63);

        sessionA.MarkDirty();
        Assert.True(sessionA.Persist(editedAcrylic));

        sessionB.Select(ThemeVariant.Light, ThemeEffectMode.Mica, sessionB.CurrentPreset);
        sessionB.MarkDirty();
        Assert.True(sessionB.Persist(editedMica));

        var reloaded = new ThemeSettingsStore(() => temp.Path).Load();
        Assert.Equal(editedAcrylic, reloaded.Presets["Dark.Acrylic"]);
        Assert.Equal(editedMica, reloaded.Presets["Light.Mica"]);
    }

    [Fact]
    public void Session_StaleInstancesChangingDifferentThemeEffects_MergeBothPreferences()
    {
        using var temp = new TemporaryDirectory();
        var storeA = new ThemeSettingsStore(() => temp.Path);
        var storeB = new ThemeSettingsStore(() => temp.Path);
        var sessionA = new ThemePresetSession(storeA, storeA.Load(), ThemeVariant.Dark);
        var sessionB = new ThemePresetSession(storeB, storeB.Load(), ThemeVariant.Dark);

        var darkMica = sessionA.SelectEffect(ThemeEffectMode.Mica, sessionA.CurrentPreset);
        sessionB.SynchronizeSystemTheme(ThemeVariant.Light, sessionB.CurrentPreset);
        var lightAcrylic = sessionB.SelectEffect(ThemeEffectMode.Acrylic, sessionB.CurrentPreset);

        Assert.True(sessionA.Persist(darkMica));
        Assert.True(sessionB.Persist(lightAcrylic));

        var reloaded = storeA.Load();
        Assert.Equal(ThemeSelectionMode.System, reloaded.SelectedThemeMode);
        Assert.Equal(ThemeEffectMode.Mica, reloaded.DarkThemeEffect);
        Assert.Equal(ThemeEffectMode.Acrylic, reloaded.LightThemeEffect);
    }

    private static ThemePreset CreatePreset(double seed) => new()
    {
        BackgroundTransparency = seed,
        PanelContrast = seed + 1,
        MenuTransparency = seed + 2,
        BorderVisibility = seed + 3
    };
}
