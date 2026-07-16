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
    public void Load_SchemaTwoDocument_AddsSolidPresetsWithoutChangingLegacyValues()
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
        Assert.Equal(legacyPreset, migrated.Presets["Light.Mica"]);
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

        Assert.Equal(4, migrated.SchemaVersion);
        Assert.Equal("Dark.Acrylic", migrated.LastSelected);
        Assert.Equal(
            legacyTransparent with { Effect = ThemeEffectMode.Acrylic, BlurRadius = 0 },
            migrated.Presets["Dark.Acrylic"]);
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
        Assert.Equal(existingBlur, migrated.Presets["Light.Acrylic"]);
        Assert.Equal(0, migrated.Presets["Light.Transparent"].BlurRadius);
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

    private static void WriteDatabase(string path, UserSettingsDb database)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(database, SerializerOptions));
    }
}
