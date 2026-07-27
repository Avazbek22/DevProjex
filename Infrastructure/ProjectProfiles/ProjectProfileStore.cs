using DevProjex.Infrastructure.Persistence;

namespace DevProjex.Infrastructure.ProjectProfiles;

public sealed class ProjectProfileStore(Func<string>? appDataPathProvider = null) : IProjectProfileStore
{
	private const int CurrentSchemaVersion = 3;
	private const int MaxProfiles = 500;
	private const string FolderName = "DevProjex";
	private const string FileName = "project-profiles.json";

	private static readonly JsonSerializerOptions SerializerOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		WriteIndented = true,
		TypeInfoResolver = InfrastructureJsonSerializerContext.Default,
		Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
	};

    private readonly object _sync = new();
    private readonly Func<string> _appDataPathProvider = appDataPathProvider ?? (() => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));

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
			// Reading a missing profile should not materialize persistence files.
			// The app bootstrap explicitly ensures store presence when that contract is needed.
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

	private bool EnsureStorageExistsCore(JsonStoreFileSet fileSet)
	{
		if (TryLoadFromPath(fileSet.PrimaryPath, out var primaryDb, out var primaryRequiresRewrite))
		{
			if (primaryRequiresRewrite || !File.Exists(fileSet.BackupPath))
				return TrySaveInternal(fileSet, primaryDb);

			return true;
		}

		if (TryLoadFromPath(fileSet.BackupPath, out var backupDb, out _))
			return TrySaveInternal(fileSet, backupDb);

		if (File.Exists(fileSet.PrimaryPath) || File.Exists(fileSet.BackupPath))
			return false;

		// Create an empty but durable profile store up front so the app-state surface stays stable
		// even before the first explicit "Apply settings" action persists a project snapshot.
		return TrySaveInternal(fileSet, CreateDefaultDb());
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

	private static ProjectProfileDb Normalize(ProjectProfileDb db, int sourceSchemaVersion)
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

			normalized[normalizedPath] = NormalizePersistedProfile(value, sourceSchemaVersion);
		}

		db.Profiles = normalized;
		return db;
	}

	private static PersistedProjectProfile NormalizePersistedProfile(
		PersistedProjectProfile profile,
		int sourceSchemaVersion)
	{
		profile.SelectedRootFolders ??= [];
		profile.SelectedExtensions ??= [];
		profile.SelectedIgnoreOptions ??= [];
		profile.RootFolderStates = NormalizeStringStateDictionary(profile.RootFolderStates, PathComparer.Default);
		profile.ExtensionStates = NormalizeStringStateDictionary(profile.ExtensionStates, StringComparer.OrdinalIgnoreCase);
		profile.IgnoreOptionStates ??= [];

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

		if (sourceSchemaVersion < 3 &&
		    !profile.IgnoreOptionStates.ContainsKey(IgnoreOptionId.SmartIgnore))
		{
			var gitIgnoreWasEnabled = profile.IgnoreOptionStates.TryGetValue(
				IgnoreOptionId.UseGitIgnore,
				out var persistedGitIgnoreState)
				? persistedGitIgnoreState
				: profile.SelectedIgnoreOptions.Contains(IgnoreOptionId.UseGitIgnore);
			var smartIgnoreWasEnabled =
				profile.SelectedIgnoreOptions.Contains(IgnoreOptionId.SmartIgnore) ||
				gitIgnoreWasEnabled;
			if (smartIgnoreWasEnabled)
			{
				profile.IgnoreOptionStates[IgnoreOptionId.SmartIgnore] = true;
				if (!profile.SelectedIgnoreOptions.Contains(IgnoreOptionId.SmartIgnore))
					profile.SelectedIgnoreOptions.Add(IgnoreOptionId.SmartIgnore);
			}
		}

		NormalizeGitFilteringState(profile.SelectedIgnoreOptions, profile.IgnoreOptionStates);

		if (profile.UpdatedUtc <= DateTimeOffset.UnixEpoch)
			profile.UpdatedUtc = DateTimeOffset.UtcNow;

		return profile;
	}

	private static PersistedProjectProfile ToPersistedProfile(ProjectSelectionProfile profile, DateTimeOffset updatedUtc)
	{
		var selectedIgnoreOptions = profile.SelectedIgnoreOptions
			.Distinct()
			.ToList();
		var ignoreOptionStates = profile.IgnoreOptionStates is null
			? []
			: new Dictionary<IgnoreOptionId, bool>(profile.IgnoreOptionStates);
		NormalizeGitFilteringState(selectedIgnoreOptions, ignoreOptionStates);

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
			SelectedIgnoreOptions = selectedIgnoreOptions,
			RootFolderStates = NormalizeStringStateDictionary(profile.RootFolderStates, PathComparer.Default),
			ExtensionStates = NormalizeStringStateDictionary(profile.ExtensionStates, StringComparer.OrdinalIgnoreCase),
			IgnoreOptionStates = ignoreOptionStates,
			UpdatedUtc = updatedUtc
		};
	}

	private static ProjectSelectionProfile ToProfile(PersistedProjectProfile profile)
	{
		var rootFolders = new HashSet<string>(profile.SelectedRootFolders, PathComparer.Default);
		var extensions = new HashSet<string>(profile.SelectedExtensions, StringComparer.OrdinalIgnoreCase);
		var selectedIgnoreOptions = profile.SelectedIgnoreOptions.ToList();
		// Empty state maps still carry v2 semantics: options first seen after reopen
		// use current defaults instead of being treated as unchecked legacy misses.
		var rootStates = new Dictionary<string, bool>(profile.RootFolderStates, PathComparer.Default);
		var extensionStates = new Dictionary<string, bool>(profile.ExtensionStates, StringComparer.OrdinalIgnoreCase);
		var ignoreStates = new Dictionary<IgnoreOptionId, bool>(profile.IgnoreOptionStates);
		NormalizeGitFilteringState(selectedIgnoreOptions, ignoreStates);
		var ignoreOptions = new HashSet<IgnoreOptionId>(selectedIgnoreOptions);

		return new ProjectSelectionProfile(
			SelectedRootFolders: rootFolders,
			SelectedExtensions: extensions,
			SelectedIgnoreOptions: ignoreOptions,
			RootFolderStates: rootStates,
			ExtensionStates: extensionStates,
			IgnoreOptionStates: ignoreStates);
	}

	private static void NormalizeGitFilteringState(
		List<IgnoreOptionId> selectedIgnoreOptions,
		Dictionary<IgnoreOptionId, bool> ignoreOptionStates)
	{
		var selected = new HashSet<IgnoreOptionId>(selectedIgnoreOptions);
		var hasPersistedGitState =
			ignoreOptionStates.ContainsKey(IgnoreOptionId.UseGitIgnore) ||
			ignoreOptionStates.ContainsKey(IgnoreOptionId.TrackedGitFilesOnly);
		var preferredMode = hasPersistedGitState
			? GitFilteringModeResolver.Resolve(ignoreOptionStates)
			: GitFilteringModeResolver.Resolve(selected);

		GitFilteringModeResolver.Normalize(selected, preferredMode);
		GitFilteringModeResolver.Normalize(ignoreOptionStates, preferredMode);
		selected.Remove(IgnoreOptionId.UseGitIgnore);
		selected.Remove(IgnoreOptionId.TrackedGitFilesOnly);
		if (preferredMode == GitFilteringMode.RespectGitIgnore)
			selected.Add(IgnoreOptionId.UseGitIgnore);
		else if (preferredMode == GitFilteringMode.TrackedFilesOnly)
			selected.Add(IgnoreOptionId.TrackedGitFilesOnly);

		selectedIgnoreOptions.Clear();
		selectedIgnoreOptions.AddRange(selected);
	}

	private static Dictionary<string, bool> NormalizeStringStateDictionary(
		IEnumerable<KeyValuePair<string, bool>>? states,
		StringComparer comparer)
	{
		var normalized = new Dictionary<string, bool>(comparer);
		if (states is null)
			return normalized;

		foreach (var (name, isChecked) in states)
		{
			if (!string.IsNullOrWhiteSpace(name))
				normalized[name] = isChecked;
		}

		return normalized;
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
			var sourceSchemaVersion = deserialized.SchemaVersion;
			var normalized = Normalize(deserialized, sourceSchemaVersion);
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
}
