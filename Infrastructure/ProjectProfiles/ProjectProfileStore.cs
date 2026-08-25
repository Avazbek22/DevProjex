using DevProjex.Application.Context;
using DevProjex.Application.Secrets;
using DevProjex.Infrastructure.Persistence;
using System.Text.Json.Nodes;

namespace DevProjex.Infrastructure.ProjectProfiles;

public sealed class ProjectProfileStore(Func<string>? appDataPathProvider = null) :
	IProjectProfileStore,
	IPersistentSecretMarkStore
{
	private const int CurrentSchemaVersion = 3;
	private const string FolderName = "DevProjex";
	private const string FileName = "project-profiles.json";
	private static readonly DateTimeOffset MaximumSafeProfileTimestamp = DateTimeOffset.MaxValue.AddDays(-1);

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
	private readonly PersistentSecretMarkStore _persistentMarks = new(
		appDataPathProvider ?? UserDataPathResolver.GetConfigurationRoot);

    public bool EnsureStorageExists()
	{
		lock (_sync)
		{
			var fileSet = GetFileSet();
			if (!CrossProcessFileLock.TryAcquire(fileSet, out var heldLock))
				return false;

			using var _ = heldLock;
			if (HasOversizedDocument(fileSet) ||
			    JsonStorePersistence.ContainsFutureDocument(fileSet, CurrentSchemaVersion))
				return false;
			return EnsureStorageExistsCore(fileSet);
		}
	}

	public bool TryLoadProfile(string localProjectPath, out ProjectSelectionProfile profile)
	{
		var lookup = LookupProfile(localProjectPath, TimeSpan.FromSeconds(5));
		profile = lookup.Profile ?? new ProjectSelectionProfile(
			SelectedRootFolders: [],
			SelectedExtensions: [],
			SelectedIgnoreOptions: []);
		return lookup.Status == ProjectProfileLookupStatus.Found;
	}

	public ValueTask<PersistentSecretMarksLoadResult> LoadMarksAsync(
		string localProjectPath,
		CancellationToken cancellationToken = default) =>
		_persistentMarks.LoadAsync(localProjectPath, cancellationToken);

	public ValueTask<PersistentSecretMarkWriteResult> AddMarkAsync(
		string localProjectPath,
		MarkedSecretProfileEntry mark,
		CancellationToken cancellationToken = default) =>
		_persistentMarks.AddAsync(localProjectPath, mark, cancellationToken);

	public ValueTask<PersistentSecretMarkWriteResult> RemoveMarkAsync(
		string localProjectPath,
		PersistentSecretMarkId markId,
		CancellationToken cancellationToken = default) =>
		_persistentMarks.RemoveAsync(localProjectPath, markId, cancellationToken);

	public ValueTask<PersistentSecretMarkWriteResult> ApplyMarkDeltaAsync(
		string localProjectPath,
		PersistentSecretMarkDelta delta,
		CancellationToken cancellationToken = default) =>
		_persistentMarks.ApplyDeltaAsync(localProjectPath, delta, cancellationToken);

	public void SaveProfile(string localProjectPath, ProjectSelectionProfile profile)
		=> _ = TrySaveProfileWithResult(localProjectPath, profile);

	public bool TrySaveProfile(string localProjectPath, ProjectSelectionProfile profile)
		=> TrySaveProfileWithResult(localProjectPath, profile).Succeeded;

	public bool TrySaveProfile(string localProjectPath, ProjectSelectionProfile profile, DateTimeOffset updatedUtc)
		=> TrySaveProfileWithResult(localProjectPath, profile, updatedUtc).Succeeded;

	public ProjectProfileSaveResult TrySaveProfileWithResult(
		string localProjectPath,
		ProjectSelectionProfile profile) =>
		TrySaveProfileWithResult(localProjectPath, profile, DateTimeOffset.UtcNow);

	private ProjectProfileSaveResult TrySaveProfileWithResult(
		string localProjectPath,
		ProjectSelectionProfile profile,
		DateTimeOffset updatedUtc)
	{
		if (!TryNormalizePath(localProjectPath, out var normalizedPath))
			return new ProjectProfileSaveResult(Succeeded: false, WasTruncated: false);

		var normalizedUpdatedUtc = NormalizeProfileTimestamp(updatedUtc);
		var persistedProfile = ToPersistedProfile(profile, normalizedUpdatedUtc, out var wasTruncated);
		if (wasTruncated)
			return new ProjectProfileSaveResult(Succeeded: false, WasTruncated: true);

		lock (_sync)
		{
			var fileSet = GetFileSet();
			if (!CrossProcessFileLock.TryAcquire(fileSet, out var heldLock))
				return new ProjectProfileSaveResult(Succeeded: false, WasTruncated: false);

			using var _ = heldLock;
			if (HasOversizedDocument(fileSet) ||
			    JsonStorePersistence.ContainsFutureDocument(fileSet, CurrentSchemaVersion))
				return new ProjectProfileSaveResult(Succeeded: false, WasTruncated: false);
			var db = LoadInternal(fileSet);
			db.SchemaVersion = CurrentSchemaVersion;

			// A delayed retry from another window/process must not stomp a newer profile revision.
			// The caller-provided timestamp reflects when the profile became user-approved.
			if (db.Profiles.TryGetValue(normalizedPath, out var existing) &&
				existing is not null &&
				existing.UpdatedUtc > normalizedUpdatedUtc)
			{
				return new ProjectProfileSaveResult(Succeeded: true, WasTruncated: false);
			}

			db.Profiles[normalizedPath] = persistedProfile;
			PruneProfiles(db);
			return new ProjectProfileSaveResult(
				TrySaveInternal(fileSet, db),
				WasTruncated: false);
		}
	}

	public void ClearAllProfiles()
	{
		var selectionStoreCleared = false;
		lock (_sync)
		{
			try
			{
				var fileSet = GetFileSet();
				if (CrossProcessFileLock.TryAcquire(fileSet, out var heldLock))
				{
					using var _ = heldLock;
					if (!HasOversizedDocument(fileSet) &&
					    JsonStorePersistence.ContainsFutureDocument(fileSet, CurrentSchemaVersion))
						return;
					if (File.Exists(fileSet.PrimaryPath))
						File.Delete(fileSet.PrimaryPath);
					if (File.Exists(fileSet.BackupPath))
						File.Delete(fileSet.BackupPath);
					selectionStoreCleared = true;
				}
			}
			catch
			{
				// Best effort: the app must stay stable even if persistence cleanup fails.
			}
		}

		if (selectionStoreCleared)
			_persistentMarks.ClearAll();
	}

	public ProjectProfileLookupResult LookupProfile(
		string localProjectPath,
		TimeSpan lockTimeout)
	{
		if (!TryNormalizePath(localProjectPath, out var normalizedPath))
		{
			return new ProjectProfileLookupResult(
				ProjectProfileLookupStatus.InvalidProjectPath,
				null);
		}

		lock (_sync)
		{
			var fileSet = GetFileSet();
			if (!CrossProcessFileLock.TryAcquire(fileSet, lockTimeout, out var heldLock))
			{
				return new ProjectProfileLookupResult(
					ProjectProfileLookupStatus.TemporarilyUnavailable,
					null);
			}

			using var _ = heldLock;
			if (HasOversizedDocument(fileSet))
			{
				return new ProjectProfileLookupResult(
					ProjectProfileLookupStatus.InvalidStorage,
					null);
			}
			if (JsonStorePersistence.ContainsFutureDocument(fileSet, CurrentSchemaVersion))
			{
				return new ProjectProfileLookupResult(
					ProjectProfileLookupStatus.UnsupportedFutureSchema,
					null);
			}
			if (TryLoadFromPath(fileSet.PrimaryPath, out var primaryDb, out var primaryRequiresRewrite))
			{
				if (primaryRequiresRewrite)
					TrySaveInternal(fileSet, primaryDb);
				return ResolveLookup(primaryDb, normalizedPath, fileSet, lockTimeout);
			}

			if (TryLoadFromPath(
				    fileSet.BackupPath,
				    out var backupDb,
				    out var backupRequiresRewrite))
			{
				TrySaveInternal(fileSet, backupDb);
				return ResolveLookup(backupDb, normalizedPath, fileSet, lockTimeout);
			}

			var status = File.Exists(fileSet.PrimaryPath) || File.Exists(fileSet.BackupPath)
				? ProjectProfileLookupStatus.InvalidStorage
				: ProjectProfileLookupStatus.Missing;
			return new ProjectProfileLookupResult(status, null);
		}
	}

	public bool TryDeleteProfile(string localProjectPath)
	{
		if (!TryNormalizePath(localProjectPath, out var normalizedPath))
			return false;

		var selectionDeleted = false;
		lock (_sync)
		{
			var fileSet = GetFileSet();
			if (!CrossProcessFileLock.TryAcquire(fileSet, out var heldLock))
				return false;

			using var _ = heldLock;
			if (HasOversizedDocument(fileSet) ||
			    JsonStorePersistence.ContainsFutureDocument(fileSet, CurrentSchemaVersion))
				return false;
			var db = LoadInternal(fileSet);
			selectionDeleted = !db.Profiles.Remove(normalizedPath) || TrySaveInternal(fileSet, db);
		}

		return selectionDeleted && _persistentMarks.DeleteProject(normalizedPath, TimeSpan.FromSeconds(5));
	}

	public string GetPath()
	{
		return GetFileSet().PrimaryPath;
	}

	private ProjectProfileLookupResult ResolveLookup(
		ProjectProfileDb database,
		string normalizedPath,
		JsonStoreFileSet selectionFileSet,
		TimeSpan lockTimeout)
	{
		if (!database.Profiles.TryGetValue(normalizedPath, out var entry) || entry is null)
		{
			return new ProjectProfileLookupResult(
				ProjectProfileLookupStatus.Missing,
				null);
		}

		var legacyMarks = entry.MarkedSecrets?.ToArray() ?? [];
		var marks = _persistentMarks.MergeLegacy(normalizedPath, legacyMarks, lockTimeout);
		if (!marks.Succeeded || marks.Snapshot is null)
		{
			return new ProjectProfileLookupResult(
				MapMarkStoreStatus(marks.Status),
				null);
		}

		if (legacyMarks.Length > 0)
		{
			entry.MarkedSecrets = null;
			_ = TrySaveInternal(selectionFileSet, database);
		}

		return new ProjectProfileLookupResult(
			ProjectProfileLookupStatus.Found,
			ToProfile(entry, marks.Snapshot.Marks));
	}

	private static ProjectProfileLookupStatus MapMarkStoreStatus(PersistentSecretMarkStoreStatus status) =>
		status switch
		{
			PersistentSecretMarkStoreStatus.TemporarilyUnavailable =>
				ProjectProfileLookupStatus.TemporarilyUnavailable,
			PersistentSecretMarkStoreStatus.InvalidProjectPath =>
				ProjectProfileLookupStatus.InvalidProjectPath,
			PersistentSecretMarkStoreStatus.UnsupportedFutureSchema =>
				ProjectProfileLookupStatus.UnsupportedFutureSchema,
			_ => ProjectProfileLookupStatus.InvalidStorage
		};

	private JsonStoreFileSet GetFileSet()
		=> JsonStoreFileSet.Create(_appDataPathProvider, FolderName, FileName);

	private static bool HasOversizedDocument(JsonStoreFileSet fileSet) =>
		IsOversizedDocument(fileSet.PrimaryPath) || IsOversizedDocument(fileSet.BackupPath);

	private static bool IsOversizedDocument(string path) =>
		File.Exists(path) &&
		!JsonStorePersistence.IsDocumentWithinSizeLimit(
			path,
			ProjectProfileStorageLimits.MaximumJsonBytes);

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
		return JsonStorePersistence.TryWriteAtomic(
			fileSet,
			db,
			SerializerOptions,
			ProjectProfileStorageLimits.MaximumJsonBytes);
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
		profile.SelectedPaths ??= [];
		profile.MarkedSecrets ??= [];

		profile.SelectedRootFolders = profile.SelectedRootFolders
			.Where(IsValidStoredString)
			.Distinct(PathComparer.Default)
			.Take(ProjectProfileStorageLimits.MaximumSelectionItemsPerCollection)
			.ToList();
		profile.SelectedExtensions = profile.SelectedExtensions
			.Where(IsValidStoredString)
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.Take(ProjectProfileStorageLimits.MaximumSelectionItemsPerCollection)
			.ToList();
		profile.SelectedIgnoreOptions = profile.SelectedIgnoreOptions
			.Distinct()
			.Take(Enum.GetValues<IgnoreOptionId>().Length)
			.ToList();
		profile.SelectedPaths = profile.SelectedPaths
			.Where(IsValidStoredPath)
			.Select(PathUtility.NormalizeSeparators)
			.Distinct(PathComparer.Default)
			.OrderBy(static item => item, PathComparer.Default)
			.Take(ProjectProfileStorageLimits.MaximumSelectionItemsPerCollection)
			.ToList();
		profile.MarkedSecrets = NormalizeMarkedSecrets(profile.MarkedSecrets);

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

		// Persisted state maps are authoritative because they retain unchecked rows.
		// Older selected-only documents have no map entries, so each selected value is
		// promoted once to a true entry. TryAdd deliberately preserves an explicit false
		// from a modern profile instead of resurrecting a stale compatibility projection.
		ReconcileSelectedStringValues(
			profile.SelectedRootFolders,
			profile.RootFolderStates,
			PathComparer.Default);
		ReconcileSelectedStringValues(
			profile.SelectedExtensions,
			profile.ExtensionStates,
			StringComparer.OrdinalIgnoreCase);
		ReconcileSelectedIgnoreOptions(
			profile.SelectedIgnoreOptions,
			profile.IgnoreOptionStates);
		NormalizeGitFilteringState(profile.SelectedIgnoreOptions, profile.IgnoreOptionStates);

		profile.UpdatedUtc = NormalizeProfileTimestamp(profile.UpdatedUtc);

		return profile;
	}

	private static PersistedProjectProfile ToPersistedProfile(
		ProjectSelectionProfile profile,
		DateTimeOffset updatedUtc,
		out bool wasTruncated)
	{
		var selectedRootFolders = profile.SelectedRootFolders
			.Where(IsValidStoredString)
			.Distinct(PathComparer.Default)
			.Take(ProjectProfileStorageLimits.MaximumSelectionItemsPerCollection + 1)
			.ToList();
		var selectedExtensions = profile.SelectedExtensions
			.Where(IsValidStoredString)
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.Take(ProjectProfileStorageLimits.MaximumSelectionItemsPerCollection + 1)
			.ToList();
		var selectedIgnoreOptions = profile.SelectedIgnoreOptions
			.Distinct()
			.ToList();
		var rootFolderStates = NormalizeStringStateDictionary(
			profile.RootFolderStates,
			PathComparer.Default,
			out var rootFolderStatesTruncated);
		var extensionStates = NormalizeStringStateDictionary(
			profile.ExtensionStates,
			StringComparer.OrdinalIgnoreCase,
			out var extensionStatesTruncated);
		var ignoreOptionStates = profile.IgnoreOptionStates is null
			? []
			: new Dictionary<IgnoreOptionId, bool>(profile.IgnoreOptionStates);
		ReconcileSelectedStringValues(selectedRootFolders, rootFolderStates, PathComparer.Default);
		ReconcileSelectedStringValues(selectedExtensions, extensionStates, StringComparer.OrdinalIgnoreCase);
		ReconcileSelectedIgnoreOptions(selectedIgnoreOptions, ignoreOptionStates);
		NormalizeGitFilteringState(selectedIgnoreOptions, ignoreOptionStates);
		var selectedPaths = (profile.SelectedPaths ?? [])
			.Where(IsValidStoredPath)
			.Select(PathUtility.NormalizeSeparators)
			.Distinct(PathComparer.Default)
			.OrderBy(static item => item, PathComparer.Default)
			.Take(ProjectProfileStorageLimits.MaximumSelectionItemsPerCollection + 1)
			.ToList();

		wasTruncated = selectedRootFolders.Count > ProjectProfileStorageLimits.MaximumSelectionItemsPerCollection ||
		               selectedExtensions.Count > ProjectProfileStorageLimits.MaximumSelectionItemsPerCollection ||
		               selectedPaths.Count > ProjectProfileStorageLimits.MaximumSelectionItemsPerCollection ||
		               rootFolderStatesTruncated ||
		               extensionStatesTruncated ||
		               rootFolderStates.Count > ProjectProfileStorageLimits.MaximumSelectionItemsPerCollection ||
		               extensionStates.Count > ProjectProfileStorageLimits.MaximumSelectionItemsPerCollection;

		return new PersistedProjectProfile
		{
			SelectedRootFolders = selectedRootFolders,
			SelectedExtensions = selectedExtensions,
			SelectedIgnoreOptions = selectedIgnoreOptions,
			RootFolderStates = rootFolderStates,
			ExtensionStates = extensionStates,
			IgnoreOptionStates = ignoreOptionStates,
			SelectedPaths = selectedPaths,
			MarkedSecrets = null,
			UpdatedUtc = NormalizeProfileTimestamp(updatedUtc)
		};
	}

	private static DateTimeOffset NormalizeProfileTimestamp(DateTimeOffset timestamp) =>
		timestamp <= DateTimeOffset.UnixEpoch || timestamp > MaximumSafeProfileTimestamp
			? DateTimeOffset.UnixEpoch.AddTicks(1)
			: timestamp;

	private static ProjectSelectionProfile ToProfile(
		PersistedProjectProfile profile,
		IReadOnlyCollection<MarkedSecretProfileEntry> marks)
	{
		var rootFolders = new HashSet<string>(profile.SelectedRootFolders, PathComparer.Default);
		var extensions = new HashSet<string>(profile.SelectedExtensions, StringComparer.OrdinalIgnoreCase);
		var selectedIgnoreOptions = profile.SelectedIgnoreOptions.ToList();
		// Local persistence always exposes a complete-map contract. Selected-only input is
		// promoted at the storage boundary, so all surfaces give newly discovered rows the
		// same current default without losing historical positive selections.
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
			IgnoreOptionStates: ignoreStates,
			SelectedPaths: profile.SelectedPaths.ToArray(),
			MarkedSecrets: marks.ToArray());
	}

	private static List<MarkedSecretProfileEntry> NormalizeMarkedSecrets(
		IEnumerable<MarkedSecretProfileEntry>? marks)
	{
		return (marks ?? [])
			.Select(static mark => TryNormalizeMarkedSecret(mark, out var normalized) ? normalized : null)
			.Where(static mark => mark is not null)
			.Select(static mark => mark!)
			.GroupBy(static mark => new PersistentSecretMarkId(
				mark.H,
				mark.Length,
				mark.RelativePath,
				mark.SourceOffset,
				mark.Class))
			.Select(static group => group.First())
			.OrderBy(static mark => mark.H, StringComparer.Ordinal)
			.ThenBy(static mark => mark.Length)
			.ThenBy(static mark => mark.Class)
			.ThenBy(static mark => mark.RelativePath, StringComparer.Ordinal)
			.ThenBy(static mark => mark.SourceOffset)
			.Take(ProjectProfileStorageLimits.MaximumPersistentMarksPerProject)
			.ToList();
	}

	private static bool TryNormalizeMarkedSecret(
		MarkedSecretProfileEntry? mark,
		out MarkedSecretProfileEntry normalized)
	{
		if (mark is null ||
		    !Enum.IsDefined(mark.Class) ||
		    !PersistentSecretIdentity.IsSupported(mark.H) ||
		    mark.Length is < MarkedSecretValueNormalizer.MinimumLength or
			    > MarkedSecretValueNormalizer.MaximumLength)
		{
			normalized = null!;
			return false;
		}

		string? relativePath = null;
		int? sourceOffset = null;
		if (mark.RelativePath is not null || mark.SourceOffset is not null)
		{
			if (string.IsNullOrWhiteSpace(mark.RelativePath) || mark.SourceOffset is null or < 0)
			{
				normalized = null!;
				return false;
			}
			try
			{
				relativePath = ProjectSelectionPath.NormalizeRelative(mark.RelativePath);
			}
			catch (ProjectContextValidationException)
			{
				normalized = null!;
				return false;
			}
			if (relativePath.Length == 0 ||
			    relativePath.Length > ProjectProfileStorageLimits.MaximumMarkedSecretPathLength)
			{
				normalized = null!;
				return false;
			}
			sourceOffset = mark.SourceOffset;
			if (!PersistentSecretIdentity.IsV2(mark.H))
			{
				normalized = null!;
				return false;
			}
		}

		normalized = mark with
		{
			H = mark.H.ToLowerInvariant(),
			Key = NormalizeMarkedSecretKey(mark.Key),
			RelativePath = relativePath,
			SourceOffset = sourceOffset
		};
		return true;
	}

	private static string? NormalizeMarkedSecretKey(string? key)
	{
		if (string.IsNullOrWhiteSpace(key))
			return null;
		var normalized = key.Trim();
		return normalized.Length <= ProjectProfileStorageLimits.MaximumMarkedSecretKeyLength
			? normalized
			: null;
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
		StringComparer comparer) =>
		NormalizeStringStateDictionary(states, comparer, out _);

	private static Dictionary<string, bool> NormalizeStringStateDictionary(
		IEnumerable<KeyValuePair<string, bool>>? states,
		StringComparer comparer,
		out bool wasTruncated)
	{
		wasTruncated = false;
		var normalized = new Dictionary<string, bool>(comparer);
		if (states is null)
			return normalized;

		foreach (var (name, isChecked) in states)
		{
			if (!IsValidStoredString(name))
				continue;
			if (normalized.ContainsKey(name))
			{
				normalized[name] = isChecked;
				continue;
			}
			if (normalized.Count >= ProjectProfileStorageLimits.MaximumSelectionItemsPerCollection)
			{
				wasTruncated = true;
				break;
			}
			normalized[name] = isChecked;
		}

		return normalized;
	}

	private static void ReconcileSelectedStringValues(
		List<string> selectedValues,
		Dictionary<string, bool> states,
		StringComparer comparer)
	{
		foreach (var selectedValue in selectedValues)
			states.TryAdd(selectedValue, true);

		selectedValues.Clear();
		selectedValues.AddRange(states
			.Where(static pair => pair.Value)
			.Select(static pair => pair.Key)
			.OrderBy(static value => value, comparer));
	}

	private static void ReconcileSelectedIgnoreOptions(
		List<IgnoreOptionId> selectedOptions,
		Dictionary<IgnoreOptionId, bool> states)
	{
		foreach (var selectedOption in selectedOptions)
			states.TryAdd(selectedOption, true);

		selectedOptions.Clear();
		selectedOptions.AddRange(states
			.Where(static pair => pair.Value)
			.Select(static pair => pair.Key)
			.OrderBy(static option => (int)option));
	}

	private static void PruneProfiles(ProjectProfileDb db)
	{
		if (db.Profiles.Count <= ProjectProfileStorageLimits.MaximumSelectionProfiles)
			return;

		var staleKeys = db.Profiles
			.OrderBy(pair => pair.Value.UpdatedUtc)
			.Take(db.Profiles.Count - ProjectProfileStorageLimits.MaximumSelectionProfiles)
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
			using var stream = new FileStream(
				path,
				FileMode.Open,
				FileAccess.Read,
				FileShare.ReadWrite | FileShare.Delete);
			if (!JsonStorePersistence.TryParseDocumentWithinSizeLimit(
				stream,
				(int)ProjectProfileStorageLimits.MaximumJsonBytes,
				new JsonDocumentOptions { MaxDepth = 64 },
				out var document))
			{
				return false;
			}
			using (document)
			{
				if (!TryParseDatabase(document.RootElement, out db, out requiresRewrite))
					return false;
				return true;
			}
		}
		catch
		{
			return false;
		}
	}

	private static bool IsValidStoredString(string? value) =>
		!string.IsNullOrWhiteSpace(value) &&
		value.Length <= ProjectProfileStorageLimits.MaximumStateNameLength;

	private static bool IsValidStoredPath(string? value) =>
		!string.IsNullOrEmpty(value) &&
		value.Length <= ProjectProfileStorageLimits.MaximumStateNameLength;

	private static bool TryParseDatabase(
		JsonElement root,
		out ProjectProfileDb database,
		out bool requiresRewrite)
	{
		database = CreateDefaultDb();
		requiresRewrite = false;
		if (root.ValueKind != JsonValueKind.Object)
			return false;

		var sourceSchemaVersion = root.TryGetProperty("schemaVersion", out var schemaElement) &&
		                          schemaElement.TryGetInt32(out var parsedSchema)
			? parsedSchema
			: 0;
		if (sourceSchemaVersion > CurrentSchemaVersion)
			return false;
		if (!root.TryGetProperty("profiles", out var profilesElement))
		{
			requiresRewrite = sourceSchemaVersion != CurrentSchemaVersion;
			return true;
		}
		if (profilesElement.ValueKind != JsonValueKind.Object)
			return false;

		var profiles = new Dictionary<string, PersistedProjectProfile>(PathComparer.Default);
		foreach (var property in profilesElement.EnumerateObject())
		{
			if (!TryNormalizePath(property.Name, out var normalizedPath) ||
			    !TryParseProfile(property.Value, sourceSchemaVersion, out var profile))
			{
				requiresRewrite = true;
				continue;
			}

			profiles[normalizedPath] = profile;
		}

		database = Normalize(
			new ProjectProfileDb
			{
				SchemaVersion = sourceSchemaVersion,
				Profiles = profiles
			},
			sourceSchemaVersion);
		PruneProfiles(database);
		requiresRewrite |= sourceSchemaVersion != CurrentSchemaVersion ||
		                   profiles.Count != database.Profiles.Count;
		return true;
	}

	private static bool TryParseProfile(
		JsonElement element,
		int sourceSchemaVersion,
		out PersistedProjectProfile profile)
	{
		profile = null!;
		if (element.ValueKind != JsonValueKind.Object)
			return false;

		try
		{
			var node = JsonNode.Parse(element.GetRawText())?.AsObject();
			if (node is null)
				return false;
			node.Remove("markedSecrets");
			var parsed = node.Deserialize<PersistedProjectProfile>(SerializerOptions);
			if (parsed is null)
				return false;

			var marks = new List<MarkedSecretProfileEntry>();
			if (element.TryGetProperty("markedSecrets", out var marksElement) &&
			    marksElement.ValueKind == JsonValueKind.Array)
			{
				var markCount = 0;
				foreach (var markElement in marksElement.EnumerateArray())
				{
					if (++markCount > ProjectProfileStorageLimits.MaximumPersistentMarkStatesPerProject)
						return false;
					try
					{
						var mark = markElement.Deserialize<MarkedSecretProfileEntry>(SerializerOptions);
						if (mark is not null)
							marks.Add(mark);
					}
					catch (JsonException)
					{
						// One malformed legacy mark must not invalidate the project profile.
					}
				}
			}

			parsed.MarkedSecrets = marks;
			profile = NormalizePersistedProfile(parsed, sourceSchemaVersion);
			return true;
		}
		catch (Exception exception) when (exception is JsonException or InvalidOperationException)
		{
			return false;
		}
	}
}
