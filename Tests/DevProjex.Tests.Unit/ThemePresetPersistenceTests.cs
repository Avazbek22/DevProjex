using System.Text.Json.Serialization;
using DevProjex.Infrastructure.ThemePresets;

namespace DevProjex.Tests.Unit;

public sealed class ThemePresetPersistenceTests
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    [Fact]
    public void Load_DefaultsContainEveryThemeAndEffectCombination()
    {
        using var temp = new TemporaryDirectory();
        var store = new UserSettingsStore(() => temp.Path);

        var database = store.Load();

        foreach (var theme in Enum.GetValues<ThemeVariant>())
        {
            foreach (var effect in Enum.GetValues<ThemeEffectMode>())
            {
                var key = $"{theme}.{effect}";
                var preset = Assert.Contains(key, database.Presets);
                Assert.Equal(theme, preset.Theme);
                Assert.Equal(effect, preset.Effect);
            }
        }
    }

    [Fact]
    public void Load_SchemaTwoDocument_AddsSolidPresetsAndPreservesLegacyAppearance()
    {
        using var temp = new TemporaryDirectory();
        var store = new UserSettingsStore(() => temp.Path);
        var legacyPreset = CreatePreset(ThemeVariant.Light, ThemeEffectMode.Mica, 17);
        var legacyDatabase = new UserSettingsDb
        {
            SchemaVersion = 2,
            LastSelected = "Light.Mica",
            Presets = new Dictionary<string, ThemePreset>
            {
                ["Light.Mica"] = legacyPreset
            }
        };
        WriteDatabase(store.GetPath(), legacyDatabase);

        var migrated = store.Load();

        Assert.True(migrated.SchemaVersion > legacyDatabase.SchemaVersion);
        Assert.Equal("Light.Mica", migrated.LastSelected);
        var migratedPreset = migrated.Presets["Light.Mica"];
        Assert.Equal(legacyPreset.MaterialIntensity, migratedPreset.MaterialIntensity);
        Assert.Equal(legacyPreset.BlurRadius, migratedPreset.BlurRadius);
        Assert.Equal(legacyPreset.MenuChildIntensity, migratedPreset.MenuChildIntensity);
        Assert.Equal(legacyPreset.BorderStrength, migratedPreset.BorderStrength);
        AssertLegacySurfaceAlphasPreserved(legacyPreset, migratedPreset);
        Assert.Equal(ThemeEffectMode.Solid, migrated.Presets["Light.Solid"].Effect);
        Assert.Equal(ThemeEffectMode.Solid, migrated.Presets["Dark.Solid"].Effect);
        Assert.Equal(8, migrated.Presets.Count);

        var reloaded = new UserSettingsStore(() => temp.Path).Load();
        Assert.Equal(migrated.SchemaVersion, reloaded.SchemaVersion);
        Assert.Equal(migrated.Presets, reloaded.Presets);
    }

    [Fact]
    public void Load_SchemaThreeActiveTransparentBlur_MigratesSelectionAndValuesToBlur()
    {
        using var temp = new TemporaryDirectory();
        var store = new UserSettingsStore(() => temp.Path);
        var legacyTransparent = CreatePreset(ThemeVariant.Dark, ThemeEffectMode.Transparent, 37) with
        {
            BlurRadius = 64
        };
        WriteDatabase(store.GetPath(), new UserSettingsDb
        {
            SchemaVersion = 3,
            LastSelected = "Dark.Transparent",
            Presets = new Dictionary<string, ThemePreset>
            {
                ["Dark.Transparent"] = legacyTransparent
            }
        });

        var migrated = store.Load();

        Assert.Equal(5, migrated.SchemaVersion);
        Assert.Equal("Dark.Acrylic", migrated.LastSelected);
        var migratedAcrylic = migrated.Presets["Dark.Acrylic"];
        Assert.Equal(ThemeEffectMode.Acrylic, migratedAcrylic.Effect);
        Assert.Equal(0, migratedAcrylic.BlurRadius);
        Assert.Equal(legacyTransparent.MenuChildIntensity, migratedAcrylic.MenuChildIntensity);
        Assert.Equal(legacyTransparent.BorderStrength, migratedAcrylic.BorderStrength);
        AssertLegacySurfaceAlphasPreserved(
            legacyTransparent with { Effect = ThemeEffectMode.Acrylic },
            migratedAcrylic);
        Assert.Equal(0, migrated.Presets["Dark.Transparent"].BlurRadius);
    }

    [Fact]
    public void Load_SchemaThreeInactiveTransparentBlur_PreservesSelectedAndExistingBlurPreset()
    {
        using var temp = new TemporaryDirectory();
        var store = new UserSettingsStore(() => temp.Path);
        var legacyTransparent = CreatePreset(ThemeVariant.Light, ThemeEffectMode.Transparent, 21) with
        {
            BlurRadius = 52
        };
        var existingBlur = CreatePreset(ThemeVariant.Light, ThemeEffectMode.Acrylic, 83);
        WriteDatabase(store.GetPath(), new UserSettingsDb
        {
            SchemaVersion = 3,
            LastSelected = "Light.Mica",
            Presets = new Dictionary<string, ThemePreset>
            {
                ["Light.Transparent"] = legacyTransparent,
                ["Light.Acrylic"] = existingBlur
            }
        });

        var migrated = store.Load();

        Assert.Equal("Light.Mica", migrated.LastSelected);
        var migratedAcrylic = migrated.Presets["Light.Acrylic"];
        Assert.Equal(existingBlur.BlurRadius, migratedAcrylic.BlurRadius);
        Assert.Equal(existingBlur.MenuChildIntensity, migratedAcrylic.MenuChildIntensity);
        Assert.Equal(existingBlur.BorderStrength, migratedAcrylic.BorderStrength);
        AssertLegacySurfaceAlphasPreserved(existingBlur, migratedAcrylic);
        Assert.Equal(0, migrated.Presets["Light.Transparent"].BlurRadius);
    }

    [Fact]
    public void Load_SchemaFourPalette_MigratesOnceAndPreservesEveryVisibleSurfaceAlpha()
    {
        using var temp = new TemporaryDirectory();
        var store = new UserSettingsStore(() => temp.Path);
        var legacyTransparent = CreatePreset(ThemeVariant.Dark, ThemeEffectMode.Transparent, 0) with
        {
            MaterialIntensity = 60.86261980830672,
            PanelContrast = 51.59744408945688
        };
        var legacyMica = CreatePreset(ThemeVariant.Light, ThemeEffectMode.Mica, 0) with
        {
            MaterialIntensity = 73,
            PanelContrast = 67
        };
        WriteDatabase(store.GetPath(), new UserSettingsDb
        {
            SchemaVersion = 4,
            LastSelected = "Dark.Transparent",
            Presets = new Dictionary<string, ThemePreset>
            {
                ["Dark.Transparent"] = legacyTransparent,
                ["Light.Mica"] = legacyMica
            }
        });

        var migrated = store.Load();

        Assert.Equal(5, migrated.SchemaVersion);
        AssertLegacySurfaceAlphasPreserved(legacyTransparent, migrated.Presets["Dark.Transparent"]);
        AssertLegacySurfaceAlphasPreserved(legacyMica, migrated.Presets["Light.Mica"]);
        Assert.NotEqual(legacyTransparent.MaterialIntensity, migrated.Presets["Dark.Transparent"].MaterialIntensity);
        Assert.NotEqual(legacyMica.PanelContrast, migrated.Presets["Light.Mica"].PanelContrast);

        var reloaded = new UserSettingsStore(() => temp.Path).Load();
        Assert.Equal(migrated.Presets["Dark.Transparent"], reloaded.Presets["Dark.Transparent"]);
        Assert.Equal(migrated.Presets["Light.Mica"], reloaded.Presets["Light.Mica"]);
    }

    [Fact]
    public void SetPreset_RepairsMetadataAndEveryInvalidPercentage()
    {
        using var temp = new TemporaryDirectory();
        var store = new UserSettingsStore(() => temp.Path);
        var database = store.Load();
        var invalid = new ThemePreset
        {
            Theme = ThemeVariant.Light,
            Effect = ThemeEffectMode.Acrylic,
            MaterialIntensity = double.NaN,
            BlurRadius = double.PositiveInfinity,
            PanelContrast = -1,
            MenuChildIntensity = 101,
            BorderStrength = double.NegativeInfinity
        };

        store.SetPreset(database, ThemeVariant.Dark, ThemeEffectMode.Mica, invalid);
        var normalized = database.Presets["Dark.Mica"];

        Assert.Equal(ThemeVariant.Dark, normalized.Theme);
        Assert.Equal(ThemeEffectMode.Mica, normalized.Effect);
        Assert.True(double.IsFinite(normalized.MaterialIntensity));
        Assert.True(double.IsFinite(normalized.BlurRadius));
        Assert.Equal(0, normalized.PanelContrast);
        Assert.Equal(100, normalized.MenuChildIntensity);
        Assert.True(double.IsFinite(normalized.BorderStrength));
        Assert.All(GetPercentages(normalized), value => Assert.InRange(value, 0, 100));
    }

    [Theory]
    [InlineData("999.999")]
    [InlineData("Dark.999")]
    [InlineData("999.Transparent")]
    public void TryParseKey_RejectsUndefinedNumericEnumValues(string key)
    {
        using var temp = new TemporaryDirectory();
        var store = new UserSettingsStore(() => temp.Path);

        Assert.False(store.TryParseKey(key, out _, out _));
    }

    [Fact]
    public void Session_EditSwitchReturn_PreservesEachPresetInMemoryWithoutDiskIo()
    {
        using var temp = new TemporaryDirectory();
        var store = new UserSettingsStore(() => temp.Path);
        var session = new ThemePresetSession(store, store.Load());
        var editedTransparent = CreatePreset(ThemeVariant.Dark, ThemeEffectMode.Transparent, 11);

        var mica = session.Select(ThemeVariant.Dark, ThemeEffectMode.Mica, editedTransparent);
        var editedMica = CreatePreset(ThemeVariant.Dark, ThemeEffectMode.Mica, 42);
        session.Select(ThemeVariant.Light, ThemeEffectMode.Acrylic, editedMica);
        var restoredMica = session.Select(
            ThemeVariant.Dark,
            ThemeEffectMode.Mica,
            CreatePreset(ThemeVariant.Light, ThemeEffectMode.Acrylic, 73));
        var restoredTransparent = session.Select(
            ThemeVariant.Dark,
            ThemeEffectMode.Transparent,
            restoredMica);

        Assert.NotEqual(editedTransparent.MaterialIntensity, mica.MaterialIntensity);
        Assert.Equal(editedMica, restoredMica);
        Assert.Equal(editedTransparent, restoredTransparent);
        Assert.False(File.Exists(store.GetPath()));
    }

    [Fact]
    public void Session_AllSelectionTransitions_PreserveIndependentPresetValues()
    {
        using var temp = new TemporaryDirectory();
        var store = new UserSettingsStore(() => temp.Path);
        var session = new ThemePresetSession(store, store.Load());
        var allSelections = (
            from theme in Enum.GetValues<ThemeVariant>()
            from effect in Enum.GetValues<ThemeEffectMode>()
            select (Theme: theme, Effect: effect)).ToArray();
        var initialIndex = Array.FindIndex(
            allSelections,
            selection => selection.Theme == session.CurrentTheme && selection.Effect == session.CurrentEffect);
        var selections = allSelections[initialIndex..].Concat(allSelections[..initialIndex]).ToArray();
        var expected = new Dictionary<(ThemeVariant, ThemeEffectMode), ThemePreset>();
        var currentValues = session.CurrentPreset;

        for (var index = 0; index < selections.Length; index++)
        {
            var currentSelection = (session.CurrentTheme, session.CurrentEffect);
            var edited = CreatePreset(currentSelection.CurrentTheme, currentSelection.CurrentEffect, 10 + (index * 7));
            expected[currentSelection] = edited;
            var target = selections[(index + 1) % selections.Length];
            currentValues = session.Select(target.Theme, target.Effect, edited);
        }

        foreach (var target in selections)
        {
            currentValues = session.Select(target.Theme, target.Effect, currentValues);
            Assert.Equal(expected[(target.Theme, target.Effect)], currentValues);
        }

        Assert.False(File.Exists(store.GetPath()));
    }

    [Fact]
    public void Session_SolidSelectionAndValues_RoundTripAcrossRestart()
    {
        using var temp = new TemporaryDirectory();
        var store = new UserSettingsStore(() => temp.Path);
        var session = new ThemePresetSession(store, store.Load());
        session.Select(ThemeVariant.Dark, ThemeEffectMode.Solid, session.CurrentPreset);
        var editedSolid = CreatePreset(ThemeVariant.Dark, ThemeEffectMode.Solid, 64);

        Assert.True(session.Persist(editedSolid));

        var reloadedSession = new ThemePresetSession(
            new UserSettingsStore(() => temp.Path),
            new UserSettingsStore(() => temp.Path).Load());
        Assert.Equal(ThemeVariant.Dark, reloadedSession.CurrentTheme);
        Assert.Equal(ThemeEffectMode.Solid, reloadedSession.CurrentEffect);
        Assert.Equal(editedSolid, reloadedSession.CurrentPreset);
    }

    [Fact]
    public void Session_PersistWhenClean_PerformsNoIo()
    {
        using var temp = new TemporaryDirectory();
        var store = new UserSettingsStore(() => temp.Path);
        var session = new ThemePresetSession(store, store.Load());

        Assert.True(session.Persist(session.CurrentPreset));
        Assert.False(File.Exists(store.GetPath()));
    }

    [Fact]
    public void Session_FailedPersist_RetainsDirtyStateForAClosingWindowRetry()
    {
        var store = new UserSettingsStore(() => throw new IOException("Unavailable app-data path."));
        var database = new UserSettingsDb
        {
            LastSelected = "Dark.Transparent",
            Presets = new Dictionary<string, ThemePreset>()
        };
        var session = new ThemePresetSession(store, database);
        session.MarkDirty();

        var saved = session.Persist(CreatePreset(ThemeVariant.Dark, ThemeEffectMode.Transparent, 32));

        Assert.False(saved);
        Assert.True(session.IsDirty);
    }

    [Fact]
    public void Session_StaleInstancesEditingDifferentPresets_MergeWithoutLostUpdates()
    {
        using var temp = new TemporaryDirectory();
        var firstStore = new UserSettingsStore(() => temp.Path);
        var secondStore = new UserSettingsStore(() => temp.Path);
        var firstSession = new ThemePresetSession(firstStore, firstStore.Load());
        var secondSession = new ThemePresetSession(secondStore, secondStore.Load());
        var editedTransparent = CreatePreset(ThemeVariant.Dark, ThemeEffectMode.Transparent, 12);
        var editedMica = CreatePreset(ThemeVariant.Light, ThemeEffectMode.Mica, 52);

        firstSession.MarkDirty();
        Assert.True(firstSession.Persist(editedTransparent));

        secondSession.Select(ThemeVariant.Light, ThemeEffectMode.Mica, secondSession.CurrentPreset);
        secondSession.MarkDirty();
        Assert.True(secondSession.Persist(editedMica));

        var reloaded = new UserSettingsStore(() => temp.Path).Load();
        Assert.Equal(editedTransparent, reloaded.Presets["Dark.Transparent"]);
        Assert.Equal(editedMica, reloaded.Presets["Light.Mica"]);
        Assert.Equal("Light.Mica", reloaded.LastSelected);
    }

    [Fact]
    public void Store_StaleViewSettingsWrite_PreservesNewerThemePreset()
    {
        using var temp = new TemporaryDirectory();
        var themeStore = new UserSettingsStore(() => temp.Path);
        var viewStore = new UserSettingsStore(() => temp.Path);
        var themeDatabase = themeStore.Load();
        var staleViewDatabase = viewStore.Load();
        var editedMica = CreatePreset(ThemeVariant.Dark, ThemeEffectMode.Mica, 31);
        themeStore.SetPreset(themeDatabase, ThemeVariant.Dark, ThemeEffectMode.Mica, editedMica);

        Assert.True(themeStore.TryPersistThemeChanges(
            themeDatabase,
            ["Dark.Mica"],
            "Dark.Mica"));

        staleViewDatabase.ViewSettings = staleViewDatabase.ViewSettings with
        {
            IsCompactMode = true,
            PreferredLanguage = AppLanguage.Ru
        };
        Assert.True(viewStore.TryPersistViewSettings(staleViewDatabase));

        var reloaded = new UserSettingsStore(() => temp.Path).Load();
        Assert.Equal(editedMica, reloaded.Presets["Dark.Mica"]);
        Assert.True(reloaded.ViewSettings.IsCompactMode);
        Assert.Equal(AppLanguage.Ru, reloaded.ViewSettings.PreferredLanguage);
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

    private static double[] GetPercentages(ThemePreset preset) =>
    [
        preset.MaterialIntensity,
        preset.BlurRadius,
        preset.PanelContrast,
        preset.MenuChildIntensity,
        preset.BorderStrength
    ];

    private static void AssertLegacySurfaceAlphasPreserved(ThemePreset legacy, ThemePreset migrated)
    {
        var legacyPanelAlpha = CalculateLegacyPanelAlpha(legacy);
        var migratedPanelAlpha = CalculateMigratedPanelAlpha(migrated);
        Assert.Equal(legacyPanelAlpha, migratedPanelAlpha);

        if (legacy.Effect == ThemeEffectMode.Solid)
            return;

        var legacyStripAlpha = CalculateAlpha(legacyPanelAlpha, 240, legacy.PanelContrast);
        var migratedStripAlpha = CalculateAlpha(migratedPanelAlpha, byte.MaxValue, migrated.PanelContrast);
        Assert.Equal(legacyStripAlpha, migratedStripAlpha);
    }

    private static byte CalculateLegacyPanelAlpha(ThemePreset preset)
    {
        if (preset.Effect == ThemeEffectMode.Solid)
            return byte.MaxValue;
        if (preset.Effect == ThemeEffectMode.Mica)
            return preset.Theme == ThemeVariant.Dark ? (byte)112 : (byte)150;

        var normalized = Math.Clamp(preset.MaterialIntensity / 100, 0, 1);
        var backgroundAlpha = (byte)Math.Clamp(
            Math.Round(byte.MaxValue * (1 - normalized)) + 22,
            90,
            byte.MaxValue);
        return (byte)Math.Max(60, backgroundAlpha - 12);
    }

    private static byte CalculateMigratedPanelAlpha(ThemePreset preset)
    {
        if (preset.Effect == ThemeEffectMode.Solid)
            return byte.MaxValue;
        if (preset.Effect == ThemeEffectMode.Mica)
            return preset.Theme == ThemeVariant.Dark ? (byte)112 : (byte)150;

        var normalized = Math.Clamp(preset.MaterialIntensity / 100, 0, 1);
        var backgroundAlpha = (byte)Math.Round(byte.MaxValue + ((90 - byte.MaxValue) * normalized));
        return (byte)Math.Max(60, backgroundAlpha - 12);
    }

    private static byte CalculateAlpha(byte start, byte end, double percentage)
    {
        var normalized = Math.Clamp(percentage / 100, 0, 1);
        return (byte)Math.Round(start + ((end - start) * normalized));
    }

    private static void WriteDatabase(string path, UserSettingsDb database)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(database, SerializerOptions));
    }
}
