using DevProjex.Infrastructure.Persistence;

namespace DevProjex.Infrastructure.RecentProjects;

public sealed class RecentProjectsStore(Func<string>? appDataPathProvider = null)
{
	private const int CurrentSchemaVersion = 2;
	private const int MaxRecentFolders = 15;
	private const int MaxRecentFolderRemovals = 64;
	private const int MaxRecentRepositories = 7;
	private const string FolderName = "DevProjex";
	private const string FileName = "recent-projects.json";
	private static readonly string RepoCacheRootPath = Path.Combine(
		Path.GetTempPath(),
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
	private readonly Func<string> _appDataPathProvider =
		appDataPathProvider ?? UserDataPathResolver.GetConfigurationRoot;

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
				// Recent history is optional during bootstrap and must never stall the first window.
				return CreateDefaultDb();
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
				if (!TryNormalizeRepositoryUrl(repositoryUrl, out var inMemoryNormalizedUrl))
					return inMemoryState;

				MoveToFront(
					inMemoryState.RecentRepositories,
					inMemoryNormalizedUrl,
					MaxRecentRepositories,
					StringComparer.OrdinalIgnoreCase,
					static entry => entry.Url,
					static value => NormalizeRepositoryComparisonKey(value),
					static (value, openedUtc) => new RecentRepositoryEntry
					{
						Url = value,
						OpenedUtc = openedUtc
					});
				return inMemoryState;
			}

			using var _ = heldLock;
			var state = SanitizeState(fileSet, MergeStates(LoadInternal(fileSet), db));
			if (!TryNormalizeRepositoryUrl(repositoryUrl, out var normalizedUrl))
				return state;

			MoveToFront(
				state.RecentRepositories,
				normalizedUrl,
				MaxRecentRepositories,
				StringComparer.OrdinalIgnoreCase,
				static entry => entry.Url,
				static value => NormalizeRepositoryComparisonKey(value),
				static (value, openedUtc) => new RecentRepositoryEntry
				{
					Url = value,
					OpenedUtc = openedUtc
				});

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
	{
		if (TryLoadFromPath(fileSet.PrimaryPath, out var primaryDb, out var primaryRequiresRewrite))
		{
			var sanitizedPrimaryDb = SanitizeState(fileSet, primaryDb, out var primaryRequiresSanitizationRewrite);
			if (primaryRequiresRewrite || primaryRequiresSanitizationRewrite)
				TrySave(fileSet, sanitizedPrimaryDb);

			return sanitizedPrimaryDb;
		}

		// Keep the last known-good snapshot as a recovery path.
		// A partially written or externally corrupted primary file must not silently erase history.
		if (TryLoadFromPath(fileSet.BackupPath, out var backupDb, out _))
		{
			var sanitizedBackupDb = SanitizeState(fileSet, backupDb);
			TrySave(fileSet, sanitizedBackupDb);
			return sanitizedBackupDb;
		}

		return CreateDefaultDb();
	}

	private bool EnsureStorageExistsCore(JsonStoreFileSet fileSet)
	{
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
			RecentRepositories = []
		};
	}

	private static RecentProjectsDb Normalize(RecentProjectsDb db)
	{
		db.SchemaVersion = CurrentSchemaVersion;
		db.RecentFolders ??= [];
		db.RecentFolderRemovals ??= [];
		db.RecentRepositories ??= [];

		db.RecentFolderRemovals = NormalizeFolderRemovals(db.RecentFolderRemovals);
		var removalTimes = new Dictionary<string, DateTimeOffset>(PathComparer.Default);
		foreach (var removal in db.RecentFolderRemovals)
			removalTimes[removal.Path] = removal.RemovedUtc;

		db.RecentFolders = NormalizeFolders(db.RecentFolders, removalTimes);
		db.RecentRepositories = NormalizeRepositories(db.RecentRepositories);
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

	private static List<RecentRepositoryEntry> NormalizeRepositories(IEnumerable<RecentRepositoryEntry> entries)
	{
		var ordered = entries
			.Where(static entry => entry is not null && TryNormalizeRepositoryUrl(entry.Url, out _))
			.Select(static entry => new RecentRepositoryEntry
			{
				Url = NormalizeRepositoryUrl(entry.Url),
				OpenedUtc = entry.OpenedUtc <= DateTimeOffset.UnixEpoch ? DateTimeOffset.UtcNow : entry.OpenedUtc
			})
			.OrderByDescending(static entry => entry.OpenedUtc)
			.ToList();

		var unique = new List<RecentRepositoryEntry>();
		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var entry in ordered)
		{
			if (seen.Add(NormalizeRepositoryComparisonKey(entry.Url)))
				unique.Add(entry);
		}

		if (unique.Count > MaxRecentRepositories)
			unique.RemoveRange(MaxRecentRepositories, unique.Count - MaxRecentRepositories);

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
			return PathUtility.IsPathInside(path, RepoCacheRootPath);
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

	private static bool TryNormalizeRepositoryUrl(string repositoryUrl, out string normalizedUrl)
	{
		normalizedUrl = string.Empty;
		if (string.IsNullOrWhiteSpace(repositoryUrl))
			return false;

		normalizedUrl = NormalizeRepositoryUrl(repositoryUrl);
		return !string.IsNullOrWhiteSpace(normalizedUrl);
	}

	private static string NormalizeRepositoryUrl(string repositoryUrl)
	{
		var trimmed = repositoryUrl.Trim();
		if (string.IsNullOrWhiteSpace(trimmed))
			return string.Empty;

		trimmed = trimmed.Replace('\\', '/').TrimEnd('/');
		if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
		{
			var builder = new UriBuilder(uri)
			{
				Fragment = string.Empty,
				Query = string.Empty
			};

			return builder.Uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
		}

		return trimmed;
	}

	private static string NormalizeRepositoryComparisonKey(string repositoryUrl)
	{
		var normalized = NormalizeRepositoryUrl(repositoryUrl);
		return normalized.EndsWith(".git", StringComparison.OrdinalIgnoreCase)
			? normalized[..^4]
			: normalized;
	}
}
