using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using DevProjex.Application.Context;
using DevProjex.Application.Diagnostics;

namespace DevProjex.Application.Secrets;

/// <summary>
/// Owns session-only keep-as-is decisions and a bounded cache of compact findings. Source and
/// redacted file contents are operation-local and are never retained by this object.
/// </summary>
public sealed class SecretRedactionSession : IDisposable
{
	private static readonly TimeSpan PreviewMigrationFlushDelay = TimeSpan.FromMilliseconds(250);
	// Selection combinations are transient UI state; 32 entries cover normal toggling while
	// preventing arbitrary selection churn from retaining snapshots for the whole session.
	internal const int MaximumSnapshots = 32;

	private readonly ISecretDetector _detector;
	private readonly IPersistentSecretMarkStore? _persistentMarkStore;
	private readonly IPersistentSecretIdentityProvider? _persistentIdentityProvider;
	private readonly SecretScanCache _scanCache;
	private readonly object _sync = new();
	private readonly HashSet<string> _keptOccurrenceIds = new(StringComparer.Ordinal);
	private readonly Dictionary<PersistentSecretMarkId, MarkedSecretProfileEntry> _durablePersistentMarks = [];
	private readonly Dictionary<PersistentSecretMarkId, long> _durablePersistentMarkAppliedRevisions = [];
	private readonly Dictionary<PersistentSecretMarkId, MarkedSecretProfileEntry> _persistentMarks = [];
	private readonly Dictionary<Guid, PersistentSecretMarkDelta> _pendingPersistentMarkDeltas = [];
	private readonly List<Guid> _pendingPersistentMarkOrder = [];
	private readonly Dictionary<PersistentSecretMarkId, PersistentSecretMarkDelta> _pendingMarkMigrations = [];
	private readonly List<SessionMarkedSecret> _sessionMarks = [];
	private readonly Dictionary<string, PersistentSecretMarkId> _promotedSessionMarkIds =
		new(StringComparer.Ordinal);
	private readonly Dictionary<string, SecretRedactionSnapshot> _snapshots = new(StringComparer.Ordinal);
	private readonly Dictionary<string, LinkedListNode<string>> _snapshotLruNodes = new(StringComparer.Ordinal);
	private readonly LinkedList<string> _snapshotLru = new();
	private SelectionKeyCacheEntry? _selectionKeyCache;
	private CancellationTokenSource _generationCancellation = new();
	private CancellationTokenSource? _previewMigrationFlushCancellation;
	private Task? _previewMigrationFlushTask;
	private Task? _detectorWarmUpTask;
	private string? _activeProjectRoot;
	private long _generation;
	private long _overrideRevision;
	private long _snapshotRevision;
	private int _markedSecretsRevision;
	private string? _persistentMarksProjectPath;
	private long _persistentMarksStoreRevision = -1;
	private int _activeFullContentBuffers;
	private int _peakFullContentBuffers;
	private bool _disposed;

	public SecretRedactionSession(
		ISecretDetector detector,
		IPersistentSecretMarkStore? persistentMarkStore = null,
		IPersistentSecretIdentityProvider? persistentIdentityProvider = null)
		: this(detector, new SecretScanCache(), persistentMarkStore, persistentIdentityProvider)
	{
	}

	internal SecretRedactionSession(
		ISecretDetector detector,
		SecretScanCache scanCache,
		IPersistentSecretMarkStore? persistentMarkStore = null,
		IPersistentSecretIdentityProvider? persistentIdentityProvider = null)
	{
		_detector = detector ?? throw new ArgumentNullException(nameof(detector));
		_scanCache = scanCache ?? throw new ArgumentNullException(nameof(scanCache));
		_persistentMarkStore = persistentMarkStore;
		_persistentIdentityProvider = persistentIdentityProvider;
	}

	public event EventHandler? OverridesChanged;
	public event EventHandler<SecretRedactionSnapshotPublishedEventArgs>? SnapshotPublished;

	public long OutputRevision
	{
		get
		{
			lock (_sync)
				return _overrideRevision;
		}
	}

	/// <summary>
	/// Starts rule-engine initialization once for the process session. It retains compiled rules,
	/// never project content, and can safely overlap selection and preview preparation.
	/// </summary>
	public Task BeginWarmUp()
	{
		lock (_sync)
		{
			ObjectDisposedException.ThrowIf(_disposed, this);
			if (_detectorWarmUpTask is not null)
				return _detectorWarmUpTask;
			_detectorWarmUpTask = Task.Run(() => _detector.WarmUp(CancellationToken.None));
			_ = _detectorWarmUpTask.ContinueWith(
				static task => _ = task.Exception,
				CancellationToken.None,
				TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
				TaskScheduler.Default);
			return _detectorWarmUpTask;
		}
	}

	internal Task EnsureWarmUpAsync(CancellationToken cancellationToken) =>
		BeginWarmUp().WaitAsync(cancellationToken);

	internal async ValueTask RefreshPersistentMarksAsync(
		string projectRoot,
		CancellationToken cancellationToken)
	{
		if (_persistentMarkStore is null)
			return;
		var normalizedProjectRoot = Path.GetFullPath(projectRoot);
		long expectedGeneration;
		lock (_sync)
		{
			ObjectDisposedException.ThrowIf(_disposed, this);
			// Only local profiles bind a session to the machine-local mark store. Portable
			// profiles deliberately carry no persistent marks and must not inherit local state.
			if (!PathComparer.Default.Equals(_persistentMarksProjectPath, normalizedProjectRoot))
				return;
			expectedGeneration = _generation;
		}

		var loaded = await _persistentMarkStore
			.LoadMarksAsync(normalizedProjectRoot, cancellationToken)
			.ConfigureAwait(false);
		if (!loaded.Succeeded || loaded.Snapshot is null)
		{
			throw new IOException(
				$"Persistent secret marks could not be loaded ({loaded.Status}).");
		}
		if (await EnsurePersistentIdentityReadyAsync(
			    loaded.Snapshot.Marks,
			    cancellationToken).ConfigureAwait(false) != PersistentSecretIdentityAvailability.Ready)
		{
			throw new SecretDetectionException("The persistent secret identity key is unavailable.");
		}

		ReplaceMarkedSecretsCore(
			loaded.Snapshot.Marks,
			normalizedProjectRoot,
			loaded.Snapshot.Revision,
			loaded.Snapshot.StateAppliedRevisions,
			expectedGeneration);
	}

	public long PersistentMarksStoreRevision
	{
		get
		{
			lock (_sync)
			{
				ObjectDisposedException.ThrowIf(_disposed, this);
				return Math.Max(0, _persistentMarksStoreRevision);
			}
		}
	}

	public SecretRedactionScope BeginOutput(
		string projectRoot,
		IReadOnlyList<string> orderedFilePaths,
		string transformIdentity = "") =>
		BeginOutput(
			projectRoot,
			ContentSelectionSnapshot.Create(projectRoot, orderedFilePaths),
			transformIdentity);

	public SecretRedactionScope BeginOutput(
		string projectRoot,
		ContentSelectionSnapshot selection,
		string transformIdentity = "")
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
		ArgumentNullException.ThrowIfNull(selection);
		var normalizedProjectRoot = Path.GetFullPath(projectRoot);

		HashSet<string> keptOccurrences;
		long overrideRevision;
		long snapshotRevision;
		long generation;
		CancellationToken generationToken;
		CancellationTokenSource? obsoleteGeneration = null;
		MarkedSecretsMatcher markedSecretsMatcher;
		int markedSecretsRevision;
		lock (_sync)
		{
			ObjectDisposedException.ThrowIf(_disposed, this);
			if (_activeProjectRoot is null)
			{
				_activeProjectRoot = normalizedProjectRoot;
			}
			else if (!PathComparer.Default.Equals(_activeProjectRoot, normalizedProjectRoot))
			{
				obsoleteGeneration = AdvanceGenerationLocked();
				ClearProjectSpecificStateForSwitchLocked(normalizedProjectRoot);
				_activeProjectRoot = normalizedProjectRoot;
			}
			// Selection changes within a project are not a cache lifetime boundary. The bounded LRU
			// keeps deselected files warm without allowing a previous project to repopulate the cache.
			_scanCache.SynchronizeProject(normalizedProjectRoot);
			keptOccurrences = new HashSet<string>(_keptOccurrenceIds, StringComparer.Ordinal);
			overrideRevision = _overrideRevision;
			snapshotRevision = _snapshotRevision;
			generation = _generation;
			generationToken = _generationCancellation.Token;
			markedSecretsRevision = _markedSecretsRevision;
			markedSecretsMatcher = new MarkedSecretsMatcher(
				_persistentMarks.Values,
				_sessionMarks,
				_persistentIdentityProvider,
				(legacyMark, v2Identity) =>
					QueueLegacyMarkMigration(legacyMark, v2Identity, generation));
		}
		CancelAndDispose(obsoleteGeneration);

		return new SecretRedactionScope(
			this,
			normalizedProjectRoot,
			selection.CreateTransformFingerprint(transformIdentity),
			keptOccurrences,
			overrideRevision,
			snapshotRevision,
			markedSecretsMatcher,
			markedSecretsRevision,
			generation,
			generationToken,
			transformIdentity);
	}

	private void ClearProjectSpecificStateForSwitchLocked(string newProjectRoot)
	{
		var marksChanged = _sessionMarks.Count > 0;
		_sessionMarks.Clear();
		_promotedSessionMarkIds.Clear();
		_keptOccurrenceIds.Clear();
		if (!PathComparer.Default.Equals(_persistentMarksProjectPath, newProjectRoot))
		{
			marksChanged |= _persistentMarks.Count > 0;
			_durablePersistentMarks.Clear();
			_durablePersistentMarkAppliedRevisions.Clear();
			_persistentMarks.Clear();
			_pendingPersistentMarkDeltas.Clear();
			_pendingPersistentMarkOrder.Clear();
			_pendingMarkMigrations.Clear();
			_persistentMarksProjectPath = null;
			_persistentMarksStoreRevision = -1;
		}
		if (marksChanged)
			_markedSecretsRevision++;
		_overrideRevision++;
	}

	public IReadOnlyCollection<MarkedSecretProfileEntry> GetMarkedSecrets()
	{
		lock (_sync)
			return _persistentMarks.Values
				.OrderBy(static mark => mark.H, StringComparer.Ordinal)
				.ThenBy(static mark => mark.Length)
				.ToArray();
	}

	public void ReplaceMarkedSecrets(IEnumerable<MarkedSecretProfileEntry>? marks)
		=> ReplaceMarkedSecretsCore(marks, null, -1, null, expectedGeneration: null);

	public void ReplacePersistentMarks(
		string projectRoot,
		PersistentSecretMarksSnapshot snapshot)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
		ArgumentNullException.ThrowIfNull(snapshot);
		ReplaceMarkedSecretsCore(
			snapshot.Marks,
			Path.GetFullPath(projectRoot),
			snapshot.Revision,
			snapshot.StateAppliedRevisions,
			expectedGeneration: null);
	}

	private void ReplaceMarkedSecretsCore(
		IEnumerable<MarkedSecretProfileEntry>? marks,
		string? projectPath,
		long storeRevision,
		IReadOnlyDictionary<PersistentSecretMarkId, long>? stateAppliedRevisions,
		long? expectedGeneration)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		var normalizedMarks = (marks ?? [])
			.Where(static mark => TryNormalizePersistentMark(mark, out _))
			.Select(static mark => NormalizePersistentMark(mark))
			.Take(SecretInspectionLimits.MaximumPersistentMarksPerProject + 1)
			.ToArray();
		if (normalizedMarks.Length > SecretInspectionLimits.MaximumPersistentMarksPerProject)
			throw SecretInspectionBudgetExceededException.PersistentMarks();
		var replacement = normalizedMarks
			.GroupBy(CreatePersistentMarkId)
			.Select(static group => group.First())
			.ToDictionary(CreatePersistentMarkId);
		var replacementAppliedRevisions = NormalizeAppliedRevisions(stateAppliedRevisions);

		lock (_sync)
		{
			ObjectDisposedException.ThrowIf(_disposed, this);
			if (expectedGeneration is { } generation &&
			    (generation != _generation ||
			     !PathComparer.Default.Equals(_persistentMarksProjectPath, projectPath)))
			{
				return;
			}
			if (projectPath is not null &&
			    PathComparer.Default.Equals(_persistentMarksProjectPath, projectPath) &&
			    storeRevision >= 0 &&
			    storeRevision < _persistentMarksStoreRevision)
			{
				return;
			}

			var identityChanged =
				!PathComparer.Default.Equals(_persistentMarksProjectPath, projectPath) ||
				_persistentMarksStoreRevision != storeRevision;
			var stateRevisionsChanged =
				_durablePersistentMarkAppliedRevisions.Count != replacementAppliedRevisions.Count ||
				_durablePersistentMarkAppliedRevisions.Any(pair =>
					!replacementAppliedRevisions.TryGetValue(pair.Key, out var revision) ||
					revision != pair.Value);
			if (!PathComparer.Default.Equals(_persistentMarksProjectPath, projectPath))
			{
				_pendingPersistentMarkDeltas.Clear();
				_pendingPersistentMarkOrder.Clear();
				_pendingMarkMigrations.Clear();
				_promotedSessionMarkIds.Clear();
			}
			if (_durablePersistentMarks.Count == replacement.Count &&
			    _durablePersistentMarks.All(pair => replacement.TryGetValue(pair.Key, out var value) && value == pair.Value) &&
			    !identityChanged &&
			    !stateRevisionsChanged)
			{
				return;
			}

			var marksChanged = _durablePersistentMarks.Count != replacement.Count ||
			                   _durablePersistentMarks.Any(pair =>
				                   !replacement.TryGetValue(pair.Key, out var value) || value != pair.Value);
			_durablePersistentMarks.Clear();
			foreach (var (identity, mark) in replacement)
				_durablePersistentMarks.Add(identity, mark);
			_durablePersistentMarkAppliedRevisions.Clear();
			foreach (var (identity, appliedRevision) in replacementAppliedRevisions)
				_durablePersistentMarkAppliedRevisions.Add(identity, appliedRevision);
			_persistentMarksProjectPath = projectPath;
			_persistentMarksStoreRevision = storeRevision;
			marksChanged |= RebuildEffectivePersistentMarksLocked();
			if (marksChanged)
				AdvanceMarkedSecretsRevisionLocked();
		}
		OverridesChanged?.Invoke(this, EventArgs.Empty);
	}

	public bool AddMarkedSecret(MarkedSecretProfileEntry mark)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		ArgumentNullException.ThrowIfNull(mark);
		if (!TryNormalizePersistentMark(mark, out var normalizedMark))
			throw new ArgumentException("The persistent secret mark is invalid.", nameof(mark));
		mark = normalizedMark;
		bool changed;
		lock (_sync)
		{
			ObjectDisposedException.ThrowIf(_disposed, this);
			var identity = CreatePersistentMarkId(mark);
			if (!_durablePersistentMarks.ContainsKey(identity) &&
			    _durablePersistentMarks.Count >= SecretInspectionLimits.MaximumPersistentMarksPerProject)
			{
				throw SecretInspectionBudgetExceededException.PersistentMarks();
			}
			changed = !_durablePersistentMarks.TryGetValue(identity, out var existing) || existing != mark;
			_durablePersistentMarks[identity] = mark;
			changed |= RebuildEffectivePersistentMarksLocked();
			if (changed)
				AdvanceMarkedSecretsRevisionLocked();
		}
		if (changed)
			OverridesChanged?.Invoke(this, EventArgs.Empty);
		return changed;
	}

	public PersistentMarkStageResult StagePersistentMarkDelta(
		string projectRoot,
		PersistentSecretMarkDelta delta)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
		ArgumentNullException.ThrowIfNull(delta);
		var normalizedProjectRoot = Path.GetFullPath(projectRoot);
		ValidatePendingDelta(delta);
		bool changed;
		lock (_sync)
		{
			ObjectDisposedException.ThrowIf(_disposed, this);
			if (!PathComparer.Default.Equals(_persistentMarksProjectPath, normalizedProjectRoot))
				throw new InvalidOperationException("Persistent mark deltas must target the loaded project.");
			if (delta.ObservedRevision > Math.Max(0, _persistentMarksStoreRevision))
				throw new ArgumentException("The persistent mark delta observes a future store revision.", nameof(delta));
			if (_pendingPersistentMarkDeltas.TryGetValue(delta.OperationId, out var existing))
			{
				if (existing != delta)
					throw new InvalidOperationException("A persistent mark operation ID cannot identify different deltas.");
				return new PersistentMarkStageResult(false, false);
			}

			_pendingPersistentMarkDeltas.Add(delta.OperationId, delta);
			_pendingPersistentMarkOrder.Add(delta.OperationId);
			try
			{
				changed = RebuildEffectivePersistentMarksLocked();
			}
			catch
			{
				RemovePendingDeltaLocked(delta.OperationId);
				throw;
			}
			if (changed)
				AdvanceMarkedSecretsRevisionLocked();
		}
		if (changed)
			OverridesChanged?.Invoke(this, EventArgs.Empty);
		return new PersistentMarkStageResult(true, changed);
	}

	public void AcknowledgePersistentMarkDelta(
		string projectRoot,
		Guid operationId,
		PersistentSecretMarksSnapshot snapshot)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
		ArgumentNullException.ThrowIfNull(snapshot);
		var normalizedProjectRoot = Path.GetFullPath(projectRoot);
		var replacement = NormalizePersistentMarks(snapshot.Marks);
		bool changed;
		lock (_sync)
		{
			ObjectDisposedException.ThrowIf(_disposed, this);
			if (!PathComparer.Default.Equals(_persistentMarksProjectPath, normalizedProjectRoot))
				return;

			_pendingPersistentMarkDeltas.TryGetValue(operationId, out var acknowledgedDelta);
			RemovePendingDeltaLocked(operationId);
			if (snapshot.Revision >= _persistentMarksStoreRevision)
			{
				_durablePersistentMarks.Clear();
				foreach (var (identity, mark) in replacement)
					_durablePersistentMarks.Add(identity, mark);
				_durablePersistentMarkAppliedRevisions.Clear();
				foreach (var (identity, appliedRevision) in NormalizeAppliedRevisions(
					         snapshot.StateAppliedRevisions))
				{
					_durablePersistentMarkAppliedRevisions.Add(identity, appliedRevision);
				}
				_persistentMarksStoreRevision = snapshot.Revision;
			}
			if (acknowledgedDelta?.Kind is PersistentSecretMarkDeltaKind.Remove or
			    PersistentSecretMarkDeltaKind.Replace)
			{
				RemovePromotedSessionMarkAliasesLocked(acknowledgedDelta.MarkId);
			}
			changed = RebuildEffectivePersistentMarksLocked();
			if (changed)
				AdvanceMarkedSecretsRevisionLocked();
		}
		if (changed)
			OverridesChanged?.Invoke(this, EventArgs.Empty);
	}

	public bool RollbackPendingPersistentMarkDelta(string projectRoot, Guid operationId)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
		var normalizedProjectRoot = Path.GetFullPath(projectRoot);
		bool changed;
		lock (_sync)
		{
			ObjectDisposedException.ThrowIf(_disposed, this);
			if (!PathComparer.Default.Equals(_persistentMarksProjectPath, normalizedProjectRoot) ||
			    !_pendingPersistentMarkDeltas.TryGetValue(operationId, out var delta) ||
			    !RemovePendingDeltaLocked(operationId))
			{
				return false;
			}
			if (delta.Kind == PersistentSecretMarkDeltaKind.Add)
				RemovePromotedSessionMarkAliasesLocked(delta.MarkId);
			changed = RebuildEffectivePersistentMarksLocked();
			if (changed)
				AdvanceMarkedSecretsRevisionLocked();
		}
		if (changed)
			OverridesChanged?.Invoke(this, EventArgs.Empty);
		return changed;
	}

	public bool TryCreatePersistentMarkedSecret(
		MarkedSecretValue value,
		string? key,
		out MarkedSecretProfileEntry mark)
	{
		ArgumentNullException.ThrowIfNull(value);
		string identity;
		lock (_sync)
		{
			ObjectDisposedException.ThrowIf(_disposed, this);
			if (!PersistentSecretIdentity.TryCreateV2(
				    _persistentIdentityProvider,
				    value.NormalizedValue,
				    out identity))
			{
				mark = null!;
				return false;
			}
		}

		mark = NormalizePersistentMark(new MarkedSecretProfileEntry(identity, key, value.Length));
		return true;
	}

	public ValueTask<MarkedSecretProfileEntry?> CreatePersistentMarkedSecretAsync(
		MarkedSecretValue value,
		string? key,
		CancellationToken cancellationToken = default) =>
		CreatePersistentMarkedSecretCoreAsync(
			value,
			key,
			relativePath: null,
			sourceOffset: null,
			cancellationToken);

	public ValueTask<MarkedSecretProfileEntry?> CreatePersistentSourceMarkedSecretAsync(
		MarkedSecretValue value,
		string? key,
		string relativePath,
		int sourceOffset,
		CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
		ArgumentOutOfRangeException.ThrowIfNegative(sourceOffset);
		return CreatePersistentMarkedSecretCoreAsync(
			value,
			key,
			relativePath,
			sourceOffset,
			cancellationToken);
	}

	private async ValueTask<MarkedSecretProfileEntry?> CreatePersistentMarkedSecretCoreAsync(
		MarkedSecretValue value,
		string? key,
		string? relativePath,
		int? sourceOffset,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(value);
		if (_persistentIdentityProvider is null ||
		    await _persistentIdentityProvider
			    .EnsureAvailableAsync(cancellationToken)
			    .ConfigureAwait(false) != PersistentSecretIdentityAvailability.Ready)
		{
			return null;
		}

		lock (_sync)
		{
			ObjectDisposedException.ThrowIf(_disposed, this);
			if (!PersistentSecretIdentity.TryCreateV2(
				    _persistentIdentityProvider,
				    value.NormalizedValue,
				    out var identity))
			{
				return null;
			}

			var candidate = new MarkedSecretProfileEntry(
				identity,
				key,
				value.Length,
				relativePath,
				sourceOffset);
			return TryNormalizePersistentMark(candidate, out var normalized)
				? normalized
				: null;
		}
	}

	public async ValueTask<PersistentSecretIdentityAvailability> EnsurePersistentIdentityReadyAsync(
		IEnumerable<MarkedSecretProfileEntry> marks,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(marks);
		var snapshot = marks as IReadOnlyCollection<MarkedSecretProfileEntry> ?? marks.ToArray();
		if (snapshot.Count == 0)
			return PersistentSecretIdentityAvailability.Ready;
		var hasV2 = snapshot.Any(static mark => mark is not null && PersistentSecretIdentity.IsV2(mark.H));
		var availability = _persistentIdentityProvider is null
			? PersistentSecretIdentityAvailability.PermanentlyUnavailable
			: await _persistentIdentityProvider
				.EnsureAvailableAsync(cancellationToken)
				.ConfigureAwait(false);
		return availability == PersistentSecretIdentityAvailability.Ready || hasV2
			? availability
			: PersistentSecretIdentityAvailability.Ready;
	}

	internal ValueTask<PersistentSecretIdentityAvailability> EnsureCurrentPersistentIdentityReadyAsync(
		CancellationToken cancellationToken = default)
	{
		MarkedSecretProfileEntry[] marks;
		lock (_sync)
		{
			ObjectDisposedException.ThrowIf(_disposed, this);
			marks = _persistentMarks.Values.ToArray();
		}
		return EnsurePersistentIdentityReadyAsync(marks, cancellationToken);
	}

	public PersistentMarkStageResult TryPromoteSessionMarkToPendingPersistentMark(
		string projectRoot,
		string relativePath,
		int sourceOffset,
		MarkedSecretValue value,
		PersistentSecretMarkDelta delta)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
		ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
		ArgumentOutOfRangeException.ThrowIfNegative(sourceOffset);
		ArgumentNullException.ThrowIfNull(value);
		ArgumentNullException.ThrowIfNull(delta);
		ValidatePendingDelta(delta);
		if (delta.Kind != PersistentSecretMarkDeltaKind.Add)
			throw new ArgumentException("Only an add delta can replace a session source anchor.", nameof(delta));
		var normalizedProjectRoot = Path.GetFullPath(projectRoot);
		var anchor = new SessionMarkedSecret(
			relativePath.Replace('\\', '/'),
			sourceOffset,
			value.Length,
			value.Hash);
		lock (_sync)
		{
			ObjectDisposedException.ThrowIf(_disposed, this);
			if (delta.ObservedRevision > Math.Max(0, _persistentMarksStoreRevision))
				throw new ArgumentException("The persistent mark delta observes a future store revision.", nameof(delta));
			if (!PathComparer.Default.Equals(_persistentMarksProjectPath, normalizedProjectRoot) ||
			    !_sessionMarks.Remove(anchor))
			{
				return new PersistentMarkStageResult(false, false);
			}
			if (!_pendingPersistentMarkDeltas.TryAdd(delta.OperationId, delta))
			{
				_sessionMarks.Add(anchor);
				return new PersistentMarkStageResult(false, false);
			}
			_pendingPersistentMarkOrder.Add(delta.OperationId);
			try
			{
				RebuildEffectivePersistentMarksLocked();
				_promotedSessionMarkIds[anchor.Id] = NormalizeMarkId(delta.MarkId);
			}
			catch
			{
				RemovePendingDeltaLocked(delta.OperationId);
				_sessionMarks.Add(anchor);
				throw;
			}
			AdvanceMarkedSecretsRevisionLocked();
		}
		OverridesChanged?.Invoke(this, EventArgs.Empty);
		return new PersistentMarkStageResult(true, true);
	}

	public bool TryResolvePromotedPersistentMarkId(
		string sessionMarkId,
		out PersistentSecretMarkId persistentMarkId)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(sessionMarkId);
		lock (_sync)
		{
			ObjectDisposedException.ThrowIf(_disposed, this);
			return _promotedSessionMarkIds.TryGetValue(sessionMarkId, out persistentMarkId);
		}
	}

	internal int PendingPersistentMarkCount
	{
		get
		{
			lock (_sync)
				return _pendingPersistentMarkDeltas.Count;
		}
	}

	private static bool TryNormalizePersistentMark(
		MarkedSecretProfileEntry? mark,
		out MarkedSecretProfileEntry normalized)
	{
		if (mark is null ||
		    !PersistentSecretIdentity.IsSupported(mark.H) ||
		    mark.Length is < MarkedSecretValueNormalizer.MinimumLength or
			    > MarkedSecretValueNormalizer.MaximumLength ||
		    !TryNormalizePersistentMarkScope(
			    mark.RelativePath,
			    mark.SourceOffset,
			    out var relativePath,
			    out var sourceOffset) ||
		    relativePath is not null && !PersistentSecretIdentity.IsV2(mark.H))
		{
			normalized = null!;
			return false;
		}

		normalized = NormalizePersistentMark(mark with
		{
			RelativePath = relativePath,
			SourceOffset = sourceOffset
		});
		return true;
	}

	private static MarkedSecretProfileEntry NormalizePersistentMark(MarkedSecretProfileEntry mark)
	{
		var key = string.IsNullOrWhiteSpace(mark.Key) ? null : mark.Key.Trim();
		if (key?.Length > SecretInspectionLimits.MaximumPersistentMarkKeyLength)
			key = null;
		return mark with { H = mark.H.ToLowerInvariant(), Key = key };
	}

	private static bool TryNormalizePersistentMarkScope(
		string? relativePath,
		int? sourceOffset,
		out string? normalizedPath,
		out int? normalizedOffset)
	{
		normalizedPath = null;
		normalizedOffset = null;
		if (relativePath is null && sourceOffset is null)
			return true;
		if (string.IsNullOrWhiteSpace(relativePath) || sourceOffset is null or < 0)
			return false;
		try
		{
			normalizedPath = ProjectSelectionPath.NormalizeRelative(relativePath);
			if (normalizedPath.Length == 0 ||
			    normalizedPath.Length > SecretInspectionLimits.MaximumPersistentMarkPathLength)
				return false;
			normalizedOffset = sourceOffset;
			return true;
		}
		catch (ProjectContextValidationException)
		{
			return false;
		}
	}

	private static Dictionary<PersistentSecretMarkId, MarkedSecretProfileEntry> NormalizePersistentMarks(
		IEnumerable<MarkedSecretProfileEntry>? marks)
	{
		var normalizedMarks = (marks ?? [])
			.Where(static mark => TryNormalizePersistentMark(mark, out _))
			.Select(static mark => NormalizePersistentMark(mark))
			.Take(SecretInspectionLimits.MaximumPersistentMarksPerProject + 1)
			.ToArray();
		if (normalizedMarks.Length > SecretInspectionLimits.MaximumPersistentMarksPerProject)
			throw SecretInspectionBudgetExceededException.PersistentMarks();
		return normalizedMarks
			.GroupBy(CreatePersistentMarkId)
			.Select(static group => group.First())
			.ToDictionary(CreatePersistentMarkId);
	}

	private static void ValidatePendingDelta(PersistentSecretMarkDelta delta)
	{
		if (delta.OperationId == Guid.Empty)
			throw new ArgumentException("The persistent mark operation ID is required.", nameof(delta));
		if (delta.IssuedUtcTicks <= 0 || delta.ObservedRevision < 0)
			throw new ArgumentException("The persistent mark operation metadata is invalid.", nameof(delta));
		switch (delta.Kind)
		{
			case PersistentSecretMarkDeltaKind.Add:
				if (!TryNormalizePersistentMark(delta.Mark, out var added) ||
				    !HasIdentity(added, delta.MarkId))
				{
					throw new ArgumentException("The persistent mark add delta is invalid.", nameof(delta));
				}
				break;
			case PersistentSecretMarkDeltaKind.Remove:
				if (!IsValidPersistentMarkId(delta.MarkId) || delta.Mark is not null)
					throw new ArgumentException("The persistent mark remove delta is invalid.", nameof(delta));
				break;
			case PersistentSecretMarkDeltaKind.Replace:
				if (!IsValidPersistentMarkId(delta.MarkId) ||
				    !TryNormalizePersistentMark(delta.Mark, out var replacement) ||
				    HasIdentity(replacement, delta.MarkId))
				{
					throw new ArgumentException("The persistent mark replacement delta is invalid.", nameof(delta));
				}
				break;
			default:
				throw new ArgumentOutOfRangeException(nameof(delta));
		}
	}

	private static bool HasIdentity(MarkedSecretProfileEntry mark, PersistentSecretMarkId markId) =>
		CreatePersistentMarkId(mark) == NormalizeMarkId(markId);

	private static bool IsValidPersistentMarkId(PersistentSecretMarkId markId) =>
		PersistentSecretIdentity.IsSupported(markId.Hash) &&
		markId.Length is >= MarkedSecretValueNormalizer.MinimumLength and <= MarkedSecretValueNormalizer.MaximumLength &&
		TryNormalizePersistentMarkScope(
			markId.RelativePath,
			markId.SourceOffset,
			out _,
			out _) &&
		(markId.RelativePath is null || PersistentSecretIdentity.IsV2(markId.Hash));

	private bool RebuildEffectivePersistentMarksLocked()
	{
		var effective = new Dictionary<PersistentSecretMarkId, MarkedSecretProfileEntry>(
			_durablePersistentMarks);
		List<Guid>? staleOperations = null;
		foreach (var operationId in _pendingPersistentMarkOrder)
		{
			if (!_pendingPersistentMarkDeltas.TryGetValue(operationId, out var delta))
				continue;
			if (IsPendingDeltaStaleLocked(delta))
			{
				(staleOperations ??= []).Add(operationId);
				continue;
			}
			ApplyPendingDelta(effective, delta);
			if (effective.Count > SecretInspectionLimits.MaximumPersistentMarksPerProject)
				throw SecretInspectionBudgetExceededException.PersistentMarks();
		}
		if (staleOperations is not null)
		{
			foreach (var operationId in staleOperations)
				RemovePendingDeltaLocked(operationId);
		}

		if (_persistentMarks.Count == effective.Count &&
		    _persistentMarks.All(pair => effective.TryGetValue(pair.Key, out var value) && value == pair.Value))
		{
			return false;
		}

		_persistentMarks.Clear();
		foreach (var (identity, mark) in effective)
			_persistentMarks.Add(identity, mark);
		return true;
	}

	private bool IsPendingDeltaStaleLocked(PersistentSecretMarkDelta delta)
	{
		if (delta.Kind == PersistentSecretMarkDeltaKind.Add)
			return false;
		var sourceId = NormalizeMarkId(delta.MarkId);
		if (GetDurableAppliedRevisionLocked(sourceId) > delta.ObservedRevision)
			return true;
		if (delta.Kind != PersistentSecretMarkDeltaKind.Replace)
			return false;
		var target = delta.Mark!;
		return GetDurableAppliedRevisionLocked(
			CreatePersistentMarkId(target)) > delta.ObservedRevision;
	}

	private long GetDurableAppliedRevisionLocked(PersistentSecretMarkId identity) =>
		_durablePersistentMarkAppliedRevisions.GetValueOrDefault(identity);

	private static PersistentSecretMarkId NormalizeMarkId(PersistentSecretMarkId identity) =>
		TryNormalizePersistentMarkScope(
			identity.RelativePath,
			identity.SourceOffset,
			out var relativePath,
			out var sourceOffset)
			? new PersistentSecretMarkId(
				identity.Hash.ToLowerInvariant(),
				identity.Length,
				relativePath,
				sourceOffset)
			: identity;

	private static PersistentSecretMarkId CreatePersistentMarkId(MarkedSecretProfileEntry mark) =>
		new(mark.H.ToLowerInvariant(), mark.Length, mark.RelativePath, mark.SourceOffset);

	private static Dictionary<PersistentSecretMarkId, long> NormalizeAppliedRevisions(
		IReadOnlyDictionary<PersistentSecretMarkId, long>? revisions)
	{
		var normalized = new Dictionary<PersistentSecretMarkId, long>();
		if (revisions is null)
			return normalized;
		foreach (var (identity, appliedRevision) in revisions)
		{
			if (appliedRevision > 0 && IsValidPersistentMarkId(identity))
				normalized[NormalizeMarkId(identity)] = appliedRevision;
		}
		return normalized;
	}

	private static void ApplyPendingDelta(
		Dictionary<PersistentSecretMarkId, MarkedSecretProfileEntry> marks,
		PersistentSecretMarkDelta delta)
	{
		var sourceId = NormalizeMarkId(delta.MarkId);
		switch (delta.Kind)
		{
			case PersistentSecretMarkDeltaKind.Add:
			{
				var mark = NormalizePersistentMark(delta.Mark!);
				marks[CreatePersistentMarkId(mark)] = mark;
				break;
			}
			case PersistentSecretMarkDeltaKind.Remove:
				marks.Remove(sourceId);
				break;
			case PersistentSecretMarkDeltaKind.Replace:
			{
				marks.Remove(sourceId);
				var mark = NormalizePersistentMark(delta.Mark!);
				marks[CreatePersistentMarkId(mark)] = mark;
				break;
			}
		}
	}

	private bool RemovePendingDeltaLocked(Guid operationId)
	{
		if (!_pendingPersistentMarkDeltas.Remove(operationId))
			return false;
		_pendingPersistentMarkOrder.Remove(operationId);
		return true;
	}

	private void RemovePromotedSessionMarkAliasesLocked(PersistentSecretMarkId markId)
	{
		var normalizedMarkId = NormalizeMarkId(markId);
		var aliases = _promotedSessionMarkIds
			.Where(pair => pair.Value == normalizedMarkId)
			.Select(static pair => pair.Key)
			.ToArray();
		foreach (var alias in aliases)
			_promotedSessionMarkIds.Remove(alias);
	}

	public bool RemoveMarkedSecret(string hash)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(hash);
		return RemoveManualSecret(hash, null).PersistentMarkRemoved;
	}

	public bool RemoveMarkedSecret(PersistentSecretMarkId markId)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(markId.Hash);
		return RemoveManualSecret(markId, null).PersistentMarkRemoved;
	}

	internal async ValueTask FlushPendingPersistentMarkMigrationsAsync(
		string projectRoot,
		CancellationToken cancellationToken)
	{
		if (_persistentMarkStore is null)
			return;
		var normalizedProjectRoot = Path.GetFullPath(projectRoot);
		PersistentSecretMarkDelta[] migrations;
		long expectedGeneration;
		lock (_sync)
		{
			ObjectDisposedException.ThrowIf(_disposed, this);
			if (!PathComparer.Default.Equals(_persistentMarksProjectPath, normalizedProjectRoot) ||
			    _pendingMarkMigrations.Count == 0)
			{
				return;
			}
			expectedGeneration = _generation;
			migrations = _pendingMarkMigrations.Values.ToArray();
		}

		PersistentSecretMarksSnapshot? latest = null;
		foreach (var migration in migrations)
		{
			var result = await _persistentMarkStore
				.ApplyMarkDeltaAsync(projectRoot, migration, cancellationToken)
				.ConfigureAwait(false);
			if (!result.Succeeded || result.Snapshot is null)
				throw new IOException($"Persistent secret mark migration failed ({result.Status}).");
			latest = result.Snapshot;
			lock (_sync)
			{
				if (_pendingMarkMigrations.TryGetValue(migration.MarkId, out var pending) &&
				    pending.OperationId == migration.OperationId)
				{
					_pendingMarkMigrations.Remove(migration.MarkId);
				}
			}
		}
		if (latest is not null)
		{
			ReplaceMarkedSecretsCore(
				latest.Marks,
				normalizedProjectRoot,
				latest.Revision,
				latest.StateAppliedRevisions,
				expectedGeneration);
		}
	}

	internal void SchedulePendingPersistentMarkMigrationsAfterPreview(string projectRoot)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
		var normalizedProjectRoot = Path.GetFullPath(projectRoot);
		CancellationTokenSource? obsoleteSchedule;
		CancellationTokenSource schedule;
		long expectedGeneration;
		lock (_sync)
		{
			ObjectDisposedException.ThrowIf(_disposed, this);
			if (_persistentMarkStore is null ||
			    _pendingMarkMigrations.Count == 0 ||
			    !PathComparer.Default.Equals(_persistentMarksProjectPath, normalizedProjectRoot))
			{
				return;
			}

			expectedGeneration = _generation;
			schedule = CancellationTokenSource.CreateLinkedTokenSource(_generationCancellation.Token);
			obsoleteSchedule = _previewMigrationFlushCancellation;
			_previewMigrationFlushCancellation = schedule;
		}
		CancelWithoutDispose(obsoleteSchedule);
		var flushTask = FlushPreviewMigrationsAfterDelayAsync(
			normalizedProjectRoot,
			expectedGeneration,
			schedule);
		lock (_sync)
		{
			if (ReferenceEquals(_previewMigrationFlushCancellation, schedule))
				_previewMigrationFlushTask = flushTask;
		}
	}

	internal Task WaitForPreviewMigrationFlushAsync()
	{
		lock (_sync)
			return _previewMigrationFlushTask ?? Task.CompletedTask;
	}

	private async Task FlushPreviewMigrationsAfterDelayAsync(
		string projectRoot,
		long expectedGeneration,
		CancellationTokenSource schedule)
	{
		try
		{
			await Task.Delay(PreviewMigrationFlushDelay, schedule.Token).ConfigureAwait(false);
			lock (_sync)
			{
				if (_disposed ||
				    expectedGeneration != _generation ||
				    !ReferenceEquals(_previewMigrationFlushCancellation, schedule) ||
				    !PathComparer.Default.Equals(_persistentMarksProjectPath, projectRoot))
				{
					return;
				}
			}
			await FlushPendingPersistentMarkMigrationsAsync(projectRoot, schedule.Token).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (schedule.IsCancellationRequested)
		{
			// A newer preview or generation owns the next migration attempt.
		}
		catch (Exception)
		{
			// Legacy identities remain valid; a later preview or strict output safely retries.
		}
		finally
		{
			lock (_sync)
			{
				if (ReferenceEquals(_previewMigrationFlushCancellation, schedule))
				{
					_previewMigrationFlushCancellation = null;
					_previewMigrationFlushTask = null;
				}
			}
			schedule.Dispose();
		}
	}

	private void QueueLegacyMarkMigration(
		MarkedSecretProfileEntry legacyMark,
		string v2Identity,
		long expectedGeneration)
	{
		var existingId = CreatePersistentMarkId(legacyMark);
		lock (_sync)
		{
			if (_disposed ||
			    expectedGeneration != _generation ||
			    _persistentMarkStore is null ||
			    _persistentMarksProjectPath is null)
				return;
			_pendingMarkMigrations.TryAdd(
				existingId,
				PersistentSecretMarkDelta.Replace(
					existingId,
					legacyMark with { H = v2Identity },
					Math.Max(0, _persistentMarksStoreRevision)));
		}
	}

	public bool AddSessionMarkedSecret(
		string relativePath,
		int sourceOffset,
		MarkedSecretValue value)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
		ArgumentOutOfRangeException.ThrowIfNegative(sourceOffset);
		ArgumentNullException.ThrowIfNull(value);
		var mark = new SessionMarkedSecret(
			relativePath.Replace('\\', '/'),
			sourceOffset,
			value.Length,
			value.Hash);
		bool added;
		lock (_sync)
		{
			ObjectDisposedException.ThrowIf(_disposed, this);
			added = !_sessionMarks.Contains(mark);
			if (added)
			{
				_sessionMarks.Add(mark);
				AdvanceMarkedSecretsRevisionLocked();
			}
		}
		if (added)
			OverridesChanged?.Invoke(this, EventArgs.Empty);
		return added;
	}

	public bool RemoveSessionMarkedSecret(string sessionMarkId)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(sessionMarkId);
		return RemoveManualSecret((string?)null, sessionMarkId).SessionMarkRemoved;
	}

	public ManualSecretMarkRemovalResult RemoveManualSecret(
		string? persistentMarkHash,
		string? sessionMarkId) =>
		RemoveManualSecretCore(
			persistentMarkHash,
			persistentMarkLength: null,
			persistentRelativePath: null,
			persistentSourceOffset: null,
			matchPersistentScope: false,
			sessionMarkId);

	public ManualSecretMarkRemovalResult RemoveManualSecret(
		PersistentSecretMarkId? persistentMarkId,
		string? sessionMarkId) =>
		RemoveManualSecretCore(
			persistentMarkId?.Hash,
			persistentMarkId?.Length,
			persistentMarkId?.RelativePath,
			persistentMarkId?.SourceOffset,
			matchPersistentScope: persistentMarkId is not null,
			sessionMarkId);

	private ManualSecretMarkRemovalResult RemoveManualSecretCore(
		string? persistentMarkHash,
		int? persistentMarkLength,
		string? persistentRelativePath,
		int? persistentSourceOffset,
		bool matchPersistentScope,
		string? sessionMarkId)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		var persistentRemoved = false;
		var sessionRemoved = false;
		lock (_sync)
		{
			ObjectDisposedException.ThrowIf(_disposed, this);
			if (!string.IsNullOrWhiteSpace(persistentMarkHash))
			{
				var identities = _durablePersistentMarks.Keys
					.Where(identity => string.Equals(
						identity.Hash,
						persistentMarkHash,
						StringComparison.OrdinalIgnoreCase) &&
						(persistentMarkLength is null || identity.Length == persistentMarkLength) &&
						(!matchPersistentScope ||
						 PathComparer.Default.Equals(identity.RelativePath, persistentRelativePath) &&
						 identity.SourceOffset == persistentSourceOffset))
					.ToArray();
				foreach (var identity in identities)
				{
					persistentRemoved |= _durablePersistentMarks.Remove(identity);
					_durablePersistentMarkAppliedRevisions.Remove(identity);
					RemovePromotedSessionMarkAliasesLocked(identity);
				}
			}
			if (!string.IsNullOrWhiteSpace(sessionMarkId))
			{
				sessionRemoved = _sessionMarks.RemoveAll(mark =>
					string.Equals(mark.Id, sessionMarkId, StringComparison.Ordinal)) > 0;
			}
			if (persistentRemoved)
				persistentRemoved |= RebuildEffectivePersistentMarksLocked();
			if (persistentRemoved || sessionRemoved)
				AdvanceMarkedSecretsRevisionLocked();
		}
		if (persistentRemoved || sessionRemoved)
			OverridesChanged?.Invoke(this, EventArgs.Empty);
		return new ManualSecretMarkRemovalResult(persistentRemoved, sessionRemoved);
	}

	public bool ToggleKeepAsIs(string occurrenceId)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		ArgumentException.ThrowIfNullOrWhiteSpace(occurrenceId);
		bool kept;
		lock (_sync)
		{
			ObjectDisposedException.ThrowIf(_disposed, this);
			kept = _keptOccurrenceIds.Add(occurrenceId);
			if (!kept)
				_keptOccurrenceIds.Remove(occurrenceId);
			_overrideRevision++;
			InvalidateSnapshotsLocked();
		}

		OverridesChanged?.Invoke(this, EventArgs.Empty);
		return kept;
	}

	public int? GetRedactionCount(
		string projectRoot,
		IReadOnlyList<string> orderedFilePaths,
		string transformIdentity = "")
		=> GetSnapshot(projectRoot, orderedFilePaths, transformIdentity)?.RedactedCount;

	public SecretRedactionSnapshot? GetSnapshot(
		string projectRoot,
		IReadOnlyList<string> orderedFilePaths,
		string transformIdentity = "")
	{
		var key = GetOrComputeSelectionKey(projectRoot, orderedFilePaths, transformIdentity);
		lock (_sync)
		{
			if (!_snapshots.TryGetValue(key, out var snapshot))
				return null;
			TouchSnapshotLocked(key);
			return snapshot;
		}
	}

	/// <summary>
	/// The UI polls the snapshot for the same selection on every relabel and refresh. The ordered
	/// file list it passes is reference-stable per selection revision, so the hashed key is reused
	/// until any input actually changes instead of re-sorting and re-hashing every path per call.
	/// </summary>
	private string GetOrComputeSelectionKey(
		string projectRoot,
		IReadOnlyList<string> orderedFilePaths,
		string transformIdentity)
	{
		lock (_sync)
		{
			var cached = _selectionKeyCache;
			if (cached is not null &&
			    ReferenceEquals(cached.OrderedFilePaths, orderedFilePaths) &&
			    string.Equals(cached.ProjectRoot, projectRoot, StringComparison.Ordinal) &&
			    string.Equals(cached.TransformIdentity, transformIdentity, StringComparison.Ordinal))
			{
				return cached.SelectionKey;
			}

			var key = BuildSelectionKey(projectRoot, orderedFilePaths, transformIdentity);
			_selectionKeyCache = new SelectionKeyCacheEntry(
				projectRoot,
				orderedFilePaths,
				transformIdentity,
				key);
			return key;
		}
	}

	private sealed record SelectionKeyCacheEntry(
		string ProjectRoot,
		IReadOnlyList<string> OrderedFilePaths,
		string TransformIdentity,
		string SelectionKey);

	public SecretScanCacheDiagnostics GetCacheDiagnostics()
	{
		var cache = _scanCache.Capture();
		return new SecretScanCacheDiagnostics(
			cache.EntryCount,
			cache.RetainedBytes,
			_scanCache.MaximumEntries,
			_scanCache.MaximumRetainedBytes,
			cache.Hits,
			cache.Misses,
			cache.DetectionRuns,
			Volatile.Read(ref _activeFullContentBuffers),
			Volatile.Read(ref _peakFullContentBuffers));
	}

	public void InvalidateSnapshots()
	{
		lock (_sync)
			InvalidateSnapshotsLocked();
	}

	/// <summary>
	/// Starts a new content identity generation after a project load, project reload, or Git update.
	/// Selection-only tree rebuilds remain in the current generation and can reuse validated content.
	/// </summary>
	public void AdvanceContentGeneration(string projectRoot)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
		var normalizedProjectRoot = Path.GetFullPath(projectRoot);
		CancellationTokenSource obsoleteGeneration;
		lock (_sync)
		{
			ObjectDisposedException.ThrowIf(_disposed, this);
			if (_activeProjectRoot is not null &&
			    !PathComparer.Default.Equals(_activeProjectRoot, normalizedProjectRoot))
			{
				ClearProjectSpecificStateForSwitchLocked(normalizedProjectRoot);
			}
			_activeProjectRoot = normalizedProjectRoot;
			obsoleteGeneration = AdvanceGenerationLocked();
		}
		CancelAndDispose(obsoleteGeneration);
	}

	/// <summary>
	/// Releases all content-derived state when Hide Secrets is switched off. Keep-as-is decisions
	/// remain session-only preferences and can be applied again if the user re-enables the option.
	/// </summary>
	public void Disable()
	{
		CancellationTokenSource obsoleteGeneration;
		lock (_sync)
		{
			ObjectDisposedException.ThrowIf(_disposed, this);
			obsoleteGeneration = AdvanceGenerationLocked();
		}
		CancelAndDispose(obsoleteGeneration);
	}

	/// <summary>
	/// Releases all project-specific state when the active workspace changes or the window closes.
	/// </summary>
	public void Reset()
	{
		CancellationTokenSource obsoleteGeneration;
		lock (_sync)
		{
			ObjectDisposedException.ThrowIf(_disposed, this);
			obsoleteGeneration = AdvanceGenerationLocked();
			_activeProjectRoot = null;
			_keptOccurrenceIds.Clear();
			_durablePersistentMarks.Clear();
			_durablePersistentMarkAppliedRevisions.Clear();
			_persistentMarks.Clear();
			_pendingPersistentMarkDeltas.Clear();
			_pendingPersistentMarkOrder.Clear();
			_pendingMarkMigrations.Clear();
			_persistentMarksProjectPath = null;
			_persistentMarksStoreRevision = -1;
			_sessionMarks.Clear();
			_promotedSessionMarkIds.Clear();
			_markedSecretsRevision++;
			_overrideRevision++;
		}
		CancelAndDispose(obsoleteGeneration);
	}

	public void Dispose()
	{
		CancellationTokenSource generation;
		lock (_sync)
		{
			if (_disposed)
				return;
			generation = AdvanceGenerationLocked();
			_disposed = true;
			_activeProjectRoot = null;
			_keptOccurrenceIds.Clear();
			_durablePersistentMarks.Clear();
			_durablePersistentMarkAppliedRevisions.Clear();
			_persistentMarks.Clear();
			_pendingPersistentMarkDeltas.Clear();
			_pendingPersistentMarkOrder.Clear();
			_pendingMarkMigrations.Clear();
			_sessionMarks.Clear();
			_promotedSessionMarkIds.Clear();
		}
		CancelAndDispose(generation);
		CancelAndDispose(_generationCancellation);
		if (_persistentIdentityProvider is IDisposable disposableIdentityProvider)
			disposableIdentityProvider.Dispose();
	}

	internal bool TryGetCachedFindings(
		string projectRoot,
		string filePath,
		SecretFileMetadata metadata,
		ISecretDetectionScope detectorScope,
		bool includeAutomaticDetection,
		int markedSecretsRevision,
		string transformIdentity,
		long generation,
		CancellationToken generationToken,
		out SecretScanCacheEntry entry)
	{
		lock (_sync)
		{
			ThrowIfGenerationIsNotCurrentLocked(generation, generationToken);
			return _scanCache.TryGetByMetadata(
				filePath,
				metadata,
				GetRulesIdentity(
					detectorScope,
					filePath,
					NormalizeRelativePath(projectRoot, filePath),
					includeAutomaticDetection),
				transformIdentity,
				markedSecretsRevision,
				out entry);
		}
	}

	internal SecretScanCacheEntry GetOrDetectFindings(
		string projectRoot,
		string filePath,
		string content,
		SecretFileMetadata metadata,
		ISecretDetectionScope detectorScope,
		MarkedSecretsMatcher markedSecretsMatcher,
		bool includeAutomaticDetection,
		int markedSecretsRevision,
		string transformIdentity,
		long generation,
		CancellationToken generationToken,
		CancellationToken cancellationToken,
		ContentTransformMap? transformMap = null) =>
		GetOrDetectFindings(
			projectRoot,
			filePath,
			content.AsSpan(),
			metadata,
			detectorScope,
			markedSecretsMatcher,
			includeAutomaticDetection,
			markedSecretsRevision,
			transformIdentity,
			generation,
			generationToken,
			cancellationToken,
			transformMap);

	internal SecretScanCacheEntry GetOrDetectFindings(
		string projectRoot,
		string filePath,
		ReadOnlySpan<char> content,
		SecretFileMetadata metadata,
		ISecretDetectionScope detectorScope,
		MarkedSecretsMatcher markedSecretsMatcher,
		bool includeAutomaticDetection,
		int markedSecretsRevision,
		string transformIdentity,
		long generation,
		CancellationToken generationToken,
		CancellationToken cancellationToken,
		ContentTransformMap? transformMap = null,
		ContentFingerprint? knownFingerprint = null,
		bool allowIdentityTransformFallback = false)
	{
		ThrowIfGenerationIsNotCurrent(generation, generationToken);
		var relativePath = NormalizeRelativePath(projectRoot, filePath);
		var rulesIdentity = GetRulesIdentity(
			detectorScope,
			filePath,
			relativePath,
			includeAutomaticDetection);
		string contentFingerprint;
		if (knownFingerprint is { } fingerprint)
		{
			contentFingerprint = fingerprint.ToHexString();
		}
		else
		{
			ContentPipelineDiagnostics.RecordContentFingerprint();
			contentFingerprint = HashText(content);
		}
		SecretScanCacheEntry cached;
		lock (_sync)
		{
			ThrowIfGenerationIsNotCurrentLocked(generation, generationToken);
			if (_scanCache.TryGetByContent(
				    filePath,
				    metadata,
				    contentFingerprint,
				    rulesIdentity,
				    transformIdentity,
				    markedSecretsRevision,
				    out cached))
			{
				return cached;
			}
		}
		if (allowIdentityTransformFallback && transformIdentity.Length > 0)
		{
			lock (_sync)
			{
				ThrowIfGenerationIsNotCurrentLocked(generation, generationToken);
				if (_scanCache.TryGetByContent(
					    filePath,
					    metadata,
					    contentFingerprint,
					    rulesIdentity,
					    transformIdentity: string.Empty,
					    markedSecretsRevision,
					    out cached))
				{
					StoreEquivalentTransformAliasLocked(cached, transformIdentity);
					return cached;
				}
			}
		}

		var inspectionBudget = new SecretFileInspectionBudget(
			SecretInspectionLimits.MaximumDetectorTimePerFile,
			generationToken);
		var detectorFindings = includeAutomaticDetection
			? detectorScope.Detect(filePath, relativePath, content, inspectionBudget, cancellationToken)
			: [];
		var markedFindings = markedSecretsMatcher.Match(
			relativePath,
			content,
			transformMap,
			inspectionBudget,
			cancellationToken);
		var detected = SecretRedactionScope.ResolveNonOverlappingMatches(
			detectorFindings.Count == 0
				? markedFindings
				: markedFindings.Count == 0
					? detectorFindings
					: [..markedFindings, ..detectorFindings]);
		var findings = new SecretFindingMetadata[detected.Count];
		for (var index = 0; index < detected.Count; index++)
		{
			cancellationToken.ThrowIfCancellationRequested();
			generationToken.ThrowIfCancellationRequested();
			var finding = detected[index];
			if (finding.Start < 0 || finding.Length <= 0 || finding.Start > content.Length - finding.Length)
				throw new SecretDetectionException($"Secret detector returned an invalid span for '{relativePath}'.");
			findings[index] = new SecretFindingMetadata(
				finding.RuleId,
				finding.Start,
				finding.Length,
				HashValue(content.Slice(finding.Start, finding.Length)),
				finding.RuleOrder,
				finding.Source,
				finding.PersistentMarkHash,
				finding.SessionMarkId,
				finding.PersistentMarkId);
		}

		var normalizedPath = Path.GetFullPath(filePath);
		var entry = new SecretScanCacheEntry(
			normalizedPath,
			metadata,
			contentFingerprint,
			rulesIdentity,
			transformIdentity,
			markedSecretsRevision,
			IsBinary: false,
			findings,
			EstimateRetainedBytes(
				normalizedPath,
				contentFingerprint,
				rulesIdentity,
				transformIdentity,
				findings));
		lock (_sync)
		{
			ThrowIfGenerationIsNotCurrentLocked(generation, generationToken);
			_scanCache.Store(entry, detectionExecuted: true);
			if (allowIdentityTransformFallback && transformIdentity.Length > 0)
				StoreEquivalentTransformAliasLocked(entry, transformIdentity: string.Empty);
		}
		return entry;
	}

	private void StoreEquivalentTransformAliasLocked(
		SecretScanCacheEntry source,
		string transformIdentity)
	{
		var alias = source with
		{
			TransformIdentity = transformIdentity,
			ApproximateRetainedBytes = EstimateRetainedBytes(
				source.NormalizedPath,
				source.ContentFingerprint,
				source.RulesIdentity,
				transformIdentity,
				source.Findings)
		};
		_scanCache.Store(alias, detectionExecuted: false);
	}

	internal SecretScanCacheEntry StoreBinary(
		string projectRoot,
		string filePath,
		SecretFileMetadata metadata,
		ISecretDetectionScope detectorScope,
		bool includeAutomaticDetection,
		int markedSecretsRevision,
		string transformIdentity,
		long generation,
		CancellationToken generationToken)
	{
		var normalizedPath = Path.GetFullPath(filePath);
		var relativePath = NormalizeRelativePath(projectRoot, filePath);
		var rulesIdentity = GetRulesIdentity(
			detectorScope,
			filePath,
			relativePath,
			includeAutomaticDetection);
		var entry = new SecretScanCacheEntry(
			normalizedPath,
			metadata,
			ContentFingerprint: string.Empty,
			rulesIdentity,
			transformIdentity,
			markedSecretsRevision,
			IsBinary: true,
			Findings: [],
			ApproximateRetainedBytes: 96 +
			                          (normalizedPath.Length + rulesIdentity.Length + transformIdentity.Length) *
			                          sizeof(char));
		lock (_sync)
		{
			ThrowIfGenerationIsNotCurrentLocked(generation, generationToken);
			_scanCache.Store(entry, detectionExecuted: false);
		}
		return entry;
	}

	/// <summary>
	/// Records a text file the scanner never read because it is past the scan limit.
	///
	/// It is stored, not skipped, so a relabel does not re-stat it on every pass. The empty content
	/// fingerprint is what tells a reader apart from a file that was scanned and found clean: no
	/// text was hashed here, so the empty finding list means "unknown", not "nothing".
	/// </summary>
	internal SecretScanCacheEntry StoreUnscannable(
		string projectRoot,
		string filePath,
		SecretFileMetadata metadata,
		ISecretDetectionScope detectorScope,
		bool includeAutomaticDetection,
		int markedSecretsRevision,
		string transformIdentity,
		long generation,
		CancellationToken generationToken)
	{
		var normalizedPath = Path.GetFullPath(filePath);
		var relativePath = NormalizeRelativePath(projectRoot, filePath);
		var rulesIdentity = GetRulesIdentity(
			detectorScope,
			filePath,
			relativePath,
			includeAutomaticDetection);
		var entry = new SecretScanCacheEntry(
			normalizedPath,
			metadata,
			ContentFingerprint: string.Empty,
			rulesIdentity,
			transformIdentity,
			markedSecretsRevision,
			IsBinary: false,
			Findings: [],
			ApproximateRetainedBytes: 96 +
			                          (normalizedPath.Length + rulesIdentity.Length + transformIdentity.Length) *
			                          sizeof(char));
		lock (_sync)
		{
			ThrowIfGenerationIsNotCurrentLocked(generation, generationToken);
			_scanCache.Store(entry, detectionExecuted: false);
		}
		return entry;
	}

	internal IDisposable TrackFullContentBuffer()
	{
		var active = Interlocked.Increment(ref _activeFullContentBuffers);
		while (true)
		{
			var peak = Volatile.Read(ref _peakFullContentBuffers);
			if (active <= peak || Interlocked.CompareExchange(ref _peakFullContentBuffers, active, peak) == peak)
				break;
		}
		return new FullContentBufferLease(this);
	}

	internal ISecretDetectionScope CreateDetectorScope(string projectRoot) =>
		_detector.CreateScope(projectRoot);

	private static string GetRulesIdentity(
		ISecretDetectionScope detectorScope,
		string filePath,
		string relativePath,
		bool includeAutomaticDetection)
	{
		var identity = detectorScope.GetRulesIdentity(filePath, relativePath);
		return includeAutomaticDetection ? identity : $"{identity}:manual-only";
	}

	internal void Publish(
		SecretRedactionSnapshot snapshot,
		long overrideRevision,
		long snapshotRevision,
		long generation,
		CancellationToken generationToken)
	{
		lock (_sync)
		{
			ThrowIfGenerationIsNotCurrentLocked(generation, generationToken);
			if (overrideRevision != _overrideRevision || snapshotRevision != _snapshotRevision)
				return;
			_snapshots[snapshot.SelectionKey] = snapshot;
			TouchSnapshotLocked(snapshot.SelectionKey);
			while (_snapshots.Count > MaximumSnapshots)
				RemoveOldestSnapshotLocked();
		}
		SnapshotPublished?.Invoke(this, new SecretRedactionSnapshotPublishedEventArgs(snapshot));
	}

	internal static string BuildSelectionKey(
		string projectRoot,
		IReadOnlyList<string> orderedFilePaths,
		string transformIdentity = "") =>
		ContentSelectionSnapshot
			.Create(projectRoot, orderedFilePaths)
			.CreateTransformFingerprint(transformIdentity);

	internal static string NormalizeRelativePath(string projectRoot, string filePath)
	{
		var relative = Path.GetRelativePath(projectRoot, filePath).Replace('\\', '/');
		return relative == "." ? Path.GetFileName(filePath) : relative;
	}

	internal static string HashValue(ReadOnlySpan<char> value) => HashText(value);

	private static string HashText(string value) => HashText(value.AsSpan());

	private static string HashText(ReadOnlySpan<char> value)
	{
		Span<byte> hash = stackalloc byte[32];
		SHA256.HashData(MemoryMarshal.AsBytes(value), hash);
		return Convert.ToHexString(hash);
	}

	private static long EstimateRetainedBytes(
		string normalizedPath,
		string contentFingerprint,
		string rulesIdentity,
		string transformIdentity,
		IReadOnlyList<SecretFindingMetadata> findings)
	{
		long bytes = 160 +
		             (normalizedPath.Length + contentFingerprint.Length + rulesIdentity.Length +
		              transformIdentity.Length) * sizeof(char);
		foreach (var finding in findings)
		{
			bytes += 64 + (finding.RuleId.Length + finding.ValueFingerprint.Length) * sizeof(char);
			if (finding.PersistentMarkId is { RelativePath: { } relativePath })
				bytes += relativePath.Length * sizeof(char);
		}
		return bytes;
	}

	private void AdvanceMarkedSecretsRevisionLocked()
	{
		_markedSecretsRevision++;
		_overrideRevision++;
		InvalidateSnapshotsLocked();
	}

	private void InvalidateSnapshotsLocked()
	{
		_snapshotRevision++;
		_snapshots.Clear();
		_snapshotLru.Clear();
		_snapshotLruNodes.Clear();
		_selectionKeyCache = null;
	}

	private CancellationTokenSource AdvanceGenerationLocked()
	{
		var obsolete = _generationCancellation;
		_generationCancellation = new CancellationTokenSource();
		_generation++;
		_scanCache.Clear();
		InvalidateSnapshotsLocked();
		return obsolete;
	}

	internal void ThrowIfGenerationIsNotCurrent(
		long generation,
		CancellationToken generationToken)
	{
		generationToken.ThrowIfCancellationRequested();
		lock (_sync)
			ThrowIfGenerationIsNotCurrentLocked(generation, generationToken);
	}

	private void ThrowIfGenerationIsNotCurrentLocked(
		long generation,
		CancellationToken generationToken)
	{
		generationToken.ThrowIfCancellationRequested();
		if (_disposed)
			throw new ObjectDisposedException(nameof(SecretRedactionSession));
		if (generation != _generation)
			throw new OperationCanceledException("The secret-redaction scope belongs to an obsolete generation.");
	}

	private static void CancelAndDispose(CancellationTokenSource? source)
	{
		if (source is null)
			return;
		try
		{
			source.Cancel();
		}
		finally
		{
			source.Dispose();
		}
	}

	private static void CancelWithoutDispose(CancellationTokenSource? source)
	{
		if (source is null)
			return;
		try
		{
			source.Cancel();
		}
		catch (ObjectDisposedException)
		{
			// The superseded background task completed between the swap and cancellation.
		}
	}

	private void TouchSnapshotLocked(string key)
	{
		if (_snapshotLruNodes.TryGetValue(key, out var existing))
		{
			_snapshotLru.Remove(existing);
			_snapshotLru.AddFirst(existing);
			return;
		}

		_snapshotLruNodes.Add(key, _snapshotLru.AddFirst(key));
	}

	private void RemoveOldestSnapshotLocked()
	{
		var oldest = _snapshotLru.Last;
		if (oldest is null)
			return;
		_snapshotLru.RemoveLast();
		_snapshotLruNodes.Remove(oldest.Value);
		_snapshots.Remove(oldest.Value);
	}

	internal int SnapshotCount
	{
		get
		{
			lock (_sync)
				return _snapshots.Count;
		}
	}

	private sealed class FullContentBufferLease(SecretRedactionSession owner) : IDisposable
	{
		private SecretRedactionSession? _owner = owner;

		public void Dispose()
		{
			var current = Interlocked.Exchange(ref _owner, null);
			if (current is not null)
				Interlocked.Decrement(ref current._activeFullContentBuffers);
		}
	}
}

public sealed class SecretRedactionSnapshotPublishedEventArgs(SecretRedactionSnapshot snapshot) : EventArgs
{
	public SecretRedactionSnapshot Snapshot { get; } = snapshot;
}

public sealed class SecretRedactionScope
{
	private readonly SecretRedactionSession _session;
	private readonly string _projectRoot;
	private readonly IReadOnlySet<string> _keptOccurrenceIds;
	private readonly long _overrideRevision;
	private readonly long _snapshotRevision;
	private readonly long _generation;
	private readonly CancellationToken _generationToken;
	private readonly MarkedSecretsMatcher _markedSecretsMatcher;
	private readonly int _markedSecretsRevision;
	private readonly string _transformIdentity;
	private readonly ISecretDetectionScope _detectorScope;
	private readonly Dictionary<string, int> _identityIndexes = new(StringComparer.Ordinal);
	private readonly Dictionary<string, int> _ruleIdentityCounts = new(StringComparer.Ordinal);
	private readonly Dictionary<string, int> _markedSecretCounts = new(StringComparer.OrdinalIgnoreCase);
	private readonly ConcurrentDictionary<string, SecretContentInspectionMode> _inspectionModes =
		new(PathComparer.Default);
	private readonly SecretOutputInspectionBudget _outputInspectionBudget = new();
	private int _detectedCount;
	private int _redactedCount;
	private int _publicConsumerActive;
	// The count scan runs files in parallel, so "first" would depend on thread timing. Keeping the
	// ordinally smallest path makes the reported file the same on every run.
	private string? _unscannablePath;
	private bool _completed;

	internal SecretRedactionScope(
		SecretRedactionSession session,
		string projectRoot,
		string selectionKey,
		IReadOnlySet<string> keptOccurrenceIds,
		long overrideRevision,
		long snapshotRevision,
		MarkedSecretsMatcher markedSecretsMatcher,
		int markedSecretsRevision,
		long generation,
		CancellationToken generationToken,
		string transformIdentity = "")
	{
		_session = session;
		_transformIdentity = transformIdentity;
		_projectRoot = Path.GetFullPath(projectRoot);
		_keptOccurrenceIds = keptOccurrenceIds;
		_overrideRevision = overrideRevision;
		_snapshotRevision = snapshotRevision;
		_markedSecretsMatcher = markedSecretsMatcher;
		_markedSecretsRevision = markedSecretsRevision;
		_generation = generation;
		_generationToken = generationToken;
		_detectorScope = session.CreateDetectorScope(_projectRoot);
		SelectionKey = selectionKey;
	}

	public string SelectionKey { get; }
	public int DetectedCount => _detectedCount;
	public int RedactedCount => _redactedCount;

	internal SecretContentInspectionMode GetContentInspectionMode(string filePath)
	{
		EnsureActive();
		return _inspectionModes.GetOrAdd(filePath, ResolveContentInspectionMode);
	}

	private SecretContentInspectionMode ResolveContentInspectionMode(string filePath)
	{
		var relativePath = SecretRedactionSession.NormalizeRelativePath(_projectRoot, filePath);
		if (_detectorScope.ShouldInspectPath(filePath, relativePath))
			return SecretContentInspectionMode.AutomaticAndManual;
		return _markedSecretsMatcher.RequiresContentInspection(relativePath)
			? SecretContentInspectionMode.ManualOnly
			: SecretContentInspectionMode.None;
	}

	internal bool TryAnalyzeCached(string filePath)
	{
		EnsureActive();
		if (!TryGetCachedEntry(filePath, SecretFileMetadata.Capture(filePath), out var entry))
			return false;
		ProcessEntry(filePath, entry);
		return true;
	}

	internal bool TryGetCachedEntry(
		string filePath,
		SecretFileMetadata metadata,
		out SecretScanCacheEntry entry)
	{
		EnsureActive();
		var inspectionMode = GetContentInspectionMode(filePath);
		if (inspectionMode == SecretContentInspectionMode.None)
		{
			entry = null!;
			return false;
		}
		return _session.TryGetCachedFindings(
			_projectRoot,
			filePath,
			metadata,
			_detectorScope,
			inspectionMode == SecretContentInspectionMode.AutomaticAndManual,
			_markedSecretsRevision,
			_transformIdentity,
			_generation,
			_generationToken,
			out entry);
	}

	internal void Analyze(
		string filePath,
		string content,
		SecretFileMetadata metadata,
		CancellationToken cancellationToken = default) =>
		Analyze(filePath, content.AsSpan(), metadata, cancellationToken);

	internal void Analyze(
		string filePath,
		ReadOnlySpan<char> content,
		SecretFileMetadata metadata,
		CancellationToken cancellationToken = default)
	{
		var entry = Detect(
			filePath,
			content,
			metadata,
			cancellationToken);
		ProcessEntry(filePath, entry);
	}

	internal SecretScanCacheEntry Detect(
		string filePath,
		ReadOnlySpan<char> content,
		SecretFileMetadata metadata,
		CancellationToken cancellationToken = default)
	{
		EnsureActive();
		var inspectionMode = GetContentInspectionMode(filePath);
		if (inspectionMode == SecretContentInspectionMode.None)
		{
			throw new InvalidOperationException(
				$"Secret detection was requested for a path excluded by detector policy: '{filePath}'.");
		}
		return _session.GetOrDetectFindings(
			_projectRoot,
			filePath,
			content,
			metadata,
			_detectorScope,
			_markedSecretsMatcher,
			inspectionMode == SecretContentInspectionMode.AutomaticAndManual,
			_markedSecretsRevision,
			_transformIdentity,
			_generation,
			_generationToken,
			cancellationToken);
	}

	internal void AnalyzeBinary(string filePath, SecretFileMetadata metadata)
	{
		EnsureActive();
		var inspectionMode = GetContentInspectionMode(filePath);
		if (inspectionMode == SecretContentInspectionMode.None)
			return;
		_session.StoreBinary(
			_projectRoot,
			filePath,
			metadata,
			_detectorScope,
			inspectionMode == SecretContentInspectionMode.AutomaticAndManual,
			_markedSecretsRevision,
			_transformIdentity,
			_generation,
			_generationToken);
	}

	internal SecretScanCacheEntry StoreBinary(string filePath, SecretFileMetadata metadata)
	{
		EnsureActive();
		var inspectionMode = GetContentInspectionMode(filePath);
		if (inspectionMode == SecretContentInspectionMode.None)
			throw new InvalidOperationException("An excluded path cannot be stored as binary scan input.");
		return _session.StoreBinary(
			_projectRoot,
			filePath,
			metadata,
			_detectorScope,
			inspectionMode == SecretContentInspectionMode.AutomaticAndManual,
			_markedSecretsRevision,
			_transformIdentity,
			_generation,
			_generationToken);
	}

	internal SecretScanCacheEntry StoreUnscannable(string filePath, SecretFileMetadata metadata)
	{
		EnsureActive();
		var inspectionMode = GetContentInspectionMode(filePath);
		if (inspectionMode == SecretContentInspectionMode.None)
			throw new InvalidOperationException("An excluded path cannot be stored as unscannable input.");
		RecordUnscannable(filePath);
		return _session.StoreUnscannable(
			_projectRoot,
			filePath,
			metadata,
			_detectorScope,
			inspectionMode == SecretContentInspectionMode.AutomaticAndManual,
			_markedSecretsRevision,
			_transformIdentity,
			_generation,
			_generationToken);
	}

	internal void AnalyzeUnscannable(string filePath, SecretFileMetadata metadata)
	{
		EnsureActive();
		var inspectionMode = GetContentInspectionMode(filePath);
		if (inspectionMode == SecretContentInspectionMode.None)
			return;
		RecordUnscannable(filePath);
		_session.StoreUnscannable(
			_projectRoot,
			filePath,
			metadata,
			_detectorScope,
			inspectionMode == SecretContentInspectionMode.AutomaticAndManual,
			_markedSecretsRevision,
			_transformIdentity,
			_generation,
			_generationToken);
	}

	private void RecordUnscannable(string filePath)
	{
		while (true)
		{
			var current = Volatile.Read(ref _unscannablePath);
			if (current is not null && string.CompareOrdinal(current, filePath) <= 0)
				return;
			if (Interlocked.CompareExchange(ref _unscannablePath, filePath, current) == current)
				return;
		}
	}

	internal void ProcessEntry(string filePath, SecretScanCacheEntry entry)
	{
		EnterOrderedConsumer();
		try
		{
			EnsureActive();
			// Also runs for entries served from the cache, which is the only way a second scan of the
			// same unchanged file would otherwise forget that it was never read.
			if (entry.IsUnscannable)
				RecordUnscannable(filePath);
			ProcessFindings(filePath, entry.Findings, transformMap: null);
		}
		finally
		{
			ExitOrderedConsumer();
		}
	}

	public SecretTextRedactionResult Redact(
		string filePath,
		string content,
		CancellationToken cancellationToken = default) =>
		Redact(filePath, content, null, cancellationToken);

	/// <param name="content">The text to redact, after compression.</param>
	/// <param name="transformMap">Translation from canonical source offsets into this text.</param>
	public SecretTextRedactionResult Redact(
		string filePath,
		string content,
		ContentTransformMap? transformMap,
		CancellationToken cancellationToken = default)
	{
		EnsureActive();
		EnterOrderedConsumer();
		try
		{
			var plan = CreatePlan(filePath, content, transformMap, cancellationToken);
			return plan.BuildResult(content);
		}
		finally
		{
			ExitOrderedConsumer();
		}
	}

	internal SecretFileRedactionPlan CreatePlan(
		string filePath,
		string content,
		CancellationToken cancellationToken = default) =>
		CreatePlan(filePath, content, null, cancellationToken);

	internal SecretFileRedactionPlan CreatePlan(
		string filePath,
		string content,
		ContentTransformMap? transformMap,
		CancellationToken cancellationToken = default) =>
		CreatePlan(
			filePath,
			content,
			transformMap,
			knownFingerprint: null,
			cancellationToken: cancellationToken);

	internal SecretFileRedactionPlan CreatePlan(
		string filePath,
		string content,
		ContentTransformMap? transformMap,
		ContentFingerprint? knownFingerprint,
		CancellationToken cancellationToken = default)
	{
		return CreatePlan(
			filePath,
			content,
			transformMap,
			SecretFileMetadata.Capture(filePath),
			knownFingerprint,
			cancellationToken);
	}

	internal SecretFileRedactionPlan CreatePlan(
		string filePath,
		string content,
		ContentTransformMap? transformMap,
		SecretFileMetadata metadata,
		ContentFingerprint? knownFingerprint,
		CancellationToken cancellationToken = default)
	{
		EnsureActive();
		var inspectionMode = GetContentInspectionMode(filePath);
		if (inspectionMode == SecretContentInspectionMode.None)
			return ProcessFindings(filePath, [], transformMap);
		// Measured on the text this scope was handed, not on the file on disk. Compression runs
		// first and the plan describes its output, so gating on the on-disk size would refuse work
		// the scanner is about to do on a fraction of that text - the limit would fight the very
		// setting a user enables to get under it.
		if (content.Length > SecretRedactionOutputPreparer.MaximumScannableFileBytes)
		{
			throw new SecretScanLimitExceededException(
				filePath,
				content.Length,
				SecretRedactionOutputPreparer.MaximumScannableFileBytes);
		}
		var entry = _session.GetOrDetectFindings(
			_projectRoot,
			filePath,
			content,
			metadata,
			_detectorScope,
			_markedSecretsMatcher,
			inspectionMode == SecretContentInspectionMode.AutomaticAndManual,
			_markedSecretsRevision,
			_transformIdentity,
			_generation,
			_generationToken,
			cancellationToken,
			transformMap,
			knownFingerprint,
			allowIdentityTransformFallback:
				knownFingerprint is not null && transformMap?.IsIdentity == true);
		return ProcessFindings(filePath, entry.Findings, transformMap);
	}

	internal IDisposable TrackFullContentBuffer() => _session.TrackFullContentBuffer();

	public SecretRedactionSnapshot Complete()
		=> Complete(skippedFileCount: 0, failedFileCount: 0);

	internal SecretRedactionSnapshot Complete(int skippedFileCount, int failedFileCount)
	{
		EnterOrderedConsumer();
		try
		{
			EnsureActive();
			ArgumentOutOfRangeException.ThrowIfNegative(skippedFileCount);
			ArgumentOutOfRangeException.ThrowIfNegative(failedFileCount);
			_completed = true;
			var snapshot = new SecretRedactionSnapshot(
				SelectionKey,
				_detectedCount,
				_redactedCount,
				new Dictionary<string, int>(_markedSecretCounts, StringComparer.OrdinalIgnoreCase),
				Volatile.Read(ref _unscannablePath),
				skippedFileCount,
				failedFileCount);
			_session.Publish(
				snapshot,
				_overrideRevision,
				_snapshotRevision,
				_generation,
				_generationToken);
			return snapshot;
		}
		finally
		{
			ExitOrderedConsumer();
		}
	}

	private SecretFileRedactionPlan ProcessFindings(
		string filePath,
		IReadOnlyList<SecretFindingMetadata> findings,
		ContentTransformMap? transformMap)
	{
		_outputInspectionBudget.RegisterFindings(findings.Count);
		var relativePath = SecretRedactionSession.NormalizeRelativePath(_projectRoot, filePath);
		var replacements = new SecretReplacement[findings.Count];
		var spans = new SecretPreviewSpan[findings.Count];
		var outputDelta = 0;
		var redactedInFile = 0;
		for (var index = 0; index < findings.Count; index++)
		{
			var finding = findings[index];
			var identity = $"{finding.RuleId}:{finding.ValueFingerprint}";
			if (!_identityIndexes.TryGetValue(identity, out var identityIndex))
			{
				identityIndex = _ruleIdentityCounts.GetValueOrDefault(finding.RuleId) + 1;
				_ruleIdentityCounts[finding.RuleId] = identityIndex;
				_identityIndexes.Add(identity, identityIndex);
			}

			var coordinateIdentity = ResolveOccurrenceCoordinateIdentity(finding, transformMap);
			var occurrenceId = SecretRedactionSession.HashValue(
				$"{_projectRoot}\n{relativePath}\n{finding.RuleId}\n{finding.ValueFingerprint}\n{coordinateIdentity}".AsSpan());
			var kept = _keptOccurrenceIds.Contains(occurrenceId);
			var replacement = kept
				? null
				: SecretRedactionLegend.CreatePlaceholder(finding.RuleId, identityIndex);
			var outputStart = checked(finding.Start + outputDelta);
			var outputLength = replacement?.Length ?? finding.Length;
			replacements[index] = new SecretReplacement(
				finding.Start,
				finding.Length,
				replacement);
			spans[index] = new SecretPreviewSpan(
				occurrenceId,
				finding.RuleId,
				outputStart,
				outputLength,
				kept ? SecretPreviewSpanState.KeptAsIs : SecretPreviewSpanState.Redacted,
				finding.Length,
				finding.Source,
				finding.PersistentMarkHash,
				finding.SessionMarkId,
				finding.PersistentMarkId);
			outputDelta = checked(outputDelta + outputLength - finding.Length);
			_detectedCount++;
			if (!kept)
			{
				_redactedCount++;
				redactedInFile++;
				if (finding.PersistentMarkHash is { Length: > 0 } markHash)
					_markedSecretCounts[markHash] = _markedSecretCounts.GetValueOrDefault(markHash) + 1;
			}
		}

		return new SecretFileRedactionPlan(replacements, spans, findings.Count, redactedInFile);
	}

	private string ResolveOccurrenceCoordinateIdentity(
		SecretFindingMetadata finding,
		ContentTransformMap? transformMap)
	{
		if (transformMap is null or { IsIdentity: true })
			return $"source:{finding.Start}:{finding.Length}";
		if (transformMap.TryMapSourceBackedRange(
			    finding.Start,
			    finding.Length,
			    out var sourceStart,
			    out var sourceLength))
		{
			return $"source:{sourceStart}:{sourceLength}";
		}

		// Replacement-only text has no source coordinate. Its namespace includes the exact
		// transform identity so it can never inherit a keep decision from source content.
		return $"transform:{_transformIdentity}:{finding.Start}:{finding.Length}";
	}

	private void EnsureActive()
	{
		if (_completed)
			throw new InvalidOperationException("The redaction output scope is already complete.");
		_session.ThrowIfGenerationIsNotCurrent(_generation, _generationToken);
	}

	private void EnterOrderedConsumer()
	{
		if (Interlocked.CompareExchange(ref _publicConsumerActive, 1, 0) != 0)
		{
			throw new InvalidOperationException(
				"A redaction scope accepts one ordered consumer at a time.");
		}
	}

	private void ExitOrderedConsumer() => Volatile.Write(ref _publicConsumerActive, 0);

	internal static IReadOnlyList<DetectedSecret> ResolveNonOverlappingMatches(
		IReadOnlyList<DetectedSecret> matches)
	{
		if (matches.Count <= 1)
			return matches;

		var mergedExactMatches = matches
			.GroupBy(static match => (match.Start, match.Length))
			.Select(static group => MergeExactMatches(group))
			.ToArray();
		var candidates = mergedExactMatches
			.OrderByDescending(static match =>
				(match.Source & (SecretFindingSource.PersistentMark | SecretFindingSource.SessionMark)) != 0)
			.ThenBy(static match => IsGenericRule(match.RuleId))
			.ThenBy(static match => match.RuleOrder)
			.ThenBy(static match => match.Start)
			.ThenByDescending(static match => match.Length)
			.ToArray();
		var accepted = new SortedSet<AcceptedInterval>(AcceptedIntervalStartComparer.Instance);
		var minimum = new AcceptedInterval(int.MinValue, int.MinValue, null);
		var maximum = new AcceptedInterval(int.MaxValue, int.MaxValue, null);
		foreach (var candidate in candidates)
		{
			var candidateEnd = candidate.Start + candidate.Length;
			var predecessorView = accepted.GetViewBetween(
				minimum,
				new AcceptedInterval(candidate.Start, candidate.Start, null));
			var predecessor = predecessorView.Max;
			if (predecessor is not null && predecessor.End > candidate.Start)
				continue;

			var successorView = accepted.GetViewBetween(
				new AcceptedInterval(candidate.Start, candidate.Start, null),
				maximum);
			var successor = successorView.Min;
			if (successor is not null && successor.Start < candidateEnd)
				continue;

			accepted.Add(new AcceptedInterval(candidate.Start, candidateEnd, candidate));
		}

		return accepted.Select(static interval => interval.Match!).ToArray();
	}

	private static DetectedSecret MergeExactMatches(IEnumerable<DetectedSecret> group)
	{
		var matches = group.ToArray();
		var winner = matches
			.OrderByDescending(static match =>
				(match.Source & (SecretFindingSource.PersistentMark | SecretFindingSource.SessionMark)) != 0)
			.ThenBy(static match => IsGenericRule(match.RuleId))
			.ThenBy(static match => match.RuleOrder)
			.First();
		var source = matches.Aggregate(
			(SecretFindingSource)0,
			static (current, match) => current | match.Source);
		var persistentHash = matches
			.Select(static match => match.PersistentMarkHash)
			.FirstOrDefault(static hash => !string.IsNullOrWhiteSpace(hash));
		var sessionMarkId = matches
			.Select(static match => match.SessionMarkId)
			.FirstOrDefault(static id => !string.IsNullOrWhiteSpace(id));
		var persistentMarkId = matches
			.Select(static match => match.PersistentMarkId)
			.FirstOrDefault(static id => id is not null);
		return winner with
		{
			Source = source,
			PersistentMarkHash = persistentHash,
			SessionMarkId = sessionMarkId,
			PersistentMarkId = persistentMarkId
		};
	}

	private static bool IsGenericRule(string ruleId) =>
		ruleId.Equals("generic-api-key", StringComparison.Ordinal);

	private sealed record AcceptedInterval(int Start, int End, DetectedSecret? Match);

	private sealed class AcceptedIntervalStartComparer : IComparer<AcceptedInterval>
	{
		public static AcceptedIntervalStartComparer Instance { get; } = new();

		public int Compare(AcceptedInterval? left, AcceptedInterval? right)
		{
			if (ReferenceEquals(left, right))
				return 0;
			if (left is null)
				return -1;
			if (right is null)
				return 1;
			return left.Start.CompareTo(right.Start);
		}
	}
}

internal enum SecretContentInspectionMode : byte
{
	None = 0,
	ManualOnly = 1,
	AutomaticAndManual = 2
}

internal sealed record SecretReplacement(int SourceStart, int SourceLength, string? Replacement)
{
	public int SourceEnd => checked(SourceStart + SourceLength);
}

internal sealed class SecretFileRedactionPlan(
	IReadOnlyList<SecretReplacement> replacements,
	IReadOnlyList<SecretPreviewSpan> spans,
	int detectedCount,
	int redactedCount)
{
	public IReadOnlyList<SecretReplacement> Replacements { get; } = replacements;
	public IReadOnlyList<SecretPreviewSpan> Spans { get; } = spans;
	public int DetectedCount { get; } = detectedCount;
	public int RedactedCount { get; } = redactedCount;

	public SecretTextRedactionResult BuildResult(string content)
	{
		if (Replacements.Count == 0)
		{
			return new SecretTextRedactionResult(
				content,
				Spans,
				0,
				0,
				ContentTransformMap.Identity);
		}

		var estimatedLength = content.Length;
		var transformedRanges = new List<ContentTransformRange>(Replacements.Count);
		foreach (var replacement in Replacements)
		{
			estimatedLength = checked(estimatedLength + (replacement.Replacement?.Length ?? replacement.SourceLength) - replacement.SourceLength);
			if (replacement.Replacement is not null)
			{
				transformedRanges.Add(new ContentTransformRange(
					replacement.SourceStart,
					replacement.SourceLength,
					replacement.Replacement.Length));
			}
		}
		var builder = new StringBuilder(estimatedLength);
		AppendTo(builder, content, content.Length);
		return new SecretTextRedactionResult(
			builder.ToString(),
			Spans,
			DetectedCount,
			RedactedCount,
			ContentTransformMap.Create(transformedRanges, content.Length));
	}

	public void AppendTo(StringBuilder destination, string content, int sourceLength)
	{
		ArgumentOutOfRangeException.ThrowIfGreaterThan(sourceLength, content.Length);
		var sourceOffset = 0;
		foreach (var replacement in Replacements)
		{
			if (replacement.SourceStart >= sourceLength)
				break;
			if (replacement.SourceEnd > sourceLength)
				throw new InvalidOperationException("A redaction span crosses the requested output boundary.");
			destination.Append(content, sourceOffset, replacement.SourceStart - sourceOffset);
			if (replacement.Replacement is null)
				destination.Append(content, replacement.SourceStart, replacement.SourceLength);
			else
				destination.Append(replacement.Replacement);
			sourceOffset = replacement.SourceEnd;
		}
		destination.Append(content, sourceOffset, sourceLength - sourceOffset);
	}

	public async ValueTask WriteToAsync(
		TextWriter destination,
		string content,
		CancellationToken cancellationToken)
	{
		var sourceOffset = 0;
		foreach (var replacement in Replacements)
		{
			cancellationToken.ThrowIfCancellationRequested();
			await destination.WriteAsync(
					content.AsMemory(sourceOffset, replacement.SourceStart - sourceOffset),
					cancellationToken)
				.ConfigureAwait(false);
			if (replacement.Replacement is null)
			{
				await destination.WriteAsync(
						content.AsMemory(replacement.SourceStart, replacement.SourceLength),
						cancellationToken)
					.ConfigureAwait(false);
			}
			else
			{
				await destination.WriteAsync(replacement.Replacement.AsMemory(), cancellationToken)
					.ConfigureAwait(false);
			}
			sourceOffset = replacement.SourceEnd;
		}
		await destination.WriteAsync(content.AsMemory(sourceOffset), cancellationToken).ConfigureAwait(false);
	}
}
