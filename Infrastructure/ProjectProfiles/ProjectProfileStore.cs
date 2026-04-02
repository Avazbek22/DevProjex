using DevProjex.Infrastructure.Persistence;

namespace DevProjex.Infrastructure.ProjectProfiles;

public sealed class ProjectProfileStore(Func<string>? appDataPathProvider = null) : IProjectProfileStore
{
	private const int CurrentSchemaVersion = 1;
	private const int MaxProfiles = 500;
	private const string FolderName = "DevProjex";
	private const string FileName = "project-profiles.json";

	private static readonly JsonSerializerOptions SerializerOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		WriteIndented = true,
		Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
	};

	private readonly object _sync = new();
    private readonly Func<string> _appDataPathProvider = appDataPathProvider ?? (() => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));

    public bool TryLoadProfile(string localProjectPath, out ProjectSelectionProfile profile)
	{
		profile = new ProjectSelectionProfile(
			SelectedRootFolders: [],
			SelectedExtensions: [],
			SelectedIgnoreOptions: []);

		if (!TryNormalizePath(localProjectPath, out var normalizedPath))
			return false;

		lock (_sync)
		{
			var fileSet = GetFileSet();
			if (!CrossProcessFileLock.TryAcquire(fileSet, out var heldLock))
				return false;

			using var _ = heldLock;
			var db = LoadInternal(fileSet);
			if (db.Profiles.Count == 0)
				return false;

			if (!db.Profiles.TryGetValue(normalizedPath, out var entry) || entry is null)
				return false;

			profile = ToProfile(entry);
			return true;
		}
	}

	public void SaveProfile(string localProjectPath, ProjectSelectionProfile profile)
		=> TrySaveProfile(localProjectPath, profile, DateTimeOffset.UtcNow);

	public bool TrySaveProfile(string localProjectPath, ProjectSelectionProfile profile)
		=> TrySaveProfile(localProjectPath, profile, DateTimeOffset.UtcNow);

	public bool TrySaveProfile(string localProjectPath, ProjectSelectionProfile profile, DateTimeOffset updatedUtc)
	{
		if (!TryNormalizePath(localProjectPath, out var normalizedPath))
			return false;

		lock (_sync)
		{
			var fileSet = GetFileSet();
			if (!CrossProcessFileLock.TryAcquire(fileSet, out var heldLock))
				return false;

			using var _ = heldLock;
			var db = LoadInternal(fileSet);
			db.SchemaVersion = CurrentSchemaVersion;

			// A delayed retry from another window/process must not stomp a newer profile revision.
			// The caller-provided timestamp reflects when the profile became user-approved.
			if (db.Profiles.TryGetValue(normalizedPath, out var existing) &&
				existing is not null &&
				existing.UpdatedUtc > updatedUtc)
			{
				return true;
			}

			db.Profiles[normalizedPath] = ToPersistedProfile(profile, updatedUtc);
			PruneProfiles(db);
			return TrySaveInternal(fileSet, db);
		}
	}

	public void ClearAllProfiles()
	{
		lock (_sync)
		{
			try
			{
				var fileSet = GetFileSet();
				if (!CrossProcessFileLock.TryAcquire(fileSet, out var heldLock))
					return;

				using var _ = heldLock;
				if (File.Exists(fileSet.PrimaryPath))
					File.Delete(fileSet.PrimaryPath);
				if (File.Exists(fileSet.BackupPath))
					File.Delete(fileSet.BackupPath);
			}
			catch
			{
				// Best effort: the app must stay stable even if persistence cleanup fails.
			}
		}
	}

	public string GetPath()
	{
		return GetFileSet().PrimaryPath;
	}

	private JsonStoreFileSet GetFileSet()
		=> JsonStoreFileSet.Create(_appDataPathProvider, FolderName, FileName);

	private ProjectProfileDb LoadInternal(JsonStoreFileSet fileSet)
	{
		if (TryLoadFromPath(fileSet.PrimaryPath, out var primaryDb, out var primaryRequiresRewrite))
		{
			if (primaryRequiresRewrite)
				TrySaveInternal(fileSet, primaryDb);

			return primaryDb;
		}

		// Recover from the last known-good snapshot when the primary profile document becomes unreadable.
		if (TryLoadFromPath(fileSet.BackupPath, out var backupDb, out _))
		{
			TrySaveInternal(fileSet, backupDb);
			return backupDb;
		}

		return CreateDefaultDb();
	}

	private bool TrySaveInternal(JsonStoreFileSet fileSet, ProjectProfileDb db)
	{
		return JsonStorePersistence.TryWriteAtomic(fileSet, db, SerializerOptions);
	}

	private static ProjectProfileDb CreateDefaultDb()
	{
		return new ProjectProfileDb
		{
			SchemaVersion = CurrentSchemaVersion,
			Profiles = new Dictionary<string, PersistedProjectProfile>(PathComparer.Default)
		};
	}

	private static ProjectProfileDb Normalize(ProjectProfileDb db)
	{
		db.SchemaVersion = CurrentSchemaVersion;
		db.Profiles ??= new Dictionary<string, PersistedProjectProfile>(PathComparer.Default);

		var normalized = new Dictionary<string, PersistedProjectProfile>(PathComparer.Default);
		foreach (var (key, value) in db.Profiles)
		{
			if (!TryNormalizePath(key, out var normalizedPath))
				continue;

			if (value is null)
				continue;

			normalized[normalizedPath] = NormalizePersistedProfile(value);
		}

		db.Profiles = normalized;
		return db;
	}

	private static PersistedProjectProfile NormalizePersistedProfile(PersistedProjectProfile profile)
	{
		profile.SelectedRootFolders ??= [];
		profile.SelectedExtensions ??= [];
		profile.SelectedIgnoreOptions ??= [];

		profile.SelectedRootFolders = profile.SelectedRootFolders
			.Where(static item => !string.IsNullOrWhiteSpace(item))
			.Distinct(PathComparer.Default)
			.ToList();
		profile.SelectedExtensions = profile.SelectedExtensions
			.Where(static item => !string.IsNullOrWhiteSpace(item))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToList();
		profile.SelectedIgnoreOptions = profile.SelectedIgnoreOptions
			.Distinct()
			.ToList();

		if (profile.UpdatedUtc <= DateTimeOffset.UnixEpoch)
			profile.UpdatedUtc = DateTimeOffset.UtcNow;

		return profile;
	}

	private static PersistedProjectProfile ToPersistedProfile(ProjectSelectionProfile profile, DateTimeOffset updatedUtc)
	{
		return new PersistedProjectProfile
		{
			SelectedRootFolders = profile.SelectedRootFolders
				.Where(static item => !string.IsNullOrWhiteSpace(item))
				.Distinct(PathComparer.Default)
				.ToList(),
			SelectedExtensions = profile.SelectedExtensions
				.Where(static item => !string.IsNullOrWhiteSpace(item))
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.ToList(),
			SelectedIgnoreOptions = profile.SelectedIgnoreOptions
				.Distinct()
				.ToList(),
			UpdatedUtc = updatedUtc
		};
	}

	private static ProjectSelectionProfile ToProfile(PersistedProjectProfile profile)
	{
		var rootFolders = new HashSet<string>(profile.SelectedRootFolders, PathComparer.Default);
		var extensions = new HashSet<string>(profile.SelectedExtensions, StringComparer.OrdinalIgnoreCase);
		var ignoreOptions = new HashSet<IgnoreOptionId>(profile.SelectedIgnoreOptions);

		return new ProjectSelectionProfile(
			SelectedRootFolders: rootFolders,
			SelectedExtensions: extensions,
			SelectedIgnoreOptions: ignoreOptions);
	}

	private static void PruneProfiles(ProjectProfileDb db)
	{
		if (db.Profiles.Count <= MaxProfiles)
			return;

		var staleKeys = db.Profiles
			.OrderBy(pair => pair.Value.UpdatedUtc)
			.Take(db.Profiles.Count - MaxProfiles)
			.Select(pair => pair.Key)
			.ToArray();

		foreach (var key in staleKeys)
			db.Profiles.Remove(key);
	}

	private static bool TryNormalizePath(string input, out string normalizedPath)
	{
		normalizedPath = string.Empty;
		if (string.IsNullOrWhiteSpace(input))
			return false;

		try
		{
			normalizedPath = PathUtility.Normalize(input);
			return !string.IsNullOrWhiteSpace(normalizedPath);
		}
		catch
		{
			return false;
		}
	}

	private static bool TryLoadFromPath(string path, out ProjectProfileDb db, out bool requiresRewrite)
	{
		db = CreateDefaultDb();
		requiresRewrite = false;

		if (!File.Exists(path))
			return false;

		try
		{
			var json = File.ReadAllText(path);
			var deserialized = JsonSerializer.Deserialize<ProjectProfileDb>(json, SerializerOptions);
			if (deserialized is null)
				return false;

			// Only normalize payloads that were parsed successfully.
			// If parsing fails, keep the file untouched and let the backup act as the recovery source.
			var originalSnapshot = JsonSerializer.Serialize(deserialized, SerializerOptions);
			var normalized = Normalize(deserialized);
			var normalizedSnapshot = JsonSerializer.Serialize(normalized, SerializerOptions);
			requiresRewrite = !string.Equals(originalSnapshot, normalizedSnapshot, StringComparison.Ordinal);
			db = normalized;
			return true;
		}
		catch
		{
			return false;
		}
	}

	private sealed class ProjectProfileDb
	{
		public int SchemaVersion { get; set; }
		public Dictionary<string, PersistedProjectProfile> Profiles { get; set; } = new(PathComparer.Default);
	}

	private sealed class PersistedProjectProfile
	{
		public List<string> SelectedRootFolders { get; set; } = [];
		public List<string> SelectedExtensions { get; set; } = [];
		public List<IgnoreOptionId> SelectedIgnoreOptions { get; set; } = [];
		public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
	}
}
