using DevProjex.Application.Secrets;
using DevProjex.Application.Context;
using DevProjex.Infrastructure.Persistence;

namespace DevProjex.Infrastructure.ProjectProfiles;

internal sealed class PersistentSecretMarkStore(
	Func<string> appDataPathProvider,
	TimeSpan? lockTimeout = null)
{
	private const int CurrentSchemaVersion = 4;
	private const int AppliedRevisionSchemaVersion = 2;
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
			() => ApplyDelta(
				localProjectPath,
				PersistentSecretMarkDelta.Add(mark, observedRevision: 0),
				_lockTimeout,
				bindToCurrentRevision: true),
			cancellationToken);
	}

	public ValueTask<PersistentSecretMarkWriteResult> RemoveAsync(
		string localProjectPath,
		PersistentSecretMarkId markId,
		CancellationToken cancellationToken)
	{
		return RunOffCallerThreadAsync(
			() => ApplyDelta(
				localProjectPath,
				PersistentSecretMarkDelta.Remove(markId, observedRevision: 0),
				_lockTimeout,
				bindToCurrentRevision: true),
			cancellationToken);
	}

	public ValueTask<PersistentSecretMarkWriteResult> ApplyDeltaAsync(
		string localProjectPath,
		PersistentSecretMarkDelta delta,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(delta);
		return RunOffCallerThreadAsync(
			() => ApplyDelta(localProjectPath, delta, _lockTimeout, bindToCurrentRevision: false),
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
				if (project.States.Any(existing => HasIdentity(existing, CreateIdentity(mark))))
					continue;
				var delta = PersistentSecretMarkDelta.Add(mark, project.AppliedRevision);
				var deltaApplied = ApplyDelta(project, delta, out var limitExceeded);
				if (limitExceeded)
					return new PersistentSecretMarksLoadResult(PersistentSecretMarkStoreStatus.WriteFailed, null);
				changed |= deltaApplied;
			}

			if (changed && !TrySave(fileSet, database))
				return new PersistentSecretMarksLoadResult(PersistentSecretMarkStoreStatus.WriteFailed, null);

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
				if (!HasOversizedDocument(fileSet) &&
				    JsonStorePersistence.ContainsFutureDocument(fileSet, CurrentSchemaVersion))
					return;
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
		TimeSpan timeout,
		bool bindToCurrentRevision)
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
			if (bindToCurrentRevision)
				delta = delta with { ObservedRevision = project.AppliedRevision };
			var changed = ApplyDelta(project, delta, out var limitExceeded);
			if (limitExceeded)
				return new PersistentSecretMarkWriteResult(PersistentSecretMarkStoreStatus.WriteFailed, null);
			if (changed && !TrySave(fileSet, database))
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
		    delta.ObservedRevision < 0 ||
		    delta.ObservedRevision > project.AppliedRevision ||
		    delta.Kind is not (PersistentSecretMarkDeltaKind.Add or
			    PersistentSecretMarkDeltaKind.Remove or
			    PersistentSecretMarkDeltaKind.Replace))
		{
			return false;
		}
		MarkedSecretProfileEntry? normalizedMark = null;
		if (!TryNormalizeIdentity(delta.MarkId, out var normalizedMarkId))
			return false;
		delta = delta with { MarkId = normalizedMarkId };
		if (delta.Kind is PersistentSecretMarkDeltaKind.Add or PersistentSecretMarkDeltaKind.Replace)
		{
			normalizedMark = NormalizeMarks([delta.Mark!]).FirstOrDefault();
			if (normalizedMark is null ||
			    delta.Kind == PersistentSecretMarkDeltaKind.Add &&
			    !HasIdentity(normalizedMark, delta.MarkId))
			{
				return false;
			}
			delta = delta with { Mark = normalizedMark };
		}
		if (project.States.Any(state => state.OperationId == delta.OperationId))
			return false;

		if (delta.Kind == PersistentSecretMarkDeltaKind.Replace)
			return ApplyReplacement(project, delta, normalizedMark!, out limitExceeded);

		var state = project.States.FirstOrDefault(existing =>
			HasIdentity(existing, delta.MarkId));
		if (state is not null && delta.ObservedRevision < state.AppliedRevision)
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
		if (!TryGetNextAppliedRevision(project, out var appliedRevision))
		{
			limitExceeded = true;
			return false;
		}

		state.Hash = delta.MarkId.Hash.ToLowerInvariant();
		state.Length = delta.MarkId.Length;
		state.Class = delta.MarkId.Class;
		state.RelativePath = delta.MarkId.RelativePath;
		state.SourceOffset = delta.MarkId.SourceOffset;
		state.Key = delta.Kind == PersistentSecretMarkDeltaKind.Add
			? normalizedMark!.Key
			: null;
		state.Removed = delta.Kind == PersistentSecretMarkDeltaKind.Remove;
		state.IssuedUtcTicks = delta.IssuedUtcTicks;
		state.OperationId = delta.OperationId;
		state.AppliedRevision = appliedRevision;
		project.AppliedRevision = appliedRevision;
		return true;
	}

	private static bool ApplyReplacement(
		PersistedProjectSecretMarks project,
		PersistentSecretMarkDelta delta,
		MarkedSecretProfileEntry replacement,
		out bool limitExceeded)
	{
		limitExceeded = false;
		if (HasIdentity(replacement, delta.MarkId))
			return false;
		var source = project.States.FirstOrDefault(existing =>
			HasIdentity(existing, delta.MarkId));
		if (source is null || source.Removed || delta.ObservedRevision < source.AppliedRevision)
			return false;

		var target = project.States.FirstOrDefault(existing =>
			HasIdentity(existing, CreateIdentity(replacement)));
		if (target is not null && delta.ObservedRevision < target.AppliedRevision)
			return false;
		if (target is null &&
		    project.States.Count >= ProjectProfileStorageLimits.MaximumPersistentMarkStatesPerProject)
		{
			limitExceeded = true;
			return false;
		}
		if (!TryGetNextAppliedRevision(project, out var appliedRevision))
		{
			limitExceeded = true;
			return false;
		}

		source.Removed = true;
		source.Key = null;
		source.IssuedUtcTicks = delta.IssuedUtcTicks;
		source.OperationId = delta.OperationId;
		source.AppliedRevision = appliedRevision;
		if (target is null)
		{
			target = new PersistedSecretMarkState();
			project.States.Add(target);
		}
		target.Hash = replacement.H.ToLowerInvariant();
		target.Length = replacement.Length;
		target.Class = replacement.Class;
		target.Key = replacement.Key;
		target.RelativePath = replacement.RelativePath;
		target.SourceOffset = replacement.SourceOffset;
		target.Removed = false;
		target.IssuedUtcTicks = delta.IssuedUtcTicks;
		target.OperationId = delta.OperationId;
		target.AppliedRevision = appliedRevision;
		project.AppliedRevision = appliedRevision;
		return true;
	}

	private static bool TryGetNextAppliedRevision(
		PersistedProjectSecretMarks project,
		out long appliedRevision)
	{
		if (project.AppliedRevision == long.MaxValue)
		{
			appliedRevision = 0;
			return false;
		}
		appliedRevision = project.AppliedRevision + 1;
		return true;
	}

	private StoreLoadResult LoadDatabase(JsonStoreFileSet fileSet)
	{
		if (HasOversizedDocument(fileSet))
			return StoreLoadResult.Failure(PersistentSecretMarkStoreStatus.InvalidStorage);
		if (JsonStorePersistence.ContainsFutureDocument(fileSet, CurrentSchemaVersion))
			return StoreLoadResult.Failure(PersistentSecretMarkStoreStatus.UnsupportedFutureSchema);
		if (TryLoadFromPath(fileSet.PrimaryPath, out var primary, out var primaryRequiresRewrite))
		{
			if (primaryRequiresRewrite && !TrySave(fileSet, primary))
				return StoreLoadResult.Failure(PersistentSecretMarkStoreStatus.WriteFailed);
			return StoreLoadResult.Success(primary);
		}
		if (TryLoadFromPath(fileSet.BackupPath, out var backup, out _))
		{
			if (!TrySave(fileSet, backup))
				return StoreLoadResult.Failure(PersistentSecretMarkStoreStatus.WriteFailed);
			return StoreLoadResult.Success(backup);
		}

		if (File.Exists(fileSet.PrimaryPath) || File.Exists(fileSet.BackupPath))
			return StoreLoadResult.Failure(PersistentSecretMarkStoreStatus.InvalidStorage);
		return StoreLoadResult.Success(CreateDefaultDatabase());
	}

	private static bool TryLoadFromPath(
		string path,
		out PersistentSecretMarkDb database,
		out bool requiresRewrite)
	{
		database = CreateDefaultDatabase();
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
				return TryParseDatabase(document.RootElement, out database, out requiresRewrite);
			}
		}
		catch
		{
			return false;
		}
	}

	private static bool TryParseDatabase(
		JsonElement root,
		out PersistentSecretMarkDb database,
		out bool requiresRewrite)
	{
		database = CreateDefaultDatabase();
		requiresRewrite = false;
		if (root.ValueKind != JsonValueKind.Object)
			return false;
		var schemaVersion = root.TryGetProperty("schemaVersion", out var schemaElement) &&
		                    schemaElement.TryGetInt32(out var parsedSchema)
			? parsedSchema
			: 0;
		if (schemaVersion > CurrentSchemaVersion)
			return false;
		requiresRewrite = schemaVersion < CurrentSchemaVersion;
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
			if (!TryParseProject(
				    property.Value,
				    schemaVersion,
				    out var project,
				    out var exceededLimits))
			{
				parsed.InvalidProjects.Add(normalizedPath);
				continue;
			}
			if (exceededLimits)
				parsed.InvalidProjects.Add(normalizedPath);
			parsed.Projects[normalizedPath] = project;
		}

		database = Normalize(parsed, migrateLegacyOrdering: schemaVersion < AppliedRevisionSchemaVersion);
		return true;
	}

	private static bool TryParseProject(
		JsonElement element,
		int schemaVersion,
		out PersistedProjectSecretMarks project,
		out bool exceededLimits)
	{
		project = new PersistedProjectSecretMarks();
		exceededLimits = false;
		if (element.ValueKind != JsonValueKind.Object)
			return false;
		var revisionProperty = schemaVersion < AppliedRevisionSchemaVersion
			? "revision"
			: "appliedRevision";
		if (element.TryGetProperty(revisionProperty, out var revisionElement) &&
		    revisionElement.TryGetInt64(out var revision))
		{
			project.AppliedRevision = Math.Max(0, revision);
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

	private static PersistentSecretMarkDb Normalize(
		PersistentSecretMarkDb database,
		bool migrateLegacyOrdering)
	{
		database.SchemaVersion = CurrentSchemaVersion;
		database.Projects ??= new Dictionary<string, PersistedProjectSecretMarks>(PathComparer.Default);
		var projects = new Dictionary<string, PersistedProjectSecretMarks>(PathComparer.Default);
		foreach (var (path, value) in database.Projects)
		{
			if (!TryNormalizePath(path, out var normalizedPath) || value is null)
				continue;
			value.AppliedRevision = Math.Max(0, value.AppliedRevision);
			value.States ??= [];
			var normalizedStates = value.States
				.Where(state => state is not null && IsValidState(state, migrateLegacyOrdering))
				.Select(state =>
				{
					state.Hash = state.Hash.ToLowerInvariant();
					state.Key = NormalizeMarkedSecretKey(state.Key);
					_ = TryNormalizeScope(
						state.RelativePath,
						state.SourceOffset,
						out var relativePath,
						out var sourceOffset);
					state.RelativePath = relativePath;
					state.SourceOffset = sourceOffset;
					return state;
				})
				.GroupBy(CreateIdentity)
				.Select(group => migrateLegacyOrdering
					? group
						.OrderByDescending(static state => state.IssuedUtcTicks)
						.ThenByDescending(static state => state.OperationId)
						.First()
					: group
						.OrderByDescending(static state => state.AppliedRevision)
						.ThenByDescending(static state => state.OperationId)
						.First())
				.ToList();
			if (migrateLegacyOrdering)
			{
				long appliedRevision = 0;
				foreach (var state in normalizedStates
				         .OrderBy(static state => state.IssuedUtcTicks)
				         .ThenBy(static state => state.OperationId))
				{
					state.AppliedRevision = ++appliedRevision;
				}
				value.AppliedRevision = Math.Max(value.AppliedRevision, appliedRevision);
			}
			else if (normalizedStates.Any(state =>
			         state.AppliedRevision <= 0 || state.AppliedRevision > value.AppliedRevision))
			{
				database.InvalidProjects.Add(normalizedPath);
			}
			value.States = normalizedStates
				.OrderBy(static state => state.Class)
				.ThenBy(static state => state.Hash, StringComparer.Ordinal)
				.ThenBy(static state => state.Length)
				.ThenBy(static state => state.RelativePath, StringComparer.Ordinal)
				.ThenBy(static state => state.SourceOffset)
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
			.Select(static mark => TryNormalizeMark(mark, out var normalized) ? normalized : null)
			.Where(static mark => mark is not null)
			.Select(static mark => mark!)
			.GroupBy(CreateIdentity)
			.Select(static group => group.First())
			.OrderBy(static mark => mark.H, StringComparer.Ordinal)
			.ThenBy(static mark => mark.Length)
			.ThenBy(static mark => mark.Class)
			.ThenBy(static mark => mark.RelativePath, StringComparer.Ordinal)
			.ThenBy(static mark => mark.SourceOffset)
			.ToList();
	}

	private static bool TryNormalizeMark(
		MarkedSecretProfileEntry? mark,
		out MarkedSecretProfileEntry normalized)
	{
		if (mark is null ||
		    !Enum.IsDefined(mark.Class) ||
		    !PersistentSecretIdentity.IsSupported(mark.H) ||
		    mark.Length is < MarkedSecretValueNormalizer.MinimumLength or
			    > MarkedSecretValueNormalizer.MaximumLength ||
		    !TryNormalizeScope(
			    mark.RelativePath,
			    mark.SourceOffset,
			    out var relativePath,
			    out var sourceOffset) ||
		    relativePath is not null && !PersistentSecretIdentity.IsV2(mark.H))
		{
			normalized = null!;
			return false;
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

	private static bool HasIdentity(MarkedSecretProfileEntry mark, PersistentSecretMarkId identity) =>
		CreateIdentity(mark) == identity;

	private static bool HasIdentity(PersistedSecretMarkState state, PersistentSecretMarkId identity) =>
		CreateIdentity(state) == identity;

	private static bool TryNormalizeIdentity(
		PersistentSecretMarkId identity,
		out PersistentSecretMarkId normalized)
	{
		if (!PersistentSecretIdentity.IsSupported(identity.Hash) ||
		    !Enum.IsDefined(identity.Class) ||
		    identity.Length is < MarkedSecretValueNormalizer.MinimumLength or
			    > MarkedSecretValueNormalizer.MaximumLength ||
		    !TryNormalizeScope(
			    identity.RelativePath,
			    identity.SourceOffset,
			    out var relativePath,
			    out var sourceOffset) ||
		    relativePath is not null && !PersistentSecretIdentity.IsV2(identity.Hash))
		{
			normalized = default;
			return false;
		}

		normalized = new PersistentSecretMarkId(
			identity.Hash.ToLowerInvariant(),
			identity.Length,
			relativePath,
			sourceOffset,
			identity.Class);
		return true;
	}

	private static bool TryNormalizeScope(
		string? relativePath,
		int? sourceOffset,
		out string? normalizedPath,
		out int? normalizedOffset)
	{
		normalizedPath = null;
		normalizedOffset = null;
		if (relativePath is null && sourceOffset is null)
			return true;
		if (string.IsNullOrEmpty(relativePath) || sourceOffset is null or < 0)
			return false;
		try
		{
			var candidate = ProjectSelectionPath.NormalizeRelative(relativePath);
			if (candidate.Length == 0 || candidate.Length > ProjectProfileStorageLimits.MaximumMarkedSecretPathLength)
				return false;
			normalizedPath = candidate;
			normalizedOffset = sourceOffset;
			return true;
		}
		catch (ProjectContextValidationException)
		{
			return false;
		}
	}

	private static PersistentSecretMarkId CreateIdentity(MarkedSecretProfileEntry mark) =>
		new(mark.H.ToLowerInvariant(), mark.Length, mark.RelativePath, mark.SourceOffset, mark.Class);

	private static PersistentSecretMarkId CreateIdentity(PersistedSecretMarkState state) =>
		new(state.Hash.ToLowerInvariant(), state.Length, state.RelativePath, state.SourceOffset, state.Class);

	private static bool IsValidState(PersistedSecretMarkState state, bool migrateLegacyOrdering) =>
		Enum.IsDefined(state.Class) &&
		PersistentSecretIdentity.IsSupported(state.Hash) &&
		state.Length is >= MarkedSecretValueNormalizer.MinimumLength and <= MarkedSecretValueNormalizer.MaximumLength &&
		TryNormalizeScope(state.RelativePath, state.SourceOffset, out _, out _) &&
		(state.RelativePath is null || PersistentSecretIdentity.IsV2(state.Hash)) &&
		state.IssuedUtcTicks > 0 &&
		state.OperationId != Guid.Empty &&
		(migrateLegacyOrdering || state.AppliedRevision > 0);

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
			.Select(static state => new MarkedSecretProfileEntry(
				state.Hash,
				state.Key,
				state.Length,
				state.RelativePath,
				state.SourceOffset,
				state.Class))
			.ToArray();
		var stateAppliedRevisions = project.States.ToDictionary(
			CreateIdentity,
			static state => state.AppliedRevision);
		return new PersistentSecretMarksSnapshot(
			project.AppliedRevision,
			marks,
			stateAppliedRevisions);
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

	private static bool HasOversizedDocument(JsonStoreFileSet fileSet) =>
		IsOversizedDocument(fileSet.PrimaryPath) || IsOversizedDocument(fileSet.BackupPath);

	private static bool IsOversizedDocument(string path) =>
		File.Exists(path) &&
		!JsonStorePersistence.IsDocumentWithinSizeLimit(
			path,
			ProjectProfileStorageLimits.MaximumJsonBytes);

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
