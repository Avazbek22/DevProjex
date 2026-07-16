using DevProjex.Infrastructure.Persistence;

namespace DevProjex.Infrastructure.ThemePresets;

public sealed class UserSettingsStore(Func<string>? appDataPathProvider = null)
{
    private const int CurrentSchemaVersion = 6;
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
                if (HasFutureSchema(fileSet.PrimaryPath))
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
                if (HasFutureSchema(fileSet.PrimaryPath))
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

    public string GetPath() => GetFileSet().PrimaryPath;

    private UserSettingsDb Normalize(UserSettingsDb database)
    {
        database.SchemaVersion = CurrentSchemaVersion;
        database.ViewSettings = NormalizeViewSettings(database.ViewSettings);
        return database;
    }

    private static AppViewSettings NormalizeViewSettings(AppViewSettings? settings)
    {
        settings ??= DefaultViewSettings;
        return Enum.IsDefined(settings.PreferredLanguage ?? AppLanguage.En)
            ? settings
            : settings with { PreferredLanguage = null };
    }

    private JsonStoreFileSet GetFileSet()
        => JsonStoreFileSet.Create(_appDataPathProvider, FolderName, FileName);

    private UserSettingsDb LoadInternal(JsonStoreFileSet fileSet)
    {
        if (HasFutureSchema(fileSet.PrimaryPath))
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
        => JsonStorePersistence.TryReadNormalized(
            path,
            SerializerOptions,
            CreateDefaultDb,
            Normalize,
            out database,
            out requiresRewrite);

    private bool EnsureStorageExistsCore(JsonStoreFileSet fileSet)
    {
        if (HasFutureSchema(fileSet.PrimaryPath))
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
        ViewSettings = DefaultViewSettings
    };

    private static bool HasFutureSchema(string path)
    {
        if (!File.Exists(path))
            return false;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.TryGetProperty("schemaVersion", out var schemaVersion) &&
                   schemaVersion.TryGetInt32(out var value) &&
                   value > CurrentSchemaVersion;
        }
        catch
        {
            return false;
        }
    }

    private static bool TrySaveInternal(JsonStoreFileSet fileSet, UserSettingsDb database)
        => JsonStorePersistence.TryWriteAtomic(fileSet, database, SerializerOptions);
}
