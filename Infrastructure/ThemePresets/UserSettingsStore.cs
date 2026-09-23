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

    public bool TryLoad(out UserSettingsDb database)
    {
        lock (_sync)
        {
            database = CreateDefaultDb();
            try
            {
                var fileSet = GetFileSet();
                if (!CrossProcessFileLock.TryAcquire(fileSet, out var heldLock))
                    return false;

                using var _ = heldLock;
                database = LoadInternal(fileSet, out var temporarilyUnavailable);
                return !temporarilyUnavailable;
            }
            catch
            {
                database = CreateDefaultDb();
                return false;
            }
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

    public bool TryPersistViewSettings(UserSettingsDb database) =>
        TryPersistViewSettings(database, _ => database.ViewSettings);

    public bool TryPersistViewSettings(
        UserSettingsDb database,
        Func<AppViewSettings, AppViewSettings> applyChanges)
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
                var latest = LoadInternal(fileSet, out var temporarilyUnavailable);
                if (temporarilyUnavailable)
                    return false;
                latest.ViewSettings = NormalizeViewSettings(applyChanges(latest.ViewSettings));
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

    public bool TryPersistUpdateCheckSettings(UserSettingsDb database) =>
        TryPersistUpdateCheckSettings(database, _ => database.UpdateCheckSettings);

    public bool TryPersistUpdateCheckSettings(
        UserSettingsDb database,
        Func<UpdateCheckSettings, UpdateCheckSettings> applyChanges)
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
                var latest = LoadInternal(fileSet, out var temporarilyUnavailable);
                if (temporarilyUnavailable)
                    return false;
                latest.UpdateCheckSettings = NormalizeUpdateCheckSettings(
                    applyChanges(latest.UpdateCheckSettings));
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
        StoredViewPreferences storedPreferences)
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
                storedPreferences.StatusMetricsAnimation ?? true,
			IsToolAnimationEnabled = storedPreferences.ToolAnimation ?? true
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

    private UserSettingsDb LoadInternal(JsonStoreFileSet fileSet) =>
        LoadInternal(fileSet, out _);

    private UserSettingsDb LoadInternal(JsonStoreFileSet fileSet, out bool temporarilyUnavailable)
    {
        temporarilyUnavailable = false;
        if (HasFutureSchema(fileSet))
            return CreateDefaultDb();

        var primaryStatus = TryRead(fileSet.PrimaryPath, out var primary, out var primaryRequiresRewrite);
        if (primaryStatus == SettingsReadStatus.TemporarilyUnavailable)
        {
            temporarilyUnavailable = true;
            return CreateDefaultDb();
        }
        if (primaryStatus == SettingsReadStatus.Loaded)
        {
            if (primaryRequiresRewrite)
                TrySaveInternal(fileSet, primary);
            return primary;
        }

        var backupStatus = TryRead(fileSet.BackupPath, out var backup, out _);
        if (backupStatus == SettingsReadStatus.TemporarilyUnavailable)
        {
            temporarilyUnavailable = true;
            return CreateDefaultDb();
        }
        if (backupStatus == SettingsReadStatus.Loaded)
        {
            TrySaveInternal(fileSet, backup);
            return backup;
        }

        var fallback = CreateDefaultDb();
        if (File.Exists(fileSet.PrimaryPath) || File.Exists(fileSet.BackupPath))
            TrySaveInternal(fileSet, fallback);
        return fallback;
    }

    private SettingsReadStatus TryRead(string path, out UserSettingsDb database, out bool requiresRewrite)
    {
        var storedPreferences = ReadStoredViewPreferences(path, out var preferencesUnavailable);
        var loaded = JsonStorePersistence.TryReadNormalized(
            path,
            SerializerOptions,
            CreateDefaultDb,
            value => NormalizeAfterRead(value, storedPreferences),
            out database,
            out requiresRewrite,
            out var temporarilyUnavailable,
            JsonStorePersistence.SmallDocumentMaximumBytes,
            IsValidCurrentSchemaDocument);
        if (preferencesUnavailable || temporarilyUnavailable)
            return SettingsReadStatus.TemporarilyUnavailable;
        return loaded
            ? SettingsReadStatus.Loaded
            : SettingsReadStatus.MissingOrInvalid;
    }

    private static bool IsValidCurrentSchemaDocument(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("schemaVersion", out var schemaVersion) ||
            !schemaVersion.TryGetInt32(out var version) ||
            version != CurrentSchemaVersion)
        {
            return true;
        }

        return root.TryGetProperty("viewSettings", out var viewSettings) &&
               viewSettings.ValueKind == JsonValueKind.Object;
    }

    private static StoredViewPreferences ReadStoredViewPreferences(string path, out bool temporarilyUnavailable)
    {
        temporarilyUnavailable = false;
        try
        {
            if (!JsonStorePersistence.TryReadAllTextWithinSizeLimit(
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

            return new StoredViewPreferences(
                ReadOptionalBoolean(viewSettings, "isStatusMetricsAnimationEnabled"),
                ReadOptionalBoolean(viewSettings, "isToolAnimationEnabled"));
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return default;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          System.Security.SecurityException)
        {
            temporarilyUnavailable = true;
            return default;
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

        var primaryStatus = TryRead(fileSet.PrimaryPath, out var primary, out var primaryRequiresRewrite);
        if (primaryStatus == SettingsReadStatus.TemporarilyUnavailable)
            return false;
        if (primaryStatus == SettingsReadStatus.Loaded)
        {
            if (primaryRequiresRewrite || !File.Exists(fileSet.BackupPath))
                return TrySaveInternal(fileSet, primary);
            return true;
        }

        var backupStatus = TryRead(fileSet.BackupPath, out var backup, out _);
        if (backupStatus == SettingsReadStatus.TemporarilyUnavailable)
            return false;
        if (backupStatus == SettingsReadStatus.Loaded)
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

    private readonly record struct StoredViewPreferences(
        bool? StatusMetricsAnimation,
		bool? ToolAnimation);

    private enum SettingsReadStatus
    {
        MissingOrInvalid,
        Loaded,
        TemporarilyUnavailable
    }
}
