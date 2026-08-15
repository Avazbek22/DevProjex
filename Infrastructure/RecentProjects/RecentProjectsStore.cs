using DevProjex.Infrastructure.Persistence;

namespace DevProjex.Infrastructure.RecentProjects;

public sealed class RecentProjectsStore
{
	private const int CurrentSchemaVersion = 3;
	private const int MaxRecentFolders = 32;
	private const int MaxRecentFolderRemovals = 64;
	private const int MaxRecentRepositories = 16;
	private const int MaxRecentRepositoryRemovals = 32;
	private const string FolderName = "DevProjex";
	private const string FileName = "recent-projects.json";
	private static readonly string LegacyRepoCacheRootPath = Path.Combine(
		Path.GetTempPath(),
		FolderName,
		"RepoCache");
	private static readonly string LegacyPersistentRepoCacheRootPath = Path.Combine(
		UserDataPathResolver.GetLegacyLocalDataRoot(),
		FolderName,
		"RepoCache");
	private static readonly string PersistentRepoCacheRootPath = Path.Combine(
		UserDataPathResolver.GetCacheRoot(),
		FolderName,
		"RepoCache");

	private static readonly JsonSerializerOptions SerializerOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		WriteIndented = true,
		TypeInfoResolver = InfrastructureJsonSerializerContext.Default,
		Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
	};

	private readonly object _sync = new();
	private readonly Func<string> _appDataPathProvider;
	private readonly Func<string>? _legacyAppDataPathProvider;

	public RecentProjectsStore(Func<string>? appDataPathProvider = null)
	{
		_appDataPathProvider = appDataPathProvider ?? UserDataPathResolver.GetStateRoot;
		_legacyAppDataPathProvider = appDataPathProvider is null
			? UserDataPathResolver.GetConfigurationRoot
			: null;
	}

	internal RecentProjectsStore(
		Func<string> statePathProvider,
		Func<string> legacyConfigurationPathProvider)
	{
		_appDataPathProvider =
			statePathProvider ?? throw new ArgumentNullException(nameof(statePathProvider));
		_legacyAppDataPathProvider =
			legacyConfigurationPathProvider ??
			throw new ArgumentNullException(nameof(legacyConfigurationPathProvider));
	}

	public RecentProjectsDb Load()
	{
		lock (_sync)
		{
			var fileSet = GetFileSet();
			if (!CrossProcessFileLock.TryAcquire(fileSet, out var heldLock))
				return CreateDefaultDb();

			using var _ = heldLock;
			// Reads must stay side-effect free. Startup bootstrap is responsible for
			// making the store files exist; plain loading should not recreate files.
			return LoadInternal(fileSet);
		}
	}

	public RecentProjectsDb LoadForStartup(TimeSpan lockTimeout)
		=> LoadForStartupWithStatus(lockTimeout).Database;

	public RecentProjectsLoadResult LoadForStartupWithStatus(TimeSpan lockTimeout)
	{
		lock (_sync)
		{
			try
			{
				var fileSet = GetFileSet();
				if (!CrossProcessFileLock.TryAcquire(fileSet, lockTimeout, out var heldLock))
				{
					return new RecentProjectsLoadResult(
						CreateDefaultDb(),
						RecentProjectsLoadStatus.TemporarilyUnavailable);
				}

				using var _ = heldLock;
				var database = LoadInternal(
					fileSet,
					out var status,
					persistLegacyMigration: true);
				return new RecentProjectsLoadResult(database, status);
			}
			catch
			{
				// Recent history is optional during bootstrap and must never stall the first window.
				return new RecentProjectsLoadResult(
					CreateDefaultDb(),
					RecentProjectsLoadStatus.InvalidStorage);
			}
		}
	}

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

	public RecentProjectsDb AddFolder(RecentProjectsDb? db, string path)
	{
		lock (_sync)
		{
			var fileSet = GetFileSet();
			if (!CrossProcessFileLock.TryAcquire(fileSet, out var heldLock))
			{
				// Keep the current session history responsive even when the shared store
				// cannot be reached. The caller keeps the returned snapshot in memory and
				// a later flush can still retry persistence.
				var inMemoryState = SanitizeState(fileSet, MergeStates(CreateDefaultDb(), db));
				if (!TryNormalizeFolderPath(path, out var inMemoryNormalizedPath))
					return inMemoryState;

				if (IsIgnoredFolderPath(fileSet, inMemoryNormalizedPath))
					return inMemoryState;

				MoveToFront(
					inMemoryState.RecentFolders,
					inMemoryNormalizedPath,
					MaxRecentFolders,
					PathComparer.Default,
					static entry => entry.Path,
					static value => value,
					static (value, openedUtc) => new RecentFolderEntry
					{
						Path = value,
						OpenedUtc = openedUtc
					},
					CreateFolderOpenedUtc(inMemoryState, inMemoryNormalizedPath));
				return inMemoryState;
			}

			using var _ = heldLock;
			var state = SanitizeState(fileSet, MergeStates(LoadInternal(fileSet), db));
			if (!TryNormalizeFolderPath(path, out var normalizedPath))
				return state;

			if (IsIgnoredFolderPath(fileSet, normalizedPath))
				return state;

			MoveToFront(
				state.RecentFolders,
				normalizedPath,
				MaxRecentFolders,
				PathComparer.Default,
				static entry => entry.Path,
				static value => value,
				static (value, openedUtc) => new RecentFolderEntry
				{
					Path = value,
					OpenedUtc = openedUtc
				},
				CreateFolderOpenedUtc(state, normalizedPath));

			TrySave(fileSet, state);
			return state;
		}
	}

	public RecentProjectsDb AddRepository(RecentProjectsDb? db, string repositoryUrl)
	{
		lock (_sync)
		{
			var fileSet = GetFileSet();
			if (!CrossProcessFileLock.TryAcquire(fileSet, out var heldLock))
			{
				// Keep the current session history responsive even when the shared store
				// cannot be reached. The caller keeps the returned snapshot in memory and
				// a later flush can still retry persistence.
				var inMemoryState = SanitizeState(fileSet, MergeStates(CreateDefaultDb(), db));
				if (!RepositoryUrlUtility.TryNormalize(repositoryUrl, out var inMemoryNormalizedUrl))
					return inMemoryState;

				MoveToFront(
					inMemoryState.RecentRepositories,
					inMemoryNormalizedUrl,
					MaxRecentRepositories,
					StringComparer.OrdinalIgnoreCase,
					static entry => entry.Url,
					static value => RepositoryUrlUtility.GetComparisonKey(value),
					static (value, openedUtc) => new RecentRepositoryEntry
					{
						Url = value,
						OpenedUtc = openedUtc
					},
					CreateRepositoryOpenedUtc(inMemoryState, inMemoryNormalizedUrl));
				return inMemoryState;
			}

			using var _ = heldLock;
			var state = SanitizeState(fileSet, MergeStates(LoadInternal(fileSet), db));
			if (!RepositoryUrlUtility.TryNormalize(repositoryUrl, out var normalizedUrl))
				return state;

			MoveToFront(
				state.RecentRepositories,
				normalizedUrl,
				MaxRecentRepositories,
				StringComparer.OrdinalIgnoreCase,
				static entry => entry.Url,
				static value => RepositoryUrlUtility.GetComparisonKey(value),
				static (value, openedUtc) => new RecentRepositoryEntry
				{
					Url = value,
					OpenedUtc = openedUtc
				},
				CreateRepositoryOpenedUtc(state, normalizedUrl));

			TrySave(fileSet, state);
			return state;
		}
	}

	public RecentProjectsDb RemoveFolder(RecentProjectsDb? db, string path)
	{
		lock (_sync)
		{
			var fileSet = GetFileSet();
			if (!CrossProcessFileLock.TryAcquire(fileSet, out var heldLock))
			{
				var inMemoryState = SanitizeState(fileSet, MergeStates(CreateDefaultDb(), db));
				return ApplyFolderRemoval(fileSet, inMemoryState, path);
			}

			using var _ = heldLock;
			var state = SanitizeState(fileSet, MergeStates(LoadInternal(fileSet), db));
			state = ApplyFolderRemoval(fileSet, state, path);
			TrySave(fileSet, state);
			return state;
		}
	}

	public bool TryPersist(RecentProjectsDb? db)
	{
		lock (_sync)
		{
			var fileSet = GetFileSet();
			if (!CrossProcessFileLock.TryAcquire(fileSet, out var heldLock))
				return false;

			using var _ = heldLock;
			var state = SanitizeState(fileSet, MergeStates(LoadInternal(fileSet), db));
			return TrySave(fileSet, state);
		}
	}

	public string GetPath()
	{
		return GetFileSet().PrimaryPath;
	}

	private JsonStoreFileSet GetFileSet()
		=> JsonStoreFileSet.Create(_appDataPathProvider, FolderName, FileName);

	private RecentProjectsDb LoadInternal(JsonStoreFileSet fileSet)
		=> LoadInternal(fileSet, out _);

	private RecentProjectsDb LoadInternal(
		JsonStoreFileSet fileSet,
		out RecentProjectsLoadStatus status,
		bool persistLegacyMigration = false)
	{
		if (!File.Exists(fileSet.PrimaryPath) &&
		    !File.Exists(fileSet.BackupPath) &&
		    TryLoadLegacy(fileSet, out var legacyDb))
		{
			status = RecentProjectsLoadStatus.Success;
			var sanitizedLegacyDb = SanitizeState(fileSet, legacyDb);
			if (persistLegacyMigration)
				TrySave(fileSet, sanitizedLegacyDb);
			return sanitizedLegacyDb;
		}

		if (TryLoadFromPath(fileSet.PrimaryPath, out var primaryDb, out var primaryRequiresRewrite))
		{
			status = RecentProjectsLoadStatus.Success;
			var sanitizedPrimaryDb = SanitizeState(fileSet, primaryDb, out var primaryRequiresSanitizationRewrite);
			if (primaryRequiresRewrite || primaryRequiresSanitizationRewrite)
				TrySave(fileSet, sanitizedPrimaryDb);

			return sanitizedPrimaryDb;
		}

		// Keep the last known-good snapshot as a recovery path.
		// A partially written or externally corrupted primary file must not silently erase history.
		if (TryLoadFromPath(fileSet.BackupPath, out var backupDb, out _))
		{
			status = RecentProjectsLoadStatus.Success;
			var sanitizedBackupDb = SanitizeState(fileSet, backupDb);
			TrySave(fileSet, sanitizedBackupDb);
			return sanitizedBackupDb;
		}

		status = File.Exists(fileSet.PrimaryPath) || File.Exists(fileSet.BackupPath)
			? RecentProjectsLoadStatus.InvalidStorage
			: RecentProjectsLoadStatus.Success;
		return CreateDefaultDb();
	}

	public RecentProjectsDb RemoveRepository(RecentProjectsDb? db, string repositoryUrl)
	{
		lock (_sync)
		{
			var fileSet = GetFileSet();
			if (!CrossProcessFileLock.TryAcquire(fileSet, out var heldLock))
			{
				var inMemoryState = SanitizeState(fileSet, MergeStates(CreateDefaultDb(), db));
				return ApplyRepositoryRemoval(fileSet, inMemoryState, repositoryUrl);
			}

			using var _ = heldLock;
			var state = SanitizeState(fileSet, MergeStates(LoadInternal(fileSet), db));
			state = ApplyRepositoryRemoval(fileSet, state, repositoryUrl);
			TrySave(fileSet, state);
			return state;
		}
	}

	private bool EnsureStorageExistsCore(JsonStoreFileSet fileSet)
	{
		if (!File.Exists(fileSet.PrimaryPath) &&
		    !File.Exists(fileSet.BackupPath) &&
		    TryLoadLegacy(fileSet, out var legacyDb))
		{
			return TrySave(fileSet, SanitizeState(fileSet, legacyDb));
		}

		if (TryLoadFromPath(fileSet.PrimaryPath, out var primaryDb, out var primaryRequiresRewrite))
		{
			var sanitizedPrimaryDb = SanitizeState(fileSet, primaryDb, out var primaryRequiresSanitizationRewrite);
			if (primaryRequiresRewrite || primaryRequiresSanitizationRewrite || !File.Exists(fileSet.BackupPath))
				return TrySave(fileSet, sanitizedPrimaryDb);

			return true;
		}

		if (TryLoadFromPath(fileSet.BackupPath, out var backupDb, out _))
			return TrySave(fileSet, SanitizeState(fileSet, backupDb));

		if (File.Exists(fileSet.PrimaryPath) || File.Exists(fileSet.BackupPath))
			return false;

		// Keep the store files present from startup so external cleanup or partial state loss
		// cannot leave the app with a surprising "history feature silently disappeared" state.
		return TrySave(fileSet, CreateDefaultDb());
	}

	private static RecentProjectsDb CreateDefaultDb()
	{
		return new RecentProjectsDb
		{
			SchemaVersion = CurrentSchemaVersion,
			RecentFolders = [],
			RecentFolderRemovals = [],
			RecentRepositories = [],
			RecentRepositoryRemovals = []
		};
	}

	private static RecentProjectsDb Normalize(RecentProjectsDb db)
	{
		db.SchemaVersion = CurrentSchemaVersion;
		db.RecentFolders ??= [];
		db.RecentFolderRemovals ??= [];
		db.RecentRepositories ??= [];
		db.RecentRepositoryRemovals ??= [];

		db.RecentFolderRemovals = NormalizeFolderRemovals(db.RecentFolderRemovals);
		var removalTimes = new Dictionary<string, DateTimeOffset>(PathComparer.Default);
		foreach (var removal in db.RecentFolderRemovals)
			removalTimes[removal.Path] = removal.RemovedUtc;

		db.RecentFolders = NormalizeFolders(db.RecentFolders, removalTimes);
		db.RecentRepositoryRemovals = NormalizeRepositoryRemovals(db.RecentRepositoryRemovals);
		var repositoryRemovalTimes = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
		foreach (var removal in db.RecentRepositoryRemovals)
			repositoryRemovalTimes[RepositoryUrlUtility.GetComparisonKey(removal.Url)] = removal.RemovedUtc;

		db.RecentRepositories = NormalizeRepositories(db.RecentRepositories, repositoryRemovalTimes);
		return db;
	}

	private static RecentProjectsDb SanitizeState(JsonStoreFileSet fileSet, RecentProjectsDb db)
		=> SanitizeState(fileSet, db, out _);

	private static RecentProjectsDb SanitizeState(JsonStoreFileSet fileSet, RecentProjectsDb db, out bool requiresRewrite)
	{
		var normalized = Normalize(db);
		var originalCount = normalized.RecentFolders.Count;
		normalized.RecentFolders = normalized.RecentFolders
			.Where(entry => !IsIgnoredFolderPath(fileSet, entry.Path))
			.ToList();
		requiresRewrite = normalized.RecentFolders.Count != originalCount;
		return normalized;
	}

	private static RecentProjectsDb MergeStates(RecentProjectsDb current, RecentProjectsDb? snapshot)
	{
		if (snapshot is null)
			return current;

		// Window-scoped snapshots can be stale by the time they are flushed on close.
		// Merge them with the latest on-disk state so one process never drops another
		// process window's history entries during a delayed retry.
		var merged = CreateDefaultDb();
		merged.RecentFolders.AddRange(snapshot.RecentFolders);
		merged.RecentFolders.AddRange(current.RecentFolders);
		merged.RecentFolderRemovals.AddRange(snapshot.RecentFolderRemovals);
		merged.RecentFolderRemovals.AddRange(current.RecentFolderRemovals);
		merged.RecentRepositories.AddRange(snapshot.RecentRepositories);
		merged.RecentRepositories.AddRange(current.RecentRepositories);
		merged.RecentRepositoryRemovals.AddRange(snapshot.RecentRepositoryRemovals);
		merged.RecentRepositoryRemovals.AddRange(current.RecentRepositoryRemovals);
		return Normalize(merged);
	}

	private static List<RecentFolderEntry> NormalizeFolders(
		IEnumerable<RecentFolderEntry> entries,
		IReadOnlyDictionary<string, DateTimeOffset> removalTimes)
	{
		var ordered = entries
			.Where(static entry => entry is not null && TryNormalizeFolderPath(entry.Path, out _))
			.Select(static entry => new RecentFolderEntry
			{
				Path = PathUtility.Normalize(entry.Path),
				OpenedUtc = entry.OpenedUtc <= DateTimeOffset.UnixEpoch ? DateTimeOffset.UtcNow : entry.OpenedUtc
			})
			.Where(static entry => !IsRepoCachePath(entry.Path))
			.Where(entry => !removalTimes.TryGetValue(entry.Path, out var removedUtc) || entry.OpenedUtc > removedUtc)
			.OrderByDescending(static entry => entry.OpenedUtc)
			.ToList();

		var unique = new List<RecentFolderEntry>();
		var seen = new HashSet<string>(PathComparer.Default);
		foreach (var entry in ordered)
		{
			if (seen.Add(entry.Path))
				unique.Add(entry);
		}

		if (unique.Count > MaxRecentFolders)
			unique.RemoveRange(MaxRecentFolders, unique.Count - MaxRecentFolders);

		return unique;
	}

	private static List<RecentFolderRemovalEntry> NormalizeFolderRemovals(
		IEnumerable<RecentFolderRemovalEntry> entries)
	{
		var ordered = entries
			.Where(static entry => entry is not null && TryNormalizeFolderPath(entry.Path, out _))
			.Select(static entry => new RecentFolderRemovalEntry
			{
				Path = PathUtility.Normalize(entry.Path),
				RemovedUtc = entry.RemovedUtc <= DateTimeOffset.UnixEpoch
					? DateTimeOffset.UtcNow
					: entry.RemovedUtc
			})
			.OrderByDescending(static entry => entry.RemovedUtc)
			.ToList();

		var unique = new List<RecentFolderRemovalEntry>();
		var seen = new HashSet<string>(PathComparer.Default);
		foreach (var entry in ordered)
		{
			if (seen.Add(entry.Path))
				unique.Add(entry);
		}

		if (unique.Count > MaxRecentFolderRemovals)
			unique.RemoveRange(MaxRecentFolderRemovals, unique.Count - MaxRecentFolderRemovals);

		return unique;
	}

	private static List<RecentRepositoryEntry> NormalizeRepositories(
		IEnumerable<RecentRepositoryEntry> entries,
		IReadOnlyDictionary<string, DateTimeOffset> removalTimes)
	{
		var ordered = entries
			.Where(static entry => entry is not null && RepositoryUrlUtility.TryNormalize(entry.Url, out _))
			.Select(static entry => new RecentRepositoryEntry
			{
				Url = RepositoryUrlUtility.Normalize(entry.Url),
				OpenedUtc = entry.OpenedUtc <= DateTimeOffset.UnixEpoch ? DateTimeOffset.UtcNow : entry.OpenedUtc
			})
			.Where(entry =>
			{
				var key = RepositoryUrlUtility.GetComparisonKey(entry.Url);
				return !removalTimes.TryGetValue(key, out var removedUtc) || entry.OpenedUtc > removedUtc;
			})
			.OrderByDescending(static entry => entry.OpenedUtc)
			.ToList();

		var unique = new List<RecentRepositoryEntry>();
		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var entry in ordered)
		{
			if (seen.Add(RepositoryUrlUtility.GetComparisonKey(entry.Url)))
				unique.Add(entry);
		}

		if (unique.Count > MaxRecentRepositories)
			unique.RemoveRange(MaxRecentRepositories, unique.Count - MaxRecentRepositories);

		return unique;
	}

	private static List<RecentRepositoryRemovalEntry> NormalizeRepositoryRemovals(
		IEnumerable<RecentRepositoryRemovalEntry> entries)
	{
		var ordered = entries
			.Where(static entry => entry is not null && RepositoryUrlUtility.TryNormalize(entry.Url, out _))
			.Select(static entry => new RecentRepositoryRemovalEntry
			{
				Url = RepositoryUrlUtility.Normalize(entry.Url),
				RemovedUtc = entry.RemovedUtc <= DateTimeOffset.UnixEpoch
					? DateTimeOffset.UtcNow
					: entry.RemovedUtc
			})
			.OrderByDescending(static entry => entry.RemovedUtc)
			.ToList();

		var unique = new List<RecentRepositoryRemovalEntry>();
		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var entry in ordered)
		{
			if (seen.Add(RepositoryUrlUtility.GetComparisonKey(entry.Url)))
				unique.Add(entry);
		}

		if (unique.Count > MaxRecentRepositoryRemovals)
			unique.RemoveRange(MaxRecentRepositoryRemovals, unique.Count - MaxRecentRepositoryRemovals);

		return unique;
	}

	private static void MoveToFront<TEntry>(
		List<TEntry> entries,
		string normalizedValue,
		int limit,
		IEqualityComparer<string> comparer,
		Func<TEntry, string> keySelector,
		Func<string, string> comparisonKeySelector,
		Func<string, DateTimeOffset, TEntry> factory,
		DateTimeOffset? openedUtc = null)
	{
		var normalizedComparisonKey = comparisonKeySelector(normalizedValue);
		entries.RemoveAll(entry => comparer.Equals(comparisonKeySelector(keySelector(entry)), normalizedComparisonKey));
		entries.Insert(0, factory(normalizedValue, openedUtc ?? DateTimeOffset.UtcNow));

		if (entries.Count > limit)
			entries.RemoveRange(limit, entries.Count - limit);
	}

	private static RecentProjectsDb ApplyFolderRemoval(
		JsonStoreFileSet fileSet,
		RecentProjectsDb state,
		string path)
	{
		if (!TryNormalizeFolderPath(path, out var normalizedPath) || IsIgnoredFolderPath(fileSet, normalizedPath))
			return state;

		// A persisted removal timestamp prevents another window's stale snapshot from
		// resurrecting the entry while still allowing a newer explicit open to restore it.
		var removedUtc = DateTimeOffset.UtcNow;
		foreach (var entry in state.RecentFolders)
		{
			if (PathComparer.Default.Equals(entry.Path, normalizedPath) && entry.OpenedUtc >= removedUtc)
				removedUtc = entry.OpenedUtc.AddTicks(1);
		}

		foreach (var removal in state.RecentFolderRemovals)
		{
			if (PathComparer.Default.Equals(removal.Path, normalizedPath) && removal.RemovedUtc >= removedUtc)
				removedUtc = removal.RemovedUtc.AddTicks(1);
		}

		state.RecentFolders.RemoveAll(entry => PathComparer.Default.Equals(entry.Path, normalizedPath));
		state.RecentFolderRemovals.RemoveAll(entry => PathComparer.Default.Equals(entry.Path, normalizedPath));
		state.RecentFolderRemovals.Add(new RecentFolderRemovalEntry
		{
			Path = normalizedPath,
			RemovedUtc = removedUtc
		});

		return SanitizeState(fileSet, state);
	}

	private static DateTimeOffset CreateFolderOpenedUtc(RecentProjectsDb state, string normalizedPath)
	{
		var openedUtc = DateTimeOffset.UtcNow;
		foreach (var removal in state.RecentFolderRemovals)
		{
			if (PathComparer.Default.Equals(removal.Path, normalizedPath) && removal.RemovedUtc >= openedUtc)
				openedUtc = removal.RemovedUtc.AddTicks(1);
		}

		return openedUtc;
	}

	private static RecentProjectsDb ApplyRepositoryRemoval(
		JsonStoreFileSet fileSet,
		RecentProjectsDb state,
		string repositoryUrl)
	{
		if (!RepositoryUrlUtility.TryNormalize(repositoryUrl, out var normalizedUrl))
			return state;

		var comparisonKey = RepositoryUrlUtility.GetComparisonKey(normalizedUrl);
		var removedUtc = DateTimeOffset.UtcNow;
		foreach (var entry in state.RecentRepositories)
		{
			if (string.Equals(
				    RepositoryUrlUtility.GetComparisonKey(entry.Url),
				    comparisonKey,
				    StringComparison.OrdinalIgnoreCase) &&
			    entry.OpenedUtc >= removedUtc)
			{
				removedUtc = entry.OpenedUtc.AddTicks(1);
			}
		}

		foreach (var removal in state.RecentRepositoryRemovals)
		{
			if (string.Equals(
				    RepositoryUrlUtility.GetComparisonKey(removal.Url),
				    comparisonKey,
				    StringComparison.OrdinalIgnoreCase) &&
			    removal.RemovedUtc >= removedUtc)
			{
				removedUtc = removal.RemovedUtc.AddTicks(1);
			}
		}

		state.RecentRepositories.RemoveAll(entry =>
			string.Equals(
				RepositoryUrlUtility.GetComparisonKey(entry.Url),
				comparisonKey,
				StringComparison.OrdinalIgnoreCase));
		state.RecentRepositoryRemovals.RemoveAll(entry =>
			string.Equals(
				RepositoryUrlUtility.GetComparisonKey(entry.Url),
				comparisonKey,
				StringComparison.OrdinalIgnoreCase));
		state.RecentRepositoryRemovals.Add(new RecentRepositoryRemovalEntry
		{
			Url = normalizedUrl,
			RemovedUtc = removedUtc
		});

		return SanitizeState(fileSet, state);
	}

	private static DateTimeOffset CreateRepositoryOpenedUtc(
		RecentProjectsDb state,
		string normalizedUrl)
	{
		var comparisonKey = RepositoryUrlUtility.GetComparisonKey(normalizedUrl);
		var openedUtc = DateTimeOffset.UtcNow;
		foreach (var removal in state.RecentRepositoryRemovals)
		{
			if (string.Equals(
				    RepositoryUrlUtility.GetComparisonKey(removal.Url),
				    comparisonKey,
				    StringComparison.OrdinalIgnoreCase) &&
			    removal.RemovedUtc >= openedUtc)
			{
				openedUtc = removal.RemovedUtc.AddTicks(1);
			}
		}

		return openedUtc;
	}

	private bool TrySave(JsonStoreFileSet fileSet, RecentProjectsDb db)
	{
		var sanitized = SanitizeState(fileSet, db);
		return JsonStorePersistence.TryWriteAtomic(fileSet, sanitized, SerializerOptions);
	}

	private static bool TryLoadFromPath(string path, out RecentProjectsDb db, out bool requiresRewrite)
	{
		db = CreateDefaultDb();
		requiresRewrite = false;

		if (!File.Exists(path))
			return false;

		try
		{
			var json = File.ReadAllText(path);
			var deserialized = JsonSerializer.Deserialize<RecentProjectsDb>(json, SerializerOptions);
			if (deserialized is null)
				return false;

			// Normalize in-memory and rewrite only structurally valid payloads.
			// Invalid payloads are left untouched so operators can inspect them and the backup can recover them.
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

	private bool TryLoadLegacy(
		JsonStoreFileSet currentFileSet,
		out RecentProjectsDb database)
	{
		database = CreateDefaultDb();
		if (_legacyAppDataPathProvider is null)
			return false;

		JsonStoreFileSet legacyFileSet;
		try
		{
			legacyFileSet = JsonStoreFileSet.Create(
				_legacyAppDataPathProvider,
				FolderName,
				FileName);
		}
		catch (Exception exception) when (
			exception is ArgumentException or
			IOException or
			InvalidOperationException or
			NotSupportedException or
			UnauthorizedAccessException or
			System.Security.SecurityException)
		{
			return false;
		}

		if (PathComparer.Default.Equals(
			    legacyFileSet.PrimaryPath,
			    currentFileSet.PrimaryPath))
			return false;

		if (TryLoadFromPath(legacyFileSet.PrimaryPath, out database, out _))
			return true;
		return TryLoadFromPath(legacyFileSet.BackupPath, out database, out _);
	}

	private static bool TryNormalizeFolderPath(string path, out string normalizedPath)
	{
		normalizedPath = string.Empty;
		if (string.IsNullOrWhiteSpace(path))
			return false;

		try
		{
			normalizedPath = PathUtility.Normalize(path);
			return !string.IsNullOrWhiteSpace(normalizedPath) && !IsRepoCachePath(normalizedPath);
		}
		catch
		{
			return false;
		}
	}

	private static bool IsRepoCachePath(string path)
	{
		if (string.IsNullOrWhiteSpace(path))
			return false;

		try
		{
			return PathUtility.IsPathInside(path, LegacyRepoCacheRootPath) ||
			       PathUtility.IsPathInside(path, LegacyPersistentRepoCacheRootPath) ||
			       PathUtility.IsPathInside(path, PersistentRepoCacheRootPath);
		}
		catch
		{
			return false;
		}
	}

	private static bool IsIgnoredFolderPath(JsonStoreFileSet fileSet, string path)
	{
		if (IsRepoCachePath(path))
			return true;

		if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(fileSet.DirectoryPath))
			return false;

		try
		{
			// The application state directory is an internal persistence implementation detail.
			// Users can inspect it manually, but it must never pollute the recent-project history.
			return PathUtility.IsPathInside(path, fileSet.DirectoryPath);
		}
		catch
		{
			return false;
		}
	}

}
