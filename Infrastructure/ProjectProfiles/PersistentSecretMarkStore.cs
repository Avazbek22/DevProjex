using DevProjex.Application.Secrets;
using DevProjex.Infrastructure.Persistence;

namespace DevProjex.Infrastructure.ProjectProfiles;

internal sealed class PersistentSecretMarkStore(
	Func<string> appDataPathProvider,
	TimeSpan? lockTimeout = null)
{
	private const int CurrentSchemaVersion = 1;
	private const string FolderName = "DevProjex";
	private const string FileName = "project-secret-marks.json";
	// GUI callers run off-thread and retry with backoff. A short attempt avoids tying up a worker
	// for seconds when another process currently owns the cross-process transaction lock.
	private static readonly TimeSpan DefaultLockTimeout = TimeSpan.FromMilliseconds(200);

	private static readonly JsonSerializerOptions SerializerOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		WriteIndented = true,
		TypeInfoResolver = InfrastructureJsonSerializerContext.Default
	};

	private readonly object _sync = new();
	private readonly TimeSpan _lockTimeout = lockTimeout ?? DefaultLockTimeout;

	public ValueTask<PersistentSecretMarksLoadResult> LoadAsync(
		string localProjectPath,
		CancellationToken cancellationToken)
	{
		return RunOffCallerThreadAsync(
			() => Load(localProjectPath, _lockTimeout),
			cancellationToken);
	}

	public ValueTask<PersistentSecretMarkWriteResult> AddAsync(
		string localProjectPath,
		MarkedSecretProfileEntry mark,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(mark);
		return RunOffCallerThreadAsync(
			() => ApplyDelta(localProjectPath, PersistentSecretMarkDelta.Add(mark), _lockTimeout),
			cancellationToken);
	}

	public ValueTask<PersistentSecretMarkWriteResult> RemoveAsync(
		string localProjectPath,
		PersistentSecretMarkId markId,
		CancellationToken cancellationToken)
	{
		return RunOffCallerThreadAsync(
			() => ApplyDelta(localProjectPath, PersistentSecretMarkDelta.Remove(markId), _lockTimeout),
			cancellationToken);
	}

	public ValueTask<PersistentSecretMarkWriteResult> ApplyDeltaAsync(
		string localProjectPath,
		PersistentSecretMarkDelta delta,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(delta);
		return RunOffCallerThreadAsync(
			() => ApplyDelta(localProjectPath, delta, _lockTimeout),
			cancellationToken);
	}

	internal PersistentSecretMarksLoadResult Load(string localProjectPath, TimeSpan timeout)
	{
		if (!TryNormalizePath(localProjectPath, out var normalizedPath))
			return new PersistentSecretMarksLoadResult(PersistentSecretMarkStoreStatus.InvalidProjectPath, null);

		lock (_sync)
		{
			var fileSet = GetFileSet();
			if (!CrossProcessFileLock.TryAcquire(fileSet, timeout, out var heldLock))
				return new PersistentSecretMarksLoadResult(PersistentSecretMarkStoreStatus.TemporarilyUnavailable, null);

			using var _ = heldLock;
			var load = LoadDatabase(fileSet);
			if (!load.Succeeded)
				return new PersistentSecretMarksLoadResult(load.Status, null);
			if (load.Database!.InvalidProjects.Contains(normalizedPath))
				return new PersistentSecretMarksLoadResult(PersistentSecretMarkStoreStatus.InvalidStorage, null);

			return new PersistentSecretMarksLoadResult(
				PersistentSecretMarkStoreStatus.Success,
				CreateSnapshot(load.Database, normalizedPath));
		}
	}

	internal PersistentSecretMarksLoadResult MergeLegacy(
		string localProjectPath,
		IReadOnlyCollection<MarkedSecretProfileEntry> legacyMarks,
		TimeSpan timeout)
	{
		if (legacyMarks.Count == 0)
			return Load(localProjectPath, timeout);
		if (!TryNormalizePath(localProjectPath, out var normalizedPath))
			return new PersistentSecretMarksLoadResult(PersistentSecretMarkStoreStatus.InvalidProjectPath, null);

		lock (_sync)
		{
			var fileSet = GetFileSet();
			if (!CrossProcessFileLock.TryAcquire(fileSet, timeout, out var heldLock))
				return new PersistentSecretMarksLoadResult(PersistentSecretMarkStoreStatus.TemporarilyUnavailable, null);

			using var _ = heldLock;
			var load = LoadDatabase(fileSet);
			if (!load.Succeeded)
				return new PersistentSecretMarksLoadResult(load.Status, null);

			var database = load.Database!;
			if (database.InvalidProjects.Contains(normalizedPath))
				return new PersistentSecretMarksLoadResult(PersistentSecretMarkStoreStatus.InvalidStorage, null);
			var project = GetOrCreateProject(database, normalizedPath);
			var changed = false;
			foreach (var mark in NormalizeMarks(legacyMarks))
			{
				if (project.States.Any(existing => HasIdentity(existing, mark.H, mark.Length)))
					continue;
				var delta = PersistentSecretMarkDelta.Add(mark);
				var deltaApplied = ApplyDelta(project, delta, out var limitExceeded);
				if (limitExceeded)
					return new PersistentSecretMarksLoadResult(PersistentSecretMarkStoreStatus.WriteFailed, null);
				changed |= deltaApplied;
			}

			if (changed)
			{
				if (!TryAdvanceRevision(project) || !TrySave(fileSet, database))
					return new PersistentSecretMarksLoadResult(PersistentSecretMarkStoreStatus.WriteFailed, null);
			}

			return new PersistentSecretMarksLoadResult(
				PersistentSecretMarkStoreStatus.Success,
				CreateSnapshot(database, normalizedPath));
		}
	}

	internal bool DeleteProject(string localProjectPath, TimeSpan timeout)
	{
		if (!TryNormalizePath(localProjectPath, out var normalizedPath))
			return false;

		lock (_sync)
		{
			var fileSet = GetFileSet();
			if (!CrossProcessFileLock.TryAcquire(fileSet, timeout, out var heldLock))
				return false;

			using var _ = heldLock;
			var load = LoadDatabase(fileSet);
			if (!load.Succeeded || load.Database is null)
				return load.Status == PersistentSecretMarkStoreStatus.Success;
			if (!load.Database.Projects.Remove(normalizedPath))
				return true;
			return TrySave(fileSet, load.Database);
		}
	}

	internal void ClearAll()
	{
		lock (_sync)
		{
			try
			{
				var fileSet = GetFileSet();
				if (!CrossProcessFileLock.TryAcquire(fileSet, _lockTimeout, out var heldLock))
					return;
				using var _ = heldLock;
				File.Delete(fileSet.PrimaryPath);
				File.Delete(fileSet.BackupPath);
			}
			catch
			{
				// Data reset remains best effort, matching the selection-profile store contract.
			}
		}
	}

	private PersistentSecretMarkWriteResult ApplyDelta(
		string localProjectPath,
		PersistentSecretMarkDelta delta,
		TimeSpan timeout)
	{
		if (!TryNormalizePath(localProjectPath, out var normalizedPath))
			return new PersistentSecretMarkWriteResult(PersistentSecretMarkStoreStatus.InvalidProjectPath, null);

		lock (_sync)
		{
			var fileSet = GetFileSet();
			if (!CrossProcessFileLock.TryAcquire(fileSet, timeout, out var heldLock))
				return new PersistentSecretMarkWriteResult(PersistentSecretMarkStoreStatus.TemporarilyUnavailable, null);

			using var _ = heldLock;
			var load = LoadDatabase(fileSet);
			if (!load.Succeeded)
				return new PersistentSecretMarkWriteResult(load.Status, null);

			var database = load.Database!;
			if (database.InvalidProjects.Contains(normalizedPath))
				return new PersistentSecretMarkWriteResult(PersistentSecretMarkStoreStatus.InvalidStorage, null);
			if (!database.Projects.ContainsKey(normalizedPath) &&
			    database.Projects.Count >= ProjectProfileStorageLimits.MaximumPersistentMarkProjects)
			{
				return new PersistentSecretMarkWriteResult(PersistentSecretMarkStoreStatus.WriteFailed, null);
			}
			var project = GetOrCreateProject(database, normalizedPath);
			var changed = ApplyDelta(project, delta, out var limitExceeded);
			if (limitExceeded)
				return new PersistentSecretMarkWriteResult(PersistentSecretMarkStoreStatus.WriteFailed, null);
			if (changed && (!TryAdvanceRevision(project) || !TrySave(fileSet, database)))
				return new PersistentSecretMarkWriteResult(PersistentSecretMarkStoreStatus.WriteFailed, null);

			return new PersistentSecretMarkWriteResult(
				PersistentSecretMarkStoreStatus.Success,
				CreateSnapshot(database, normalizedPath));
		}
	}

	private static bool ApplyDelta(
		PersistedProjectSecretMarks project,
		PersistentSecretMarkDelta delta,
		out bool limitExceeded)
	{
		limitExceeded = false;
		if (delta.OperationId == Guid.Empty ||
		    delta.IssuedUtcTicks <= 0 ||
		    delta.Kind is not (PersistentSecretMarkDeltaKind.Add or
			    PersistentSecretMarkDeltaKind.Remove or
			    PersistentSecretMarkDeltaKind.Replace))
		{
			return false;
		}
		MarkedSecretProfileEntry? normalizedMark = null;
		if (delta.Kind is PersistentSecretMarkDeltaKind.Add or PersistentSecretMarkDeltaKind.Replace)
		{
			normalizedMark = NormalizeMarks([delta.Mark!]).FirstOrDefault();
			if (normalizedMark is null ||
			    delta.Kind == PersistentSecretMarkDeltaKind.Add &&
			    !HasIdentity(normalizedMark, delta.MarkId.Hash, delta.MarkId.Length))
			{
				return false;
			}
		}
		else if (!IsValidIdentity(delta.MarkId.Hash, delta.MarkId.Length))
		{
			return false;
		}

		if (delta.Kind == PersistentSecretMarkDeltaKind.Replace)
			return ApplyReplacement(project, delta, normalizedMark!, out limitExceeded);

		var state = project.States.FirstOrDefault(existing =>
			HasIdentity(existing, delta.MarkId.Hash, delta.MarkId.Length));
		if (state is not null && CompareOrder(state, delta) >= 0)
			return false;
		if (state is null &&
		    project.States.Count >= ProjectProfileStorageLimits.MaximumPersistentMarkStatesPerProject)
		{
			limitExceeded = true;
			return false;
		}
		if (delta.Kind == PersistentSecretMarkDeltaKind.Add &&
		    (state is null || state.Removed) &&
		    project.States.Count(static existing => !existing.Removed) >=
		    ProjectProfileStorageLimits.MaximumPersistentMarksPerProject)
		{
			limitExceeded = true;
			return false;
		}

		if (state is null)
		{
			state = new PersistedSecretMarkState();
			project.States.Add(state);
		}

		state.Hash = delta.MarkId.Hash.ToLowerInvariant();
		state.Length = delta.MarkId.Length;
		state.Key = delta.Kind == PersistentSecretMarkDeltaKind.Add
			? normalizedMark!.Key
			: null;
		state.Removed = delta.Kind == PersistentSecretMarkDeltaKind.Remove;
		state.IssuedUtcTicks = delta.IssuedUtcTicks;
		state.OperationId = delta.OperationId;
		return true;
	}

	private static bool ApplyReplacement(
		PersistedProjectSecretMarks project,
		PersistentSecretMarkDelta delta,
		MarkedSecretProfileEntry replacement,
		out bool limitExceeded)
	{
		limitExceeded = false;
		if (HasIdentity(replacement, delta.MarkId.Hash, delta.MarkId.Length))
			return false;
		var source = project.States.FirstOrDefault(existing =>
			HasIdentity(existing, delta.MarkId.Hash, delta.MarkId.Length));
		if (source is null || source.Removed || CompareOrder(source, delta) >= 0)
			return false;

		var target = project.States.FirstOrDefault(existing =>
			HasIdentity(existing, replacement.H, replacement.Length));
		if (target is null &&
		    project.States.Count >= ProjectProfileStorageLimits.MaximumPersistentMarkStatesPerProject)
		{
			limitExceeded = true;
			return false;
		}

		source.Removed = true;
		source.Key = null;
		source.IssuedUtcTicks = delta.IssuedUtcTicks;
		source.OperationId = delta.OperationId;
		if (target is not null && CompareOrder(target, delta) >= 0)
			return true;
		if (target is null)
		{
			target = new PersistedSecretMarkState();
			project.States.Add(target);
		}
		target.Hash = replacement.H.ToLowerInvariant();
		target.Length = replacement.Length;
		target.Key = replacement.Key;
		target.Removed = false;
		target.IssuedUtcTicks = delta.IssuedUtcTicks;
		target.OperationId = delta.OperationId;
		return true;
	}

	private static int CompareOrder(PersistedSecretMarkState state, PersistentSecretMarkDelta delta)
	{
		var tickOrder = state.IssuedUtcTicks.CompareTo(delta.IssuedUtcTicks);
		return tickOrder != 0 ? tickOrder : state.OperationId.CompareTo(delta.OperationId);
	}

	private static bool TryAdvanceRevision(PersistedProjectSecretMarks project)
	{
		if (project.Revision == long.MaxValue)
			return false;
		project.Revision++;
		return true;
	}

	private StoreLoadResult LoadDatabase(JsonStoreFileSet fileSet)
	{
		if (TryLoadFromPath(fileSet.PrimaryPath, out var primary))
			return StoreLoadResult.Success(primary);
		if (TryLoadFromPath(fileSet.BackupPath, out var backup))
		{
			if (!TrySave(fileSet, backup))
				return StoreLoadResult.Failure(PersistentSecretMarkStoreStatus.WriteFailed);
			return StoreLoadResult.Success(backup);
		}

		if (File.Exists(fileSet.PrimaryPath) || File.Exists(fileSet.BackupPath))
			return StoreLoadResult.Failure(PersistentSecretMarkStoreStatus.InvalidStorage);
		return StoreLoadResult.Success(CreateDefaultDatabase());
	}

	private static bool TryLoadFromPath(string path, out PersistentSecretMarkDb database)
	{
		database = CreateDefaultDatabase();
		if (!File.Exists(path))
			return false;
		try
		{
			using var stream = new FileStream(
				path,
				FileMode.Open,
				FileAccess.Read,
				FileShare.ReadWrite | FileShare.Delete);
			if (stream.Length > ProjectProfileStorageLimits.MaximumJsonBytes)
				return false;
			using var document = JsonDocument.Parse(
				stream,
				new JsonDocumentOptions { MaxDepth = 64 });
			return TryParseDatabase(document.RootElement, out database);
		}
		catch
		{
			return false;
		}
	}

	private static bool TryParseDatabase(JsonElement root, out PersistentSecretMarkDb database)
	{
		database = CreateDefaultDatabase();
		if (root.ValueKind != JsonValueKind.Object)
			return false;
		var schemaVersion = root.TryGetProperty("schemaVersion", out var schemaElement) &&
		                    schemaElement.TryGetInt32(out var parsedSchema)
			? parsedSchema
			: 0;
		if (schemaVersion > CurrentSchemaVersion)
			return false;
		if (!root.TryGetProperty("projects", out var projectsElement))
			return true;
		if (projectsElement.ValueKind != JsonValueKind.Object)
			return false;

		var parsed = new PersistentSecretMarkDb { SchemaVersion = schemaVersion };
		var projectCount = 0;
		foreach (var property in projectsElement.EnumerateObject())
		{
			if (!TryNormalizePath(property.Name, out var normalizedPath))
				continue;
			if (++projectCount > ProjectProfileStorageLimits.MaximumPersistentMarkProjects)
			{
				parsed.InvalidProjects.Add(normalizedPath);
				continue;
			}
			if (!TryParseProject(property.Value, out var project, out var exceededLimits))
			{
				parsed.InvalidProjects.Add(normalizedPath);
				continue;
			}
			if (exceededLimits)
				parsed.InvalidProjects.Add(normalizedPath);
			parsed.Projects[normalizedPath] = project;
		}

		database = Normalize(parsed);
		return true;
	}

	private static bool TryParseProject(
		JsonElement element,
		out PersistedProjectSecretMarks project,
		out bool exceededLimits)
	{
		project = new PersistedProjectSecretMarks();
		exceededLimits = false;
		if (element.ValueKind != JsonValueKind.Object)
			return false;
		if (element.TryGetProperty("revision", out var revisionElement) &&
		    revisionElement.TryGetInt64(out var revision))
		{
			project.Revision = Math.Max(0, revision);
		}
		if (!element.TryGetProperty("states", out var statesElement))
			return true;
		if (statesElement.ValueKind != JsonValueKind.Array)
			return false;

		var stateCount = 0;
		foreach (var stateElement in statesElement.EnumerateArray())
		{
			if (++stateCount > ProjectProfileStorageLimits.MaximumPersistentMarkStatesPerProject)
			{
				exceededLimits = true;
				continue;
			}
			try
			{
				var state = stateElement.Deserialize<PersistedSecretMarkState>(SerializerOptions);
				if (state is not null)
					project.States.Add(state);
			}
			catch (JsonException)
			{
				// Invalid entries are isolated; valid siblings remain available.
			}
		}

		return true;
	}

	private static PersistentSecretMarkDb Normalize(PersistentSecretMarkDb database)
	{
		database.SchemaVersion = CurrentSchemaVersion;
		database.Projects ??= new Dictionary<string, PersistedProjectSecretMarks>(PathComparer.Default);
		var projects = new Dictionary<string, PersistedProjectSecretMarks>(PathComparer.Default);
		foreach (var (path, value) in database.Projects)
		{
			if (!TryNormalizePath(path, out var normalizedPath) || value is null)
				continue;
			value.Revision = Math.Max(0, value.Revision);
			value.States ??= [];
			value.States = value.States
				.Where(static state => state is not null && IsValidState(state))
				.Select(static state =>
				{
					state.Hash = state.Hash.ToLowerInvariant();
					state.Key = NormalizeMarkedSecretKey(state.Key);
					return state;
				})
				.GroupBy(
					static state => new PersistentSecretMarkId(state.Hash.ToLowerInvariant(), state.Length))
				.Select(static group => group
					.OrderByDescending(static state => state.IssuedUtcTicks)
					.ThenByDescending(static state => state.OperationId)
					.First())
				.OrderBy(static state => state.Hash, StringComparer.Ordinal)
				.ThenBy(static state => state.Length)
				.Take(ProjectProfileStorageLimits.MaximumPersistentMarkStatesPerProject)
				.ToList();
			if (value.States.Count(static state => !state.Removed) >
			    ProjectProfileStorageLimits.MaximumPersistentMarksPerProject)
			{
				database.InvalidProjects.Add(normalizedPath);
			}
			projects[normalizedPath] = value;
		}
		database.Projects = projects;
		return database;
	}

	private static List<MarkedSecretProfileEntry> NormalizeMarks(
		IEnumerable<MarkedSecretProfileEntry>? marks)
	{
		return (marks ?? [])
			.Where(static mark =>
				mark is not null &&
				PersistentSecretIdentity.IsSupported(mark.H) &&
				mark.Length is >= MarkedSecretValueNormalizer.MinimumLength and <= MarkedSecretValueNormalizer.MaximumLength)
			.Select(static mark => mark with
			{
				H = mark.H.ToLowerInvariant(),
				Key = NormalizeMarkedSecretKey(mark.Key)
			})
			.GroupBy(static mark => new PersistentSecretMarkId(mark.H, mark.Length))
			.Select(static group => group.First())
			.OrderBy(static mark => mark.H, StringComparer.Ordinal)
			.ThenBy(static mark => mark.Length)
			.ToList();
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

	private static bool HasIdentity(MarkedSecretProfileEntry mark, string hash, int length) =>
		mark.Length == length && string.Equals(mark.H, hash, StringComparison.OrdinalIgnoreCase);

	private static bool HasIdentity(PersistedSecretMarkState state, string hash, int length) =>
		state.Length == length && string.Equals(state.Hash, hash, StringComparison.OrdinalIgnoreCase);

	private static bool IsValidIdentity(string? hash, int length) =>
		PersistentSecretIdentity.IsSupported(hash) &&
		length is >= MarkedSecretValueNormalizer.MinimumLength and <= MarkedSecretValueNormalizer.MaximumLength;

	private static bool IsValidState(PersistedSecretMarkState state) =>
		IsValidIdentity(state.Hash, state.Length) &&
		state.IssuedUtcTicks > 0 &&
		state.OperationId != Guid.Empty;

	private static PersistedProjectSecretMarks GetOrCreateProject(
		PersistentSecretMarkDb database,
		string normalizedPath)
	{
		if (database.Projects.TryGetValue(normalizedPath, out var existing) && existing is not null)
			return existing;
		var created = new PersistedProjectSecretMarks();
		database.Projects[normalizedPath] = created;
		return created;
	}

	private static PersistentSecretMarksSnapshot CreateSnapshot(
		PersistentSecretMarkDb database,
		string normalizedPath)
	{
		if (!database.Projects.TryGetValue(normalizedPath, out var project) || project is null)
			return PersistentSecretMarksSnapshot.Empty;
		var marks = project.States
			.Where(static state => !state.Removed)
			.Select(static state => new MarkedSecretProfileEntry(state.Hash, state.Key, state.Length))
			.ToArray();
		return new PersistentSecretMarksSnapshot(project.Revision, marks);
	}

	private static PersistentSecretMarkDb CreateDefaultDatabase() => new()
	{
		SchemaVersion = CurrentSchemaVersion,
		Projects = new Dictionary<string, PersistedProjectSecretMarks>(PathComparer.Default)
	};

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

	private JsonStoreFileSet GetFileSet() =>
		JsonStoreFileSet.Create(appDataPathProvider, FolderName, FileName);

	private static bool TrySave(JsonStoreFileSet fileSet, PersistentSecretMarkDb database)
	{
		database.SchemaVersion = CurrentSchemaVersion;
		return JsonStorePersistence.TryWriteAtomicDurable(
			fileSet,
			database,
			SerializerOptions,
			ProjectProfileStorageLimits.MaximumJsonBytes);
	}

	private static async ValueTask<T> RunOffCallerThreadAsync<T>(Func<T> operation, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		return await Task.Run(operation, cancellationToken).ConfigureAwait(false);
	}

	private readonly record struct StoreLoadResult(
		PersistentSecretMarkStoreStatus Status,
		PersistentSecretMarkDb? Database)
	{
		public bool Succeeded => Status == PersistentSecretMarkStoreStatus.Success && Database is not null;

		public static StoreLoadResult Success(PersistentSecretMarkDb database) =>
			new(PersistentSecretMarkStoreStatus.Success, database);

		public static StoreLoadResult Failure(PersistentSecretMarkStoreStatus status) =>
			new(status, null);
	}
}
