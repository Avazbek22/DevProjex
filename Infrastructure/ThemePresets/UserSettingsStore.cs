using DevProjex.Infrastructure.Persistence;

namespace DevProjex.Infrastructure.ThemePresets;

public sealed class UserSettingsStore(Func<string>? appDataPathProvider = null)
{
    private const int CurrentSchemaVersion = 9;
    private const string FolderName = "DevProjex";
    private const string FileName = "user-settings.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        TypeInfoResolver = InfrastructureJsonSerializerContext.Default,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static readonly AppViewSettings DefaultViewSettings = new();
    private readonly Func<string> _appDataPathProvider =
        appDataPathProvider ?? UserDataPathResolver.GetConfigurationRoot;
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
                return CreateDefaultDb();
            }
        }
    }

    public void Save(UserSettingsDb database) => TrySave(database);

    public bool TrySave(UserSettingsDb database)
    {
        lock (_sync)
        {
            try
            {
                var fileSet = GetFileSet();
                if (!CrossProcessFileLock.TryAcquire(fileSet, out var heldLock))
                    return false;

                using var _ = heldLock;
                if (HasFutureSchema(fileSet))
                    return false;
                return TrySaveInternal(fileSet, Normalize(database));
            }
            catch
            {
                return false;
            }
        }
    }

    public bool TryPersistViewSettings(UserSettingsDb database)
    {
        lock (_sync)
        {
            try
            {
                var fileSet = GetFileSet();
                if (!CrossProcessFileLock.TryAcquire(fileSet, out var heldLock))
                    return false;

                using var _ = heldLock;
                if (HasFutureSchema(fileSet))
                    return false;
                var latest = LoadInternal(fileSet);
                latest.ViewSettings = NormalizeViewSettings(database.ViewSettings);
                if (!TrySaveInternal(fileSet, Normalize(latest)))
                    return false;

                database.SchemaVersion = latest.SchemaVersion;
                database.ViewSettings = latest.ViewSettings;
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    public bool TryPersistUpdateCheckSettings(UserSettingsDb database)
    {
        lock (_sync)
        {
            try
            {
                var fileSet = GetFileSet();
                if (!CrossProcessFileLock.TryAcquire(fileSet, out var heldLock))
                    return false;

                using var _ = heldLock;
                if (HasFutureSchema(fileSet))
                    return false;
                var latest = LoadInternal(fileSet);
                latest.UpdateCheckSettings = NormalizeUpdateCheckSettings(
                    database.UpdateCheckSettings);
                if (!TrySaveInternal(fileSet, Normalize(latest)))
                    return false;

                database.SchemaVersion = latest.SchemaVersion;
                database.UpdateCheckSettings = latest.UpdateCheckSettings;
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    public string GetPath() => GetFileSet().PrimaryPath;

    private UserSettingsDb Normalize(UserSettingsDb database)
    {
        database.SchemaVersion = CurrentSchemaVersion;
        database.ViewSettings = NormalizeViewSettings(database.ViewSettings);
        database.UpdateCheckSettings = NormalizeUpdateCheckSettings(
            database.UpdateCheckSettings);
        return database;
    }

    private UserSettingsDb NormalizeAfterRead(
        UserSettingsDb database,
        StoredAnimationPreferences animationPreferences)
    {
        var sourceSchemaVersion = database.SchemaVersion;
        Normalize(database);

        if (sourceSchemaVersion < 7)
        {
            // Schema 6 stored a hover-only row translation under a similarly named
            // property. It is intentionally not a preference for the new chevron and
            // branch expansion motion, so upgrades receive the new v7 default.
            database.ViewSettings = database.ViewSettings with
            {
                IsTreeExpansionAnimationEnabled = true
            };
        }

        database.ViewSettings = database.ViewSettings with
        {
            IsStatusMetricsAnimationEnabled =
                animationPreferences.StatusMetrics ?? true,
            IsToolAnimationEnabled = animationPreferences.Tools ?? true
        };

        return database;
    }

    private static AppViewSettings NormalizeViewSettings(AppViewSettings? settings)
    {
        settings ??= DefaultViewSettings;
        return Enum.IsDefined(settings.PreferredLanguage ?? AppLanguage.En)
            ? settings
            : settings with { PreferredLanguage = null };
    }

    private static UpdateCheckSettings NormalizeUpdateCheckSettings(
        UpdateCheckSettings? settings)
    {
        settings ??= new UpdateCheckSettings();
        var latestKnownVersion = NormalizeStoredVersion(settings.LatestKnownVersion);
        var lastNotifiedVersion = NormalizeStoredVersion(settings.LastNotifiedVersion);

        return settings with
        {
            LatestKnownVersion = latestKnownVersion,
            LastNotifiedVersion = lastNotifiedVersion
        };
    }

    private static string NormalizeStoredVersion(string? version)
    {
        var normalized = version?.Trim() ?? string.Empty;
        return normalized.Length <= 64 ? normalized : string.Empty;
    }

    private JsonStoreFileSet GetFileSet()
        => JsonStoreFileSet.Create(_appDataPathProvider, FolderName, FileName);

    private UserSettingsDb LoadInternal(JsonStoreFileSet fileSet)
    {
        if (HasFutureSchema(fileSet))
            return CreateDefaultDb();

        if (TryRead(fileSet.PrimaryPath, out var primary, out var primaryRequiresRewrite))
        {
            if (primaryRequiresRewrite)
                TrySaveInternal(fileSet, primary);
            return primary;
        }

        if (TryRead(fileSet.BackupPath, out var backup, out _))
        {
            TrySaveInternal(fileSet, backup);
            return backup;
        }

        var fallback = CreateDefaultDb();
        if (File.Exists(fileSet.PrimaryPath) || File.Exists(fileSet.BackupPath))
            TrySaveInternal(fileSet, fallback);
        return fallback;
    }

    private bool TryRead(string path, out UserSettingsDb database, out bool requiresRewrite)
    {
        var animationPreferences = ReadStoredAnimationPreferences(path);
        return JsonStorePersistence.TryReadNormalized(
            path,
            SerializerOptions,
            CreateDefaultDb,
            value => NormalizeAfterRead(value, animationPreferences),
            out database,
            out requiresRewrite,
            JsonStorePersistence.SmallDocumentMaximumBytes);
    }

    private static StoredAnimationPreferences ReadStoredAnimationPreferences(string path)
    {
        try
        {
            if (!File.Exists(path) ||
                !JsonStorePersistence.TryReadAllTextWithinSizeLimit(
                    path,
                    checked((int)JsonStorePersistence.SmallDocumentMaximumBytes),
                    out var json))
            {
                return default;
            }

            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("viewSettings", out var viewSettings) ||
                viewSettings.ValueKind != JsonValueKind.Object)
            {
                return default;
            }

            return new StoredAnimationPreferences(
                ReadOptionalBoolean(viewSettings, "isStatusMetricsAnimationEnabled"),
                ReadOptionalBoolean(viewSettings, "isToolAnimationEnabled"));
        }
        catch
        {
            return default;
        }
    }

    private static bool? ReadOptionalBoolean(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private bool EnsureStorageExistsCore(JsonStoreFileSet fileSet)
    {
        if (HasFutureSchema(fileSet))
            return true;

        if (TryRead(fileSet.PrimaryPath, out var primary, out var primaryRequiresRewrite))
        {
            if (primaryRequiresRewrite || !File.Exists(fileSet.BackupPath))
                return TrySaveInternal(fileSet, primary);
            return true;
        }

        if (TryRead(fileSet.BackupPath, out var backup, out _))
            return TrySaveInternal(fileSet, backup);

        if (File.Exists(fileSet.PrimaryPath) || File.Exists(fileSet.BackupPath))
            return false;

        return TrySaveInternal(fileSet, CreateDefaultDb());
    }

    private static UserSettingsDb CreateDefaultDb() => new()
    {
        SchemaVersion = CurrentSchemaVersion,
        ViewSettings = DefaultViewSettings,
        UpdateCheckSettings = new UpdateCheckSettings()
    };

    private static bool HasFutureSchema(JsonStoreFileSet fileSet) =>
        JsonStorePersistence.ContainsFutureDocument(
            fileSet,
            CurrentSchemaVersion,
            maximumDocumentBytes: JsonStorePersistence.SmallDocumentMaximumBytes);

    private static bool TrySaveInternal(JsonStoreFileSet fileSet, UserSettingsDb database)
        => JsonStorePersistence.TryWriteAtomic(fileSet, database, SerializerOptions);

    private readonly record struct StoredAnimationPreferences(
        bool? StatusMetrics,
        bool? Tools);
}
