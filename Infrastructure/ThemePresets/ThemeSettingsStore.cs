using DevProjex.Infrastructure.Persistence;

namespace DevProjex.Infrastructure.ThemePresets;

public sealed class ThemeSettingsStore(Func<string>? appDataPathProvider = null)
{
    public const int CurrentSchemaVersion = 1;
    public const int CurrentDefaultsRevision = 1;

    private const string FolderName = "DevProjex";
    private const string FileName = "theme-settings.json";
    private const string DefaultSelectedPreset = "Dark.Acrylic";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        TypeInfoResolver = InfrastructureJsonSerializerContext.Default,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static readonly IReadOnlyDictionary<string, ThemePreset> DefaultPresets = CreateDefaultPresets();
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

    public ThemeSettingsDocument Load()
    {
        lock (_sync)
        {
            var fileSet = GetFileSet();
            if (!CrossProcessFileLock.TryAcquire(fileSet, out var heldLock))
                return CreateFactoryDefaults();

            using var _ = heldLock;
            return LoadInternal(fileSet, persistReset: false);
        }
    }

    public ThemeSettingsDocument LoadForStartup(TimeSpan lockTimeout)
    {
        lock (_sync)
        {
            try
            {
                var fileSet = GetFileSet();
                if (!CrossProcessFileLock.TryAcquire(fileSet, lockTimeout, out var heldLock))
                    return CreateFactoryDefaults();

                using var _ = heldLock;
                return LoadInternal(fileSet, persistReset: true);
            }
            catch
            {
                return CreateFactoryDefaults();
            }
        }
    }

    public ThemePreset GetPreset(ThemeSettingsDocument document, ThemeVariant theme, ThemeEffectMode effect)
    {
        var key = GetKey(theme, effect);
        if (document.Presets.TryGetValue(key, out var preset) && preset is not null)
        {
            var normalized = NormalizePreset(preset, DefaultPresets[key]);
            document.Presets[key] = normalized;
            return normalized;
        }

        var created = DefaultPresets[key];
        document.Presets[key] = created;
        return created;
    }

    public void SetPreset(
        ThemeSettingsDocument document,
        ThemeVariant theme,
        ThemeEffectMode effect,
        ThemePreset preset)
    {
        var key = GetKey(theme, effect);
        document.Presets[key] = NormalizePreset(preset, DefaultPresets[key]);
    }

    public bool TryPersistChanges(
        ThemeSettingsDocument document,
        IReadOnlyCollection<string> changedPresetKeys,
        string selectedPreset)
    {
        lock (_sync)
        {
            try
            {
                var fileSet = GetFileSet();
                if (!CrossProcessFileLock.TryAcquire(fileSet, out var heldLock))
                    return false;

                using var _ = heldLock;
                if (ContainsFutureDocument(fileSet))
                    return false;
                var latest = LoadInternal(fileSet, persistReset: false);
                foreach (var key in changedPresetKeys.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (!TryParseKey(key, out var theme, out var effect) ||
                        !document.Presets.TryGetValue(key, out var preset) ||
                        preset is null)
                    {
                        continue;
                    }

                    SetPreset(latest, theme, effect, preset);
                }

                if (TryParseKey(selectedPreset, out var selectedTheme, out var selectedEffect))
                    latest.SelectedPreset = GetKey(selectedTheme, selectedEffect);

                latest = NormalizeCurrent(latest);
                if (!TrySaveInternal(fileSet, latest))
                    return false;

                CopyDocument(latest, document);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    public ThemeSettingsDocument ResetToDefaults()
    {
        var defaults = CreateFactoryDefaults();
        TrySave(defaults);
        return defaults;
    }

    public bool TrySave(ThemeSettingsDocument document)
    {
        lock (_sync)
        {
            try
            {
                var fileSet = GetFileSet();
                if (!CrossProcessFileLock.TryAcquire(fileSet, out var heldLock))
                    return false;

                using var _ = heldLock;
                if (ContainsFutureDocument(fileSet))
                    return false;
                return TrySaveInternal(fileSet, NormalizeCurrent(document));
            }
            catch
            {
                return false;
            }
        }
    }

    public string GetPath() => GetFileSet().PrimaryPath;

    public bool TryParseKey(string? key, out ThemeVariant theme, out ThemeEffectMode effect)
    {
        theme = ThemeVariant.Dark;
        effect = ThemeEffectMode.Acrylic;
        if (string.IsNullOrWhiteSpace(key))
            return false;

        var parts = key.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2 ||
            !Enum.TryParse(parts[0], true, out ThemeVariant parsedTheme) || !Enum.IsDefined(parsedTheme) ||
            !Enum.TryParse(parts[1], true, out ThemeEffectMode parsedEffect) || !Enum.IsDefined(parsedEffect))
        {
            return false;
        }

        theme = parsedTheme;
        effect = parsedEffect;
        return true;
    }

    private ThemeSettingsDocument LoadInternal(JsonStoreFileSet fileSet, bool persistReset)
    {
        if (ContainsFutureDocument(fileSet))
            return CreateFactoryDefaults();

        var primaryStatus = TryReadCurrent(fileSet.PrimaryPath, out var primary, out var primaryRequiresRewrite);
        if (primaryStatus == ThemeDocumentReadStatus.Current)
        {
            if (primaryRequiresRewrite)
                TrySaveInternal(fileSet, primary);
            return primary;
        }

        if (primaryStatus == ThemeDocumentReadStatus.Future)
            return CreateFactoryDefaults();

        if (primaryStatus == ThemeDocumentReadStatus.Obsolete)
            return ResetObsoleteDocument(fileSet, persistReset);

        var backupStatus = TryReadCurrent(fileSet.BackupPath, out var backup, out _);
        if (backupStatus == ThemeDocumentReadStatus.Current)
        {
            if (persistReset)
                TrySaveInternal(fileSet, backup);
            return backup;
        }

        if (backupStatus == ThemeDocumentReadStatus.Future)
            return CreateFactoryDefaults();

        return ResetObsoleteDocument(fileSet, persistReset);
    }

    private ThemeSettingsDocument ResetObsoleteDocument(JsonStoreFileSet fileSet, bool persistReset)
    {
        var defaults = CreateFactoryDefaults();
        if (persistReset)
            TrySaveInternal(fileSet, defaults);
        return defaults;
    }

    private ThemeDocumentReadStatus TryReadCurrent(
        string path,
        out ThemeSettingsDocument document,
        out bool requiresRewrite)
    {
        document = CreateFactoryDefaults();
        requiresRewrite = false;
        if (!File.Exists(path))
            return ThemeDocumentReadStatus.MissingOrInvalid;

        try
        {
            var json = File.ReadAllText(path);
            var deserialized = JsonSerializer.Deserialize<ThemeSettingsDocument>(json, SerializerOptions);
            if (deserialized is null)
                return ThemeDocumentReadStatus.MissingOrInvalid;

            if (deserialized.SchemaVersion > CurrentSchemaVersion ||
                deserialized.DefaultsRevision > CurrentDefaultsRevision)
            {
                return ThemeDocumentReadStatus.Future;
            }

            if (deserialized.SchemaVersion != CurrentSchemaVersion ||
                deserialized.DefaultsRevision != CurrentDefaultsRevision)
            {
                return ThemeDocumentReadStatus.Obsolete;
            }

            var original = JsonSerializer.Serialize(deserialized, SerializerOptions);
            document = NormalizeCurrent(deserialized);
            requiresRewrite = !string.Equals(
                original,
                JsonSerializer.Serialize(document, SerializerOptions),
                StringComparison.Ordinal);
            return ThemeDocumentReadStatus.Current;
        }
        catch
        {
            return ThemeDocumentReadStatus.MissingOrInvalid;
        }
    }

    private bool EnsureStorageExistsCore(JsonStoreFileSet fileSet)
    {
        if (ContainsFutureDocument(fileSet))
            return true;

        var status = TryReadCurrent(fileSet.PrimaryPath, out var primary, out var requiresRewrite);
        if (status == ThemeDocumentReadStatus.Future)
            return true;
        if (status == ThemeDocumentReadStatus.Current)
        {
            if (requiresRewrite || !File.Exists(fileSet.BackupPath))
                return TrySaveInternal(fileSet, primary);
            return true;
        }

        return TrySaveInternal(fileSet, CreateFactoryDefaults());
    }

    private static ThemeSettingsDocument NormalizeCurrent(ThemeSettingsDocument document)
    {
        document.SchemaVersion = CurrentSchemaVersion;
        document.DefaultsRevision = CurrentDefaultsRevision;
        document.Presets = new Dictionary<string, ThemePreset>(
            document.Presets ?? new Dictionary<string, ThemePreset>(),
            StringComparer.OrdinalIgnoreCase);

        foreach (var pair in DefaultPresets)
        {
            document.Presets[pair.Key] = document.Presets.TryGetValue(pair.Key, out var current) && current is not null
                ? NormalizePreset(current, pair.Value)
                : pair.Value;
        }

        foreach (var unknownKey in document.Presets.Keys.Where(key => !DefaultPresets.ContainsKey(key)).ToArray())
            document.Presets.Remove(unknownKey);

        if (!TryParseKeyStatic(document.SelectedPreset, out var selectedTheme, out var selectedEffect))
            document.SelectedPreset = DefaultSelectedPreset;
        else
            document.SelectedPreset = GetKey(selectedTheme, selectedEffect);

        return document;
    }

    private static ThemePreset NormalizePreset(ThemePreset preset, ThemePreset fallback) => new()
    {
        BackgroundTransparency = NormalizePercentage(preset.BackgroundTransparency, fallback.BackgroundTransparency),
        PanelContrast = NormalizePercentage(preset.PanelContrast, fallback.PanelContrast),
        MenuTransparency = NormalizePercentage(preset.MenuTransparency, fallback.MenuTransparency),
        BorderVisibility = NormalizePercentage(preset.BorderVisibility, fallback.BorderVisibility)
    };

    private static double NormalizePercentage(double value, double fallback)
        => double.IsFinite(value) ? Math.Clamp(value, 0, 100) : fallback;

    private static ThemeSettingsDocument CreateFactoryDefaults() => new()
    {
        SchemaVersion = CurrentSchemaVersion,
        DefaultsRevision = CurrentDefaultsRevision,
        Presets = new Dictionary<string, ThemePreset>(DefaultPresets, StringComparer.OrdinalIgnoreCase),
        SelectedPreset = DefaultSelectedPreset
    };

    private static IReadOnlyDictionary<string, ThemePreset> CreateDefaultPresets()
        => new Dictionary<string, ThemePreset>(StringComparer.OrdinalIgnoreCase)
        {
            ["Light.Transparent"] = new()
            {
                BackgroundTransparency = 100,
                PanelContrast = 24.198717948717942,
                MenuTransparency = 0,
                BorderVisibility = 49.19871794871795
            },
            ["Light.Solid"] = new()
            {
                BackgroundTransparency = 78.43450479233228,
                PanelContrast = 0,
                MenuTransparency = 0,
                BorderVisibility = 71.31410256410255
            },
            ["Light.Mica"] = new()
            {
                BackgroundTransparency = 100,
                PanelContrast = 0,
                MenuTransparency = 0,
                BorderVisibility = 47.91666666666667
            },
            ["Light.Acrylic"] = new()
            {
                BackgroundTransparency = 100,
                PanelContrast = 14.903846153846153,
                MenuTransparency = 0,
                BorderVisibility = 61.69871794871794
            },
            ["Dark.Transparent"] = new()
            {
                BackgroundTransparency = 39.58333333333333,
                PanelContrast = 53.68589743589743,
                MenuTransparency = 0,
                BorderVisibility = 31.789137380191697
            },
            ["Dark.Solid"] = new()
            {
                BackgroundTransparency = 60.86261980830672,
                PanelContrast = 51.59744408945688,
                MenuTransparency = 0,
                BorderVisibility = 28.36538461538461
            },
            ["Dark.Mica"] = new()
            {
                BackgroundTransparency = 100,
                PanelContrast = 0,
                MenuTransparency = 49.19871794871795,
                BorderVisibility = 23.557692307692303
            },
            ["Dark.Acrylic"] = new()
            {
                BackgroundTransparency = 84.77564102564102,
                PanelContrast = 7.532051282051282,
                MenuTransparency = 46.9551282051282,
                BorderVisibility = 19.71153846153846
            }
        };

    private JsonStoreFileSet GetFileSet()
        => JsonStoreFileSet.Create(_appDataPathProvider, FolderName, FileName);

    private static string GetKey(ThemeVariant theme, ThemeEffectMode effect) => $"{theme}.{effect}";

    private static bool ContainsFutureDocument(JsonStoreFileSet fileSet) =>
        JsonStorePersistence.ContainsFutureDocument(
            fileSet,
            CurrentSchemaVersion,
            CurrentDefaultsRevision);

    private static bool TryParseKeyStatic(string? key, out ThemeVariant theme, out ThemeEffectMode effect)
    {
        theme = ThemeVariant.Dark;
        effect = ThemeEffectMode.Acrylic;
        if (string.IsNullOrWhiteSpace(key))
            return false;

        var parts = key.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2 ||
            !Enum.TryParse(parts[0], true, out theme) || !Enum.IsDefined(theme) ||
            !Enum.TryParse(parts[1], true, out effect) || !Enum.IsDefined(effect))
        {
            return false;
        }

        return true;
    }

    private static void CopyDocument(ThemeSettingsDocument source, ThemeSettingsDocument destination)
    {
        destination.SchemaVersion = source.SchemaVersion;
        destination.DefaultsRevision = source.DefaultsRevision;
        destination.Presets = source.Presets;
        destination.SelectedPreset = source.SelectedPreset;
    }

    private static bool TrySaveInternal(JsonStoreFileSet fileSet, ThemeSettingsDocument document)
        => JsonStorePersistence.TryWriteAtomic(fileSet, document, SerializerOptions);

    private enum ThemeDocumentReadStatus
    {
        MissingOrInvalid,
        Obsolete,
        Current,
        Future
    }
}
