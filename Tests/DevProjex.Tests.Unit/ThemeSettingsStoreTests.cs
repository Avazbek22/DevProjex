using System.Text.Json.Serialization;
using DevProjex.Infrastructure.Persistence;
using DevProjex.Infrastructure.ThemePresets;

namespace DevProjex.Tests.Unit;

public sealed class ThemeSettingsStoreTests
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static TheoryData<string, double, double, double, double> FactoryPresets => new()
    {
        { "Light.Transparent", 100, 24.198717948717942, 0, 49.19871794871795 },
        { "Light.Solid", 78.43450479233228, 0, 0, 71.31410256410255 },
        { "Light.Mica", 100, 0, 0, 47.91666666666667 },
        { "Light.Acrylic", 100, 14.903846153846153, 0, 61.69871794871794 },
        { "Dark.Transparent", 39.58333333333333, 53.68589743589743, 0, 31.789137380191697 },
        { "Dark.Solid", 60.86261980830672, 51.59744408945688, 0, 28.36538461538461 },
        { "Dark.Mica", 100, 0, 49.19871794871795, 23.557692307692303 },
        { "Dark.Acrylic", 84.77564102564102, 7.532051282051282, 46.9551282051282, 19.71153846153846 }
    };

    [Theory]
    [MemberData(nameof(FactoryPresets))]
    public void FactoryDefaults_ExactlyMatchCalibratedPresetPack(
        string key,
        double backgroundTransparency,
        double panelContrast,
        double menuTransparency,
        double borderVisibility)
    {
        using var temp = new TemporaryDirectory();
        var store = new ThemeSettingsStore(() => temp.Path);

        var document = store.Load();
        var preset = Assert.Contains(key, document.Presets);

        Assert.Equal(backgroundTransparency, preset.BackgroundTransparency);
        Assert.Equal(panelContrast, preset.PanelContrast);
        Assert.Equal(menuTransparency, preset.MenuTransparency);
        Assert.Equal(borderVisibility, preset.BorderVisibility);
        Assert.Equal("Dark.Acrylic", document.SelectedPreset);
        Assert.Equal(ThemeSelectionMode.System, document.SelectedThemeMode);
        Assert.Equal(ThemeEffectMode.Solid, document.LightThemeEffect);
        Assert.Equal(ThemeEffectMode.Acrylic, document.DarkThemeEffect);
        Assert.Equal(ThemeSettingsStore.CurrentSchemaVersion, document.SchemaVersion);
        Assert.Equal(ThemeSettingsStore.CurrentDefaultsRevision, document.DefaultsRevision);
    }

    [Fact]
    public void LoadForStartup_MissingFile_CreatesCleanThemeDocumentWithoutLegacyFields()
    {
        using var temp = new TemporaryDirectory();
        var store = new ThemeSettingsStore(() => temp.Path);

        var loaded = store.LoadForStartup(TimeSpan.FromSeconds(1));

        Assert.Equal(8, loaded.Presets.Count);
        Assert.True(File.Exists(store.GetPath()));
        Assert.True(File.Exists(store.GetPath() + ".bak"));
        var json = File.ReadAllText(store.GetPath());
        Assert.DoesNotContain("materialIntensity", json, StringComparison.Ordinal);
        Assert.DoesNotContain("blurRadius", json, StringComparison.Ordinal);
        Assert.DoesNotContain("menuChildIntensity", json, StringComparison.Ordinal);
        Assert.DoesNotContain("borderStrength", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"theme\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"effect\"", json, StringComparison.Ordinal);
        Assert.Contains("\"selectedThemeMode\": \"system\"", json, StringComparison.Ordinal);
        Assert.Contains("\"lightThemeEffect\": \"solid\"", json, StringComparison.Ordinal);
        Assert.Contains("\"darkThemeEffect\": \"acrylic\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void EnsureStorageExists_MissingBackup_RecreatesItWithoutLosingCurrentTheme()
    {
        using var temp = new TemporaryDirectory();
        var store = new ThemeSettingsStore(() => temp.Path);
        var document = store.Load();
        var edited = CreatePreset(33);
        store.SetPreset(document, ThemeVariant.Light, ThemeEffectMode.Mica, edited);
        document.SelectedPreset = "Light.Mica";
        Assert.True(store.TrySave(document));
        File.Delete(store.GetPath() + ".bak");

        Assert.True(store.EnsureStorageExists());

        var reloaded = store.Load();
        Assert.True(File.Exists(store.GetPath() + ".bak"));
        Assert.Equal("Light.Mica", reloaded.SelectedPreset);
        Assert.Equal(edited, reloaded.Presets["Light.Mica"]);
    }

    [Fact]
    public void LoadForStartup_WhenStoreLockIsHeld_ReturnsFactoryDefaultsWithinBoundedTime()
    {
        using var temp = new TemporaryDirectory();
        var store = new ThemeSettingsStore(() => temp.Path);
        var lockPath = store.GetPath() + ".lock";
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        using var heldLock = new FileStream(
            lockPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var loaded = store.LoadForStartup(TimeSpan.FromMilliseconds(25));

        stopwatch.Stop();
        Assert.Equal("Dark.Acrylic", loaded.SelectedPreset);
        Assert.Equal(8, loaded.Presets.Count);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1), $"Startup load took {stopwatch.Elapsed}.");
    }

    [Theory]
    [InlineData(0, ThemeSettingsStore.CurrentDefaultsRevision)]
    [InlineData(1, 1)]
    [InlineData(ThemeSettingsStore.CurrentSchemaVersion, 0)]
    public void LoadForStartup_ObsoleteSchemaOrRevision_HardResetsEveryPreset(
        int schemaVersion,
        int defaultsRevision)
    {
        using var temp = new TemporaryDirectory();
        var store = new ThemeSettingsStore(() => temp.Path);
        WriteDocument(store.GetPath(), new ThemeSettingsDocument
        {
            SchemaVersion = schemaVersion,
            DefaultsRevision = defaultsRevision,
            SelectedPreset = "Light.Solid",
            Presets = new Dictionary<string, ThemePreset>
            {
                ["Dark.Acrylic"] = CreatePreset(1)
            }
        });

        var reset = store.LoadForStartup(TimeSpan.FromSeconds(1));

        Assert.Equal("Dark.Acrylic", reset.SelectedPreset);
        Assert.Equal(ThemeSelectionMode.System, reset.SelectedThemeMode);
        Assert.Equal(ThemeEffectMode.Solid, reset.LightThemeEffect);
        Assert.Equal(ThemeEffectMode.Acrylic, reset.DarkThemeEffect);
        Assert.Equal(8, reset.Presets.Count);
        Assert.Equal(84.77564102564102, reset.Presets["Dark.Acrylic"].BackgroundTransparency);
        var persisted = JsonSerializer.Deserialize<ThemeSettingsDocument>(
            File.ReadAllText(store.GetPath()),
            SerializerOptions);
        Assert.NotNull(persisted);
        Assert.Equal(ThemeSettingsStore.CurrentSchemaVersion, persisted!.SchemaVersion);
        Assert.Equal(ThemeSettingsStore.CurrentDefaultsRevision, persisted.DefaultsRevision);
        Assert.Equal(reset.Presets, persisted.Presets);
    }

    [Fact]
    public void LoadForStartup_LegacyCombinedUserSettings_DoesNotSeedIndependentThemeStorage()
    {
        using var temp = new TemporaryDirectory();
        var userStore = new UserSettingsStore(() => temp.Path);
        var themeStore = new ThemeSettingsStore(() => temp.Path);
        WriteJson(userStore.GetPath(), """
        {
          "schemaVersion": 5,
          "presets": {
            "Dark.Acrylic": {
              "materialIntensity": 1,
              "menuChildIntensity": 2,
              "borderStrength": 3
            }
          },
          "lastSelected": "Light.Solid"
        }
        """);

        var userSettings = userStore.LoadForStartup(TimeSpan.FromSeconds(1));
        var themeSettings = themeStore.LoadForStartup(TimeSpan.FromSeconds(1));

        Assert.Equal("Dark.Acrylic", themeSettings.SelectedPreset);
        Assert.Equal(84.77564102564102, themeSettings.Presets["Dark.Acrylic"].BackgroundTransparency);
        Assert.DoesNotContain("materialIntensity", File.ReadAllText(userStore.GetPath()), StringComparison.Ordinal);
        Assert.DoesNotContain("materialIntensity", File.ReadAllText(themeStore.GetPath()), StringComparison.Ordinal);
        Assert.False(userSettings.ViewSettings.IsCompactMode);
    }

    [Fact]
    public void FutureDocument_IsPreservedAndRejectsWritesFromOlderApplication()
    {
        using var temp = new TemporaryDirectory();
        var store = new ThemeSettingsStore(() => temp.Path);
        var future = new ThemeSettingsDocument
        {
            SchemaVersion = ThemeSettingsStore.CurrentSchemaVersion + 1,
            DefaultsRevision = ThemeSettingsStore.CurrentDefaultsRevision + 1,
            SelectedPreset = "Future.Effect",
            Presets = new Dictionary<string, ThemePreset> { ["Future.Effect"] = CreatePreset(9) }
        };
        WriteDocument(store.GetPath(), future);
        var originalJson = File.ReadAllText(store.GetPath());

        var loaded = store.LoadForStartup(TimeSpan.FromSeconds(1));

        Assert.Equal("Dark.Acrylic", loaded.SelectedPreset);
        Assert.False(store.TrySave(loaded));
        Assert.Equal(originalJson, File.ReadAllText(store.GetPath()));
    }

    [Fact]
    public void OversizedDocument_IsPreservedAndRejectsWrites()
    {
        using var temp = new TemporaryDirectory();
        var store = new ThemeSettingsStore(() => temp.Path);
        var path = store.GetPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
            stream.SetLength(JsonStorePersistence.SmallDocumentMaximumBytes + 1);

        var loaded = store.LoadForStartup(TimeSpan.FromSeconds(1));

        Assert.Equal("Dark.Acrylic", loaded.SelectedPreset);
        Assert.False(store.TrySave(loaded));
        Assert.Equal(JsonStorePersistence.SmallDocumentMaximumBytes + 1, new FileInfo(path).Length);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("corrupt")]
    [InlineData("current")]
    public void FutureDocumentInBackup_BlocksRecoveryEnsureAndEveryWrite(string primaryState)
    {
        using var temp = new TemporaryDirectory();
        var store = new ThemeSettingsStore(() => temp.Path);
        var primaryPath = store.GetPath();
        var backupPath = primaryPath + ".bak";
        const string futureJson = """
        {
          "schemaVersion": 999,
          "defaultsRevision": 999,
          "selectedPreset": "Future.Effect",
          "presets": { "Future.Effect": { "futureProperty": true } }
        }
        """;

        var primaryJson = primaryState switch
        {
            "corrupt" => "{ invalid-primary",
            "current" => JsonSerializer.Serialize(
                new ThemeSettingsDocument
                {
                    SchemaVersion = ThemeSettingsStore.CurrentSchemaVersion,
                    DefaultsRevision = ThemeSettingsStore.CurrentDefaultsRevision,
                    SelectedPreset = "Light.Mica",
                    Presets = new Dictionary<string, ThemePreset>()
                },
                SerializerOptions),
            _ => null
        };
        if (primaryJson is not null)
            WriteJson(primaryPath, primaryJson);
        WriteJson(backupPath, futureJson);

        var loaded = store.LoadForStartup(TimeSpan.FromSeconds(1));

        Assert.False(store.TrySave(loaded));
        Assert.False(store.TryPersistChanges(loaded, ["Dark.Acrylic"], "Dark.Acrylic"));
        Assert.True(store.EnsureStorageExists());
        Assert.Equal(primaryJson is not null, File.Exists(primaryPath));
        if (primaryJson is not null)
            Assert.Equal(primaryJson, File.ReadAllText(primaryPath));
        Assert.Equal(futureJson, File.ReadAllText(backupPath));
    }

    [Fact]
    public void FutureDefaultsRevisionInBackup_IsProtectedWithoutFutureSchemaVersion()
    {
        using var temp = new TemporaryDirectory();
        var store = new ThemeSettingsStore(() => temp.Path);
        var backupPath = store.GetPath() + ".bak";
        const string futureJson = """
        {
          "schemaVersion": 1,
          "defaultsRevision": 999,
          "selectedPreset": "Future.Defaults",
          "presets": {}
        }
        """;
        WriteJson(backupPath, futureJson);

        var loaded = store.LoadForStartup(TimeSpan.FromSeconds(1));

        Assert.False(store.TrySave(loaded));
        Assert.False(File.Exists(store.GetPath()));
        Assert.Equal(futureJson, File.ReadAllText(backupPath));
    }

    [Fact]
    public void CurrentDocument_NormalizesInvalidValuesAddsMissingPresetsAndRemovesUnknownKeys()
    {
        using var temp = new TemporaryDirectory();
        var store = new ThemeSettingsStore(() => temp.Path);
        WriteDocument(store.GetPath(), new ThemeSettingsDocument
        {
            SchemaVersion = ThemeSettingsStore.CurrentSchemaVersion,
            DefaultsRevision = ThemeSettingsStore.CurrentDefaultsRevision,
            SelectedPreset = "invalid",
            SelectedThemeMode = (ThemeSelectionMode)999,
            LightThemeEffect = (ThemeEffectMode)999,
            DarkThemeEffect = (ThemeEffectMode)999,
            Presets = new Dictionary<string, ThemePreset>
            {
                ["Dark.Acrylic"] = new ThemePreset
                {
                    BackgroundTransparency = -5,
                    PanelContrast = -10,
                    MenuTransparency = 125,
                    BorderVisibility = 500
                },
                ["Unknown.Future"] = CreatePreset(50)
            }
        });

        var normalized = store.LoadForStartup(TimeSpan.FromSeconds(1));

        Assert.Equal(8, normalized.Presets.Count);
        Assert.DoesNotContain("Unknown.Future", normalized.Presets);
        Assert.Equal("Dark.Acrylic", normalized.SelectedPreset);
        Assert.Equal(ThemeSelectionMode.System, normalized.SelectedThemeMode);
        Assert.Equal(ThemeEffectMode.Solid, normalized.LightThemeEffect);
        Assert.Equal(ThemeEffectMode.Acrylic, normalized.DarkThemeEffect);
        var acrylic = normalized.Presets["Dark.Acrylic"];
        Assert.Equal(0, acrylic.BackgroundTransparency);
        Assert.Equal(0, acrylic.PanelContrast);
        Assert.Equal(100, acrylic.MenuTransparency);
        Assert.Equal(100, acrylic.BorderVisibility);
    }

    [Fact]
    public void SetPreset_NonFiniteRuntimeValues_FallsBackToCalibratedDefaults()
    {
        using var temp = new TemporaryDirectory();
        var store = new ThemeSettingsStore(() => temp.Path);
        var document = store.Load();

        store.SetPreset(document, ThemeVariant.Dark, ThemeEffectMode.Acrylic, new ThemePreset
        {
            BackgroundTransparency = double.NaN,
            PanelContrast = double.NegativeInfinity,
            MenuTransparency = double.PositiveInfinity,
            BorderVisibility = double.NaN
        });

        var preset = document.Presets["Dark.Acrylic"];
        Assert.Equal(84.77564102564102, preset.BackgroundTransparency);
        Assert.Equal(7.532051282051282, preset.PanelContrast);
        Assert.Equal(46.9551282051282, preset.MenuTransparency);
        Assert.Equal(19.71153846153846, preset.BorderVisibility);
    }

    [Fact]
    public void CorruptPrimary_RecoversCurrentBackupWithoutResettingUserPresets()
    {
        using var temp = new TemporaryDirectory();
        var store = new ThemeSettingsStore(() => temp.Path);
        var current = store.Load();
        var edited = CreatePreset(37);
        store.SetPreset(current, ThemeVariant.Light, ThemeEffectMode.Mica, edited);
        current.SelectedPreset = "Light.Mica";
        Assert.True(store.TrySave(current));
        File.WriteAllText(store.GetPath(), "{ invalid");

        var recovered = new ThemeSettingsStore(() => temp.Path).LoadForStartup(TimeSpan.FromSeconds(1));

        Assert.Equal("Light.Mica", recovered.SelectedPreset);
        Assert.Equal(edited, recovered.Presets["Light.Mica"]);
    }

    [Fact]
    public void EnsureStorageExists_CorruptPrimaryRestoresCurrentBackupBeforeCreatingDefaults()
    {
        using var temp = new TemporaryDirectory();
        var store = new ThemeSettingsStore(() => temp.Path);
        var current = store.Load();
        var edited = CreatePreset(43);
        store.SetPreset(current, ThemeVariant.Light, ThemeEffectMode.Mica, edited);
        current.SelectedPreset = "Light.Mica";
        Assert.True(store.TrySave(current));
        File.WriteAllText(store.GetPath(), "{ invalid");

        Assert.True(store.EnsureStorageExists());

        var recovered = new ThemeSettingsStore(() => temp.Path).Load();
        Assert.Equal("Light.Mica", recovered.SelectedPreset);
        Assert.Equal(edited, recovered.Presets["Light.Mica"]);
        Assert.Equal(
            File.ReadAllText(store.GetPath()),
            File.ReadAllText(store.GetPath() + ".bak"));
    }

    [Fact]
    public void ResetToDefaults_OverwritesEveryCustomPresetAndSelectionOnDisk()
    {
        using var temp = new TemporaryDirectory();
        var store = new ThemeSettingsStore(() => temp.Path);
        var customized = store.Load();
        foreach (var key in customized.Presets.Keys.ToArray())
            customized.Presets[key] = CreatePreset(1);
        customized.SelectedPreset = "Light.Solid";
        Assert.True(store.TrySave(customized));

        var reset = store.ResetToDefaults();
        var reloaded = store.Load();

        Assert.Equal("Dark.Acrylic", reset.SelectedPreset);
        Assert.Equal("Dark.Acrylic", reloaded.SelectedPreset);
        Assert.Equal(8, reloaded.Presets.Count);
        foreach (var expected in reset.Presets)
            Assert.Equal(expected.Value, reloaded.Presets[expected.Key]);
        Assert.DoesNotContain(reloaded.Presets.Values, preset => preset == CreatePreset(1));
    }

    [Fact]
    public void StaleInstances_EditingDifferentPresets_MergeWithoutLostUpdates()
    {
        using var temp = new TemporaryDirectory();
        var storeA = new ThemeSettingsStore(() => temp.Path);
        var storeB = new ThemeSettingsStore(() => temp.Path);
        var documentA = storeA.Load();
        var documentB = storeB.Load();
        var transparent = CreatePreset(21);
        var mica = CreatePreset(61);
        storeA.SetPreset(documentA, ThemeVariant.Dark, ThemeEffectMode.Transparent, transparent);
        storeB.SetPreset(documentB, ThemeVariant.Light, ThemeEffectMode.Mica, mica);

        Assert.True(storeA.TryPersistChanges(documentA, ["Dark.Transparent"], "Dark.Transparent"));
        Assert.True(storeB.TryPersistChanges(documentB, ["Light.Mica"], "Light.Mica"));

        var reloaded = new ThemeSettingsStore(() => temp.Path).Load();
        Assert.Equal(transparent, reloaded.Presets["Dark.Transparent"]);
        Assert.Equal(mica, reloaded.Presets["Light.Mica"]);
        Assert.Equal("Light.Mica", reloaded.SelectedPreset);
    }

    [Theory]
    [InlineData("Light.Transparent", ThemeVariant.Light, ThemeEffectMode.Transparent)]
    [InlineData("Light.Solid", ThemeVariant.Light, ThemeEffectMode.Solid)]
    [InlineData("Dark.Mica", ThemeVariant.Dark, ThemeEffectMode.Mica)]
    [InlineData("dark.acrylic", ThemeVariant.Dark, ThemeEffectMode.Acrylic)]
    public void TryParseKey_ParsesEverySupportedIdentity(
        string key,
        ThemeVariant expectedTheme,
        ThemeEffectMode expectedEffect)
    {
        using var temp = new TemporaryDirectory();
        var store = new ThemeSettingsStore(() => temp.Path);

        Assert.True(store.TryParseKey(key, out var theme, out var effect));
        Assert.Equal(expectedTheme, theme);
        Assert.Equal(expectedEffect, effect);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Dark")]
    [InlineData("Dark.Unknown")]
    [InlineData("999.999")]
    public void TryParseKey_RejectsMalformedOrUndefinedIdentity(string? key)
    {
        using var temp = new TemporaryDirectory();
        var store = new ThemeSettingsStore(() => temp.Path);

        Assert.False(store.TryParseKey(key, out _, out _));
    }

    private static ThemePreset CreatePreset(double seed) => new()
    {
        BackgroundTransparency = seed,
        PanelContrast = seed + 1,
        MenuTransparency = seed + 2,
        BorderVisibility = seed + 3
    };

    private static void WriteDocument(string path, ThemeSettingsDocument document)
        => WriteJson(path, JsonSerializer.Serialize(document, SerializerOptions));

    private static void WriteJson(string path, string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
    }
}
