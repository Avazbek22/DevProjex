using DevProjex.Infrastructure.Persistence;

namespace DevProjex.Infrastructure.ThemePresets;

public sealed class UserSettingsStore(Func<string>? appDataPathProvider = null)
{
    private const int CurrentSchemaVersion = 4;
    private const double LegacyBlurEnabledThreshold = 0.0001;
    private const string FolderName = "DevProjex";
    private const string FileName = "user-settings.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        TypeInfoResolver = InfrastructureJsonSerializerContext.Default,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static readonly AppViewSettings DefaultViewSettings = new()
    {
        IsCompactMode = false,
        IsTreeAnimationEnabled = false,
        IsAdvancedIgnoreCountsEnabled = true,
        IsTerminalCommandPromptDismissed = false,
        PreferredLanguage = null
    };
    private static readonly Dictionary<string, ThemePreset> DefaultPresets = CreateDefaultPresetsCore();
    private readonly Func<string> _appDataPathProvider =
        appDataPathProvider ?? (() => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
    private readonly object _sync = new();

    public bool EnsureStorageExists()
    {
        lock (_sync)
        {
            var fileSet = GetFileSet();
            if (!CrossProcessFileLock.TryAcquire(fileSet, out var heldLock))
                return false;

            using var _ = heldLock;
            return EnsureStorageExistsCore(fileSet);
        }
    }

    public UserSettingsDb Load()
    {
        lock (_sync)
        {
            var fileSet = GetFileSet();
            if (!CrossProcessFileLock.TryAcquire(fileSet, out var heldLock))
                return CreateDefaultDb();

            using var _ = heldLock;
            // Loading settings should not create files by itself. A dedicated bootstrap
            // path handles store creation so pure reads remain predictable for tests/tools.
            return LoadInternal(fileSet);
        }
    }

    public UserSettingsDb LoadForStartup(TimeSpan lockTimeout)
    {
        lock (_sync)
        {
            try
            {
                var fileSet = GetFileSet();
                if (!CrossProcessFileLock.TryAcquire(fileSet, lockTimeout, out var heldLock))
                    return CreateDefaultDb();

                using var _ = heldLock;
                return LoadInternal(fileSet);
            }
            catch
            {
                // Startup must remain responsive when app-data resolution or recovery IO fails.
                return CreateDefaultDb();
            }
        }
    }

    public void Save(UserSettingsDb db) => TrySave(db);

    public bool TrySave(UserSettingsDb db)
    {
        lock (_sync)
        {
            try
            {
                var fileSet = GetFileSet();
                if (!CrossProcessFileLock.TryAcquire(fileSet, out var heldLock))
                    return false;

                using var _ = heldLock;
                return TrySaveInternal(fileSet, Normalize(db));
            }
            catch
            {
                return false;
            }
        }
    }

    public ThemePreset GetPreset(UserSettingsDb db, ThemeVariant theme, ThemeEffectMode effect)
    {
        var key = GetKey(theme, effect);
        if (db.Presets.TryGetValue(key, out var preset) && preset is not null)
        {
            var normalized = NormalizePreset(preset, CreateDefaultPreset(theme, effect), theme, effect);
            db.Presets[key] = normalized;
            return normalized;
        }

        var created = CreateDefaultPreset(theme, effect);
        db.Presets[key] = created;
        return created;
    }

    public void SetPreset(UserSettingsDb db, ThemeVariant theme, ThemeEffectMode effect, ThemePreset preset)
    {
        var key = GetKey(theme, effect);
        db.Presets[key] = NormalizePreset(preset, CreateDefaultPreset(theme, effect), theme, effect);
    }

    /// <summary>
    /// Resets all presets to factory defaults and saves the result.
    /// Returns the new database with default values applied.
    /// </summary>
    public UserSettingsDb ResetToDefaults()
    {
        var defaultDb = CreateDefaultDb();
        TrySave(defaultDb);
        return defaultDb;
    }

    public string GetPath()
    {
        return GetFileSet().PrimaryPath;
    }

    public bool TryParseKey(string? key, out ThemeVariant theme, out ThemeEffectMode effect)
    {
        theme = ThemeVariant.Dark;
        effect = ThemeEffectMode.Transparent;

        if (string.IsNullOrWhiteSpace(key))
            return false;

        var parts = key.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
            return false;

        if (!Enum.TryParse(parts[0], true, out ThemeVariant parsedTheme) || !Enum.IsDefined(parsedTheme))
            return false;

        if (!Enum.TryParse(parts[1], true, out ThemeEffectMode parsedEffect) || !Enum.IsDefined(parsedEffect))
            return false;

        theme = parsedTheme;
        effect = parsedEffect;
        return true;
    }

    private UserSettingsDb Normalize(UserSettingsDb db)
    {
        var sourceSchemaVersion = db.SchemaVersion;
        db.Presets ??= new Dictionary<string, ThemePreset>();
        db.ViewSettings ??= DefaultViewSettings;
        if (sourceSchemaVersion < 4)
            MigrateLegacyTransparentBlur(db);

        db.SchemaVersion = CurrentSchemaVersion;
        db.ViewSettings = db.ViewSettings with
        {
            // The UI toggle was removed, but older settings files can still contain false.
            // Keep the current behavior deterministic until a raw JSON migration removes the field.
            IsAdvancedIgnoreCountsEnabled = true
        };

        foreach (var preset in CreateDefaultPresets())
        {
            if (!db.Presets.TryGetValue(preset.Key, out var currentPreset) || currentPreset is null)
            {
                db.Presets[preset.Key] = preset.Value;
                continue;
            }

            db.Presets[preset.Key] = NormalizePreset(
                currentPreset,
                preset.Value,
                preset.Value.Theme,
                preset.Value.Effect);
        }

        if (string.IsNullOrWhiteSpace(db.LastSelected) ||
            !db.Presets.ContainsKey(db.LastSelected) ||
            !TryParseKey(db.LastSelected, out _, out _))
            db.LastSelected = GetKey(ThemeVariant.Dark, ThemeEffectMode.Transparent);

        return db;
    }

    private static void MigrateLegacyTransparentBlur(UserSettingsDb db)
    {
        foreach (var theme in Enum.GetValues<ThemeVariant>())
        {
            var transparentKey = GetKey(theme, ThemeEffectMode.Transparent);
            if (!db.Presets.TryGetValue(transparentKey, out var transparentPreset) || transparentPreset is null)
                continue;

            var usedNativeBlur = double.IsFinite(transparentPreset.BlurRadius) &&
                                 transparentPreset.BlurRadius > LegacyBlurEnabledThreshold;
            if (usedNativeBlur && string.Equals(db.LastSelected, transparentKey, StringComparison.OrdinalIgnoreCase))
            {
                var blurKey = GetKey(theme, ThemeEffectMode.Acrylic);
                db.Presets[blurKey] = transparentPreset with
                {
                    Theme = theme,
                    Effect = ThemeEffectMode.Acrylic,
                    BlurRadius = 0
                };
                db.LastSelected = blurKey;
            }

            // From schema 4 onward Transparent never requests a blurred backdrop.
            db.Presets[transparentKey] = transparentPreset with
            {
                Theme = theme,
                Effect = ThemeEffectMode.Transparent,
                BlurRadius = 0
            };
        }
    }

    private JsonStoreFileSet GetFileSet()
        => JsonStoreFileSet.Create(_appDataPathProvider, FolderName, FileName);

    private UserSettingsDb LoadInternal(JsonStoreFileSet fileSet)
    {
        if (JsonStorePersistence.TryReadNormalized(
                fileSet.PrimaryPath,
                SerializerOptions,
                CreateDefaultDb,
                Normalize,
                out var primaryDb,
                out var primaryRequiresRewrite))
        {
            if (primaryRequiresRewrite)
                TrySaveInternal(fileSet, primaryDb);

            return primaryDb;
        }

        if (JsonStorePersistence.TryReadNormalized(
                fileSet.BackupPath,
                SerializerOptions,
                CreateDefaultDb,
                Normalize,
                out var backupDb,
                out _))
        {
            TrySaveInternal(fileSet, backupDb);
            return backupDb;
        }

        var fallback = CreateDefaultDb();
        if (File.Exists(fileSet.PrimaryPath) || File.Exists(fileSet.BackupPath))
            TrySaveInternal(fileSet, fallback);

        return fallback;
    }

    private bool EnsureStorageExistsCore(JsonStoreFileSet fileSet)
    {
        if (JsonStorePersistence.TryReadNormalized(
                fileSet.PrimaryPath,
                SerializerOptions,
                CreateDefaultDb,
                Normalize,
                out var primaryDb,
                out var primaryRequiresRewrite))
        {
            if (primaryRequiresRewrite || !File.Exists(fileSet.BackupPath))
                return TrySaveInternal(fileSet, primaryDb);

            return true;
        }

        if (JsonStorePersistence.TryReadNormalized(
                fileSet.BackupPath,
                SerializerOptions,
                CreateDefaultDb,
                Normalize,
                out var backupDb,
                out _))
        {
            return TrySaveInternal(fileSet, backupDb);
        }

        if (File.Exists(fileSet.PrimaryPath) || File.Exists(fileSet.BackupPath))
            return false;

        return TrySaveInternal(fileSet, CreateDefaultDb());
    }

    private UserSettingsDb CreateDefaultDb()
    {
        var db = new UserSettingsDb
        {
            SchemaVersion = CurrentSchemaVersion,
            Presets = CreateDefaultPresets(),
            LastSelected = GetKey(ThemeVariant.Dark, ThemeEffectMode.Transparent),
            ViewSettings = DefaultViewSettings
        };

        return db;
    }

    private static Dictionary<string, ThemePreset> CreateDefaultPresets()
        => new(DefaultPresets, StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, ThemePreset> CreateDefaultPresetsCore()
    {
        return new Dictionary<string, ThemePreset>(StringComparer.OrdinalIgnoreCase)
        {
            [GetKey(ThemeVariant.Light, ThemeEffectMode.Transparent)] = new ThemePreset
            {
                Theme = ThemeVariant.Light,
                Effect = ThemeEffectMode.Transparent,
                MaterialIntensity = 78.43450479233228,
                BlurRadius = 0,
                PanelContrast = 0,
                MenuChildIntensity = 0,
                BorderStrength = 53.19488817891374
            },
            [GetKey(ThemeVariant.Light, ThemeEffectMode.Solid)] = new ThemePreset
            {
                Theme = ThemeVariant.Light,
                Effect = ThemeEffectMode.Solid,
                MaterialIntensity = 78.43450479233228,
                BlurRadius = 0,
                PanelContrast = 0,
                MenuChildIntensity = 0,
                BorderStrength = 53.19488817891374
            },
            [GetKey(ThemeVariant.Light, ThemeEffectMode.Mica)] = new ThemePreset
            {
                Theme = ThemeVariant.Light,
                Effect = ThemeEffectMode.Mica,
                MaterialIntensity = 100,
                BlurRadius = 0,
                PanelContrast = 0,
                MenuChildIntensity = 0,
                BorderStrength = 57.66773162939298
            },
            [GetKey(ThemeVariant.Light, ThemeEffectMode.Acrylic)] = new ThemePreset
            {
                Theme = ThemeVariant.Light,
                Effect = ThemeEffectMode.Acrylic,
                MaterialIntensity = 75.87859424920129,
                BlurRadius = 0,
                PanelContrast = 0,
                MenuChildIntensity = 0,
                BorderStrength = 100
            },
            [GetKey(ThemeVariant.Dark, ThemeEffectMode.Transparent)] = new ThemePreset
            {
                Theme = ThemeVariant.Dark,
                Effect = ThemeEffectMode.Transparent,
                MaterialIntensity = 60.86261980830672,
                BlurRadius = 0,
                PanelContrast = 51.59744408945688,
                MenuChildIntensity = 0,
                BorderStrength = 31.789137380191697
            },
            [GetKey(ThemeVariant.Dark, ThemeEffectMode.Solid)] = new ThemePreset
            {
                Theme = ThemeVariant.Dark,
                Effect = ThemeEffectMode.Solid,
                MaterialIntensity = 60.86261980830672,
                BlurRadius = 0,
                PanelContrast = 51.59744408945688,
                MenuChildIntensity = 0,
                BorderStrength = 31.789137380191697
            },
            [GetKey(ThemeVariant.Dark, ThemeEffectMode.Mica)] = new ThemePreset
            {
                Theme = ThemeVariant.Dark,
                Effect = ThemeEffectMode.Mica,
                MaterialIntensity = 100,
                BlurRadius = 0,
                PanelContrast = 0,
                MenuChildIntensity = 0,
                BorderStrength = 35.94249201277955
            },
            [GetKey(ThemeVariant.Dark, ThemeEffectMode.Acrylic)] = new ThemePreset
            {
                Theme = ThemeVariant.Dark,
                Effect = ThemeEffectMode.Acrylic,
                MaterialIntensity = 73.00319488817892,
                BlurRadius = 0,
                PanelContrast = 0,
                MenuChildIntensity = 0,
                BorderStrength = 26.677316293929714
            }
        };
    }

    private static ThemePreset CreateDefaultPreset(ThemeVariant theme, ThemeEffectMode effect)
        => DefaultPresets[GetKey(theme, effect)];

    private static string GetKey(ThemeVariant theme, ThemeEffectMode effect) => $"{theme}.{effect}";

    private static ThemePreset NormalizePreset(
        ThemePreset preset,
        ThemePreset fallback,
        ThemeVariant theme,
        ThemeEffectMode effect)
    {
        var materialIntensity = NormalizePercentage(preset.MaterialIntensity, fallback.MaterialIntensity);
        var blurRadius = NormalizePercentage(preset.BlurRadius, fallback.BlurRadius);
        var panelContrast = NormalizePercentage(preset.PanelContrast, fallback.PanelContrast);
        var menuChildIntensity = NormalizePercentage(preset.MenuChildIntensity, fallback.MenuChildIntensity);
        var borderStrength = NormalizePercentage(preset.BorderStrength, fallback.BorderStrength);
        if (preset.Theme == theme &&
            preset.Effect == effect &&
            preset.MaterialIntensity.Equals(materialIntensity) &&
            preset.BlurRadius.Equals(blurRadius) &&
            preset.PanelContrast.Equals(panelContrast) &&
            preset.MenuChildIntensity.Equals(menuChildIntensity) &&
            preset.BorderStrength.Equals(borderStrength))
        {
            return preset;
        }

        return preset with
        {
            Theme = theme,
            Effect = effect,
            MaterialIntensity = materialIntensity,
            BlurRadius = blurRadius,
            PanelContrast = panelContrast,
            MenuChildIntensity = menuChildIntensity,
            BorderStrength = borderStrength
        };
    }

    private static double NormalizePercentage(double value, double fallback)
        => double.IsFinite(value) ? Math.Clamp(value, 0, 100) : fallback;

    private static bool TrySaveInternal(JsonStoreFileSet fileSet, UserSettingsDb db)
        => JsonStorePersistence.TryWriteAtomic(fileSet, db, SerializerOptions);
}
