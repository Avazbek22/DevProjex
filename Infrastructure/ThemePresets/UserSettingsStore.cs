using DevProjex.Infrastructure.Persistence;

namespace DevProjex.Infrastructure.ThemePresets;

public sealed class UserSettingsStore(Func<string>? appDataPathProvider = null)
{
    private const int CurrentSchemaVersion = 2;
    private const string FolderName = "DevProjex";
    private const string FileName = "user-settings.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static readonly AppViewSettings DefaultViewSettings = new()
    {
        IsCompactMode = false,
        IsTreeAnimationEnabled = false,
        IsAdvancedIgnoreCountsEnabled = true,
        PreferredLanguage = null
    };
    private readonly Func<string> _appDataPathProvider =
        appDataPathProvider ?? (() => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
    private readonly object _sync = new();

    public UserSettingsDb Load()
    {
        lock (_sync)
        {
            var fileSet = GetFileSet();
            if (!CrossProcessFileLock.TryAcquire(fileSet, out var heldLock))
                return CreateDefaultDb();

            using var _ = heldLock;
            return LoadInternal(fileSet);
        }
    }

    public void Save(UserSettingsDb db) => TrySave(db);

    public ThemePreset GetPreset(UserSettingsDb db, ThemeVariant theme, ThemeEffectMode effect)
    {
        var key = GetKey(theme, effect);
        if (db.Presets.TryGetValue(key, out var preset) && preset is not null)
            return preset;

        var created = CreateDefaultPreset(theme, effect);
        db.Presets[key] = created;
        return created;
    }

    public void SetPreset(UserSettingsDb db, ThemeVariant theme, ThemeEffectMode effect, ThemePreset preset)
    {
        var key = GetKey(theme, effect);
        db.Presets[key] = preset;
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

        if (!Enum.TryParse(parts[0], true, out ThemeVariant parsedTheme))
            return false;

        if (!Enum.TryParse(parts[1], true, out ThemeEffectMode parsedEffect))
            return false;

        theme = parsedTheme;
        effect = parsedEffect;
        return true;
    }

    private UserSettingsDb Normalize(UserSettingsDb db)
    {
        db.SchemaVersion = CurrentSchemaVersion;
        db.Presets ??= new Dictionary<string, ThemePreset>();
        db.ViewSettings ??= DefaultViewSettings;

        foreach (var preset in CreateDefaultPresets())
        {
            if (!db.Presets.ContainsKey(preset.Key))
                db.Presets[preset.Key] = preset.Value;
        }

        if (string.IsNullOrWhiteSpace(db.LastSelected) || !db.Presets.ContainsKey(db.LastSelected))
            db.LastSelected = GetKey(ThemeVariant.Dark, ThemeEffectMode.Transparent);

        return db;
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

    private Dictionary<string, ThemePreset> CreateDefaultPresets()
    {
        return new Dictionary<string, ThemePreset>(StringComparer.OrdinalIgnoreCase)
        {
            [GetKey(ThemeVariant.Light, ThemeEffectMode.Transparent)] = new ThemePreset
            {
                Theme = ThemeVariant.Light,
                Effect = ThemeEffectMode.Transparent,
                MaterialIntensity = 78.43450479233228,
                BlurRadius = 30,
                PanelContrast = 0,
                MenuChildIntensity = 0,
                BorderStrength = 53.19488817891374
            },
            [GetKey(ThemeVariant.Light, ThemeEffectMode.Mica)] = new ThemePreset
            {
                Theme = ThemeVariant.Light,
                Effect = ThemeEffectMode.Mica,
                MaterialIntensity = 100,
                BlurRadius = 30,
                PanelContrast = 0,
                MenuChildIntensity = 0,
                BorderStrength = 57.66773162939298
            },
            [GetKey(ThemeVariant.Light, ThemeEffectMode.Acrylic)] = new ThemePreset
            {
                Theme = ThemeVariant.Light,
                Effect = ThemeEffectMode.Acrylic,
                MaterialIntensity = 75.87859424920129,
                BlurRadius = 30,
                PanelContrast = 0,
                MenuChildIntensity = 0,
                BorderStrength = 100
            },
            [GetKey(ThemeVariant.Dark, ThemeEffectMode.Transparent)] = new ThemePreset
            {
                Theme = ThemeVariant.Dark,
                Effect = ThemeEffectMode.Transparent,
                MaterialIntensity = 60.86261980830672,
                BlurRadius = 29.233226837060705,
                PanelContrast = 51.59744408945688,
                MenuChildIntensity = 0,
                BorderStrength = 31.789137380191697
            },
            [GetKey(ThemeVariant.Dark, ThemeEffectMode.Mica)] = new ThemePreset
            {
                Theme = ThemeVariant.Dark,
                Effect = ThemeEffectMode.Mica,
                MaterialIntensity = 100,
                BlurRadius = 30,
                PanelContrast = 0,
                MenuChildIntensity = 0,
                BorderStrength = 35.94249201277955
            },
            [GetKey(ThemeVariant.Dark, ThemeEffectMode.Acrylic)] = new ThemePreset
            {
                Theme = ThemeVariant.Dark,
                Effect = ThemeEffectMode.Acrylic,
                MaterialIntensity = 73.00319488817892,
                BlurRadius = 30,
                PanelContrast = 0,
                MenuChildIntensity = 0,
                BorderStrength = 26.677316293929714
            }
        };
    }

    private ThemePreset CreateDefaultPreset(ThemeVariant theme, ThemeEffectMode effect)
    {
        var defaults = CreateDefaultPresets();
        return defaults[GetKey(theme, effect)];
    }

    private string GetKey(ThemeVariant theme, ThemeEffectMode effect) => $"{theme}.{effect}";

    private void TrySave(UserSettingsDb db)
    {
        lock (_sync)
        {
            try
            {
                var fileSet = GetFileSet();
                if (!CrossProcessFileLock.TryAcquire(fileSet, out var heldLock))
                    return;

                using var _ = heldLock;
                TrySaveInternal(fileSet, Normalize(db));
            }
            catch
            {
                // Ignore persistence errors.
            }
        }
    }

    private static bool TrySaveInternal(JsonStoreFileSet fileSet, UserSettingsDb db)
        => JsonStorePersistence.TryWriteAtomic(fileSet, db, SerializerOptions);
}
