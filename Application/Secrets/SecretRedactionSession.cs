using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace DevProjex.Application.Secrets;

/// <summary>
/// Owns session-only keep-as-is decisions and a bounded cache of compact findings. Source and
/// redacted file contents are operation-local and are never retained by this object.
/// </summary>
public sealed class SecretRedactionSession : IDisposable
{
	private readonly ISecretDetector _detector;
	private readonly SecretScanCache _scanCache;
	private readonly object _sync = new();
	private readonly HashSet<string> _keptOccurrenceIds = new(StringComparer.Ordinal);
	private readonly Dictionary<string, MarkedSecretProfileEntry> _persistentMarks =
		new(StringComparer.OrdinalIgnoreCase);
	private readonly List<SessionMarkedSecret> _sessionMarks = [];
	private readonly Dictionary<string, SecretRedactionSnapshot> _snapshots = new(StringComparer.Ordinal);
	private Task? _detectorWarmUpTask;
	private long _overrideRevision;
	private int _markedSecretsRevision;
	private int _activeFullContentBuffers;
	private int _peakFullContentBuffers;
	private bool _disposed;

	public SecretRedactionSession(ISecretDetector detector)
		: this(detector, new SecretScanCache())
	{
	}

	internal SecretRedactionSession(
		ISecretDetector detector,
		SecretScanCache scanCache)
	{
		_detector = detector ?? throw new ArgumentNullException(nameof(detector));
		_scanCache = scanCache ?? throw new ArgumentNullException(nameof(scanCache));
	}

	public event EventHandler? OverridesChanged;
	public event EventHandler<SecretRedactionSnapshotPublishedEventArgs>? SnapshotPublished;

	/// <summary>
	/// Starts rule-engine initialization once for the process session. It retains compiled rules,
	/// never project content, and can safely overlap selection and preview preparation.
	/// </summary>
	public Task BeginWarmUp()
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		lock (_sync)
		{
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

	public SecretRedactionScope BeginOutput(
		string projectRoot,
		IReadOnlyList<string> orderedFilePaths,
		string transformIdentity = "")
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
		ArgumentNullException.ThrowIfNull(orderedFilePaths);
		_scanCache.SynchronizeSelection(projectRoot, orderedFilePaths);

		HashSet<string> keptOccurrences;
		long overrideRevision;
		MarkedSecretsMatcher markedSecretsMatcher;
		int markedSecretsRevision;
		lock (_sync)
		{
			keptOccurrences = new HashSet<string>(_keptOccurrenceIds, StringComparer.Ordinal);
			overrideRevision = _overrideRevision;
			markedSecretsRevision = _markedSecretsRevision;
			markedSecretsMatcher = new MarkedSecretsMatcher(
				_persistentMarks.Values,
				_sessionMarks);
		}

		return new SecretRedactionScope(
			this,
			projectRoot,
			orderedFilePaths,
			keptOccurrences,
			overrideRevision,
			markedSecretsMatcher,
			markedSecretsRevision,
			transformIdentity);
	}

	public IReadOnlyCollection<MarkedSecretProfileEntry> GetMarkedSecrets()
	{
		lock (_sync)
			return _persistentMarks.Values.OrderBy(static mark => mark.H, StringComparer.Ordinal).ToArray();
	}

	public void ReplaceMarkedSecrets(IEnumerable<MarkedSecretProfileEntry>? marks)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		var replacement = (marks ?? [])
			.Where(static mark => mark is not null)
			.GroupBy(static mark => mark.H, StringComparer.OrdinalIgnoreCase)
			.Select(static group => group.First())
			.ToDictionary(static mark => mark.H, StringComparer.OrdinalIgnoreCase);

		lock (_sync)
		{
			if (_persistentMarks.Count == replacement.Count &&
			    _persistentMarks.All(pair => replacement.TryGetValue(pair.Key, out var value) && value == pair.Value))
			{
				return;
			}

			_persistentMarks.Clear();
			foreach (var (hash, mark) in replacement)
				_persistentMarks.Add(hash, mark);
			AdvanceMarkedSecretsRevisionLocked();
		}
		OverridesChanged?.Invoke(this, EventArgs.Empty);
	}

	public bool AddMarkedSecret(MarkedSecretProfileEntry mark)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		ArgumentNullException.ThrowIfNull(mark);
		bool changed;
		lock (_sync)
		{
			changed = !_persistentMarks.TryGetValue(mark.H, out var existing) || existing != mark;
			_persistentMarks[mark.H] = mark;
			if (changed)
				AdvanceMarkedSecretsRevisionLocked();
		}
		if (changed)
			OverridesChanged?.Invoke(this, EventArgs.Empty);
		return changed;
	}

	public bool RemoveMarkedSecret(string hash)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(hash);
		return RemoveManualSecret(hash, null).PersistentMarkRemoved;
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
		return RemoveManualSecret(null, sessionMarkId).SessionMarkRemoved;
	}

	public ManualSecretMarkRemovalResult RemoveManualSecret(
		string? persistentMarkHash,
		string? sessionMarkId)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		var persistentRemoved = false;
		var sessionRemoved = false;
		lock (_sync)
		{
			if (!string.IsNullOrWhiteSpace(persistentMarkHash))
				persistentRemoved = _persistentMarks.Remove(persistentMarkHash);
			if (!string.IsNullOrWhiteSpace(sessionMarkId))
			{
				sessionRemoved = _sessionMarks.RemoveAll(mark =>
					string.Equals(mark.Id, sessionMarkId, StringComparison.Ordinal)) > 0;
			}
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
			kept = _keptOccurrenceIds.Add(occurrenceId);
			if (!kept)
				_keptOccurrenceIds.Remove(occurrenceId);
			_overrideRevision++;
			_snapshots.Clear();
		}

		OverridesChanged?.Invoke(this, EventArgs.Empty);
		return kept;
	}

	public int? GetRedactionCount(string projectRoot, IReadOnlyList<string> orderedFilePaths)
		=> GetSnapshot(projectRoot, orderedFilePaths)?.RedactedCount;

	public SecretRedactionSnapshot? GetSnapshot(
		string projectRoot,
		IReadOnlyList<string> orderedFilePaths)
	{
		var key = BuildSelectionKey(projectRoot, orderedFilePaths);
		lock (_sync)
			return _snapshots.GetValueOrDefault(key);
	}

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
			_snapshots.Clear();
	}

	/// <summary>
	/// Releases all content-derived state when Hide Secrets is switched off. Keep-as-is decisions
	/// remain session-only preferences and can be applied again if the user re-enables the option.
	/// </summary>
	public void Disable()
	{
		_scanCache.Clear();
		InvalidateSnapshots();
	}

	/// <summary>
	/// Releases all project-specific state when the active workspace changes or the window closes.
	/// </summary>
	public void Reset()
	{
		_scanCache.Clear();
		lock (_sync)
		{
			_snapshots.Clear();
			_keptOccurrenceIds.Clear();
			_persistentMarks.Clear();
			_sessionMarks.Clear();
			_markedSecretsRevision++;
			_overrideRevision++;
		}
	}

	public void Dispose()
	{
		if (_disposed)
			return;
		_disposed = true;
		Reset();
	}

	internal bool TryGetCachedFindings(
		string projectRoot,
		string filePath,
		SecretFileMetadata metadata,
		ISecretDetectionScope detectorScope,
		int markedSecretsRevision,
		string transformIdentity,
		out SecretScanCacheEntry entry) =>
		_scanCache.TryGetByMetadata(
			filePath,
			metadata,
			ComposeRulesIdentity(
				detectorScope.GetRulesIdentity(
					filePath,
					NormalizeRelativePath(projectRoot, filePath)),
				transformIdentity),
			markedSecretsRevision,
			out entry);

	/// <summary>
	/// Findings describe positions in the text that was scanned. A metadata-only cache hit would
	/// otherwise serve offsets taken from the uncompressed file after compression is switched on,
	/// so the transformation that produced the text is part of the cache identity.
	/// </summary>
	private static string ComposeRulesIdentity(string rulesIdentity, string transformIdentity) =>
		transformIdentity.Length == 0 ? rulesIdentity : $"{rulesIdentity}|{transformIdentity}";

	internal SecretScanCacheEntry GetOrDetectFindings(
		string projectRoot,
		string filePath,
		string content,
		SecretFileMetadata metadata,
		ISecretDetectionScope detectorScope,
		MarkedSecretsMatcher markedSecretsMatcher,
		int markedSecretsRevision,
		string transformIdentity,
		CancellationToken cancellationToken,
		ContentTransformMap? transformMap = null) =>
		GetOrDetectFindings(
			projectRoot,
			filePath,
			content.AsSpan(),
			metadata,
			detectorScope,
			markedSecretsMatcher,
			markedSecretsRevision,
			transformIdentity,
			cancellationToken,
			transformMap);

	internal SecretScanCacheEntry GetOrDetectFindings(
		string projectRoot,
		string filePath,
		ReadOnlySpan<char> content,
		SecretFileMetadata metadata,
		ISecretDetectionScope detectorScope,
		MarkedSecretsMatcher markedSecretsMatcher,
		int markedSecretsRevision,
		string transformIdentity,
		CancellationToken cancellationToken,
		ContentTransformMap? transformMap = null)
	{
		var relativePath = NormalizeRelativePath(projectRoot, filePath);
		var rulesIdentity = ComposeRulesIdentity(
			detectorScope.GetRulesIdentity(filePath, relativePath),
			transformIdentity);
		var contentFingerprint = HashText(content);
		if (_scanCache.TryGetByContent(
			    filePath,
			    metadata,
			    contentFingerprint,
			    rulesIdentity,
			    markedSecretsRevision,
			    out var cached))
		{
			return cached;
		}

		var detectorFindings = detectorScope.Detect(filePath, relativePath, content, cancellationToken);
		var markedFindings = markedSecretsMatcher.Match(
			relativePath,
			content,
			transformMap,
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
				finding.SessionMarkId);
		}

		var normalizedPath = Path.GetFullPath(filePath);
		var entry = new SecretScanCacheEntry(
			normalizedPath,
			metadata,
			contentFingerprint,
			rulesIdentity,
			markedSecretsRevision,
			IsBinary: false,
			findings,
			EstimateRetainedBytes(
				normalizedPath,
				contentFingerprint,
				rulesIdentity,
				findings));
		_scanCache.Store(entry, detectionExecuted: true);
		return entry;
	}

	internal SecretScanCacheEntry StoreBinary(
		string projectRoot,
		string filePath,
		SecretFileMetadata metadata,
		ISecretDetectionScope detectorScope,
		int markedSecretsRevision)
	{
		var normalizedPath = Path.GetFullPath(filePath);
		var relativePath = NormalizeRelativePath(projectRoot, filePath);
		var rulesIdentity = detectorScope.GetRulesIdentity(filePath, relativePath);
		var entry = new SecretScanCacheEntry(
			normalizedPath,
			metadata,
			ContentFingerprint: string.Empty,
			rulesIdentity,
			markedSecretsRevision,
			IsBinary: true,
			Findings: [],
			ApproximateRetainedBytes: 96 +
			                          (normalizedPath.Length + rulesIdentity.Length) * sizeof(char));
		_scanCache.Store(entry, detectionExecuted: false);
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
		int markedSecretsRevision)
	{
		var normalizedPath = Path.GetFullPath(filePath);
		var relativePath = NormalizeRelativePath(projectRoot, filePath);
		var rulesIdentity = detectorScope.GetRulesIdentity(filePath, relativePath);
		var entry = new SecretScanCacheEntry(
			normalizedPath,
			metadata,
			ContentFingerprint: string.Empty,
			rulesIdentity,
			markedSecretsRevision,
			IsBinary: false,
			Findings: [],
			ApproximateRetainedBytes: 96 +
			                          (normalizedPath.Length + rulesIdentity.Length) * sizeof(char));
		_scanCache.Store(entry, detectionExecuted: false);
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

	internal void Publish(SecretRedactionSnapshot snapshot, long overrideRevision)
	{
		lock (_sync)
		{
			if (overrideRevision != _overrideRevision)
				return;
			_snapshots.Clear();
			_snapshots[snapshot.SelectionKey] = snapshot;
		}
		SnapshotPublished?.Invoke(this, new SecretRedactionSnapshotPublishedEventArgs(snapshot));
	}

	internal static string BuildSelectionKey(string projectRoot, IReadOnlyList<string> orderedFilePaths)
	{
		using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
		AppendHashValue(hash, Path.GetFullPath(projectRoot));
		var relativePaths = orderedFilePaths
			.Select(path => NormalizeRelativePath(projectRoot, path))
			.OrderBy(static path => path, StringComparer.Ordinal);
		foreach (var relativePath in relativePaths)
			AppendHashValue(hash, relativePath);
		return Convert.ToHexString(hash.GetHashAndReset());
	}

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
		IReadOnlyList<SecretFindingMetadata> findings)
	{
		long bytes = 160 +
		             (normalizedPath.Length + contentFingerprint.Length + rulesIdentity.Length) * sizeof(char);
		foreach (var finding in findings)
			bytes += 64 + (finding.RuleId.Length + finding.ValueFingerprint.Length) * sizeof(char);
		return bytes;
	}

	private static void AppendHashValue(IncrementalHash hash, string value)
	{
		var bytes = Encoding.UTF8.GetBytes(value);
		hash.AppendData(BitConverter.GetBytes(bytes.Length));
		hash.AppendData(bytes);
	}

	private void AdvanceMarkedSecretsRevisionLocked()
	{
		_markedSecretsRevision++;
		_overrideRevision++;
		_snapshots.Clear();
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
	private readonly MarkedSecretsMatcher _markedSecretsMatcher;
	private readonly int _markedSecretsRevision;
	private readonly string _transformIdentity;
	private readonly ISecretDetectionScope _detectorScope;
	private readonly Dictionary<string, int> _identityIndexes = new(StringComparer.Ordinal);
	private readonly Dictionary<string, int> _ruleIdentityCounts = new(StringComparer.Ordinal);
	private readonly Dictionary<string, int> _markedSecretCounts = new(StringComparer.OrdinalIgnoreCase);
	private int _detectedCount;
	private int _redactedCount;
	// The count scan runs files in parallel, so "first" would depend on thread timing. Keeping the
	// ordinally smallest path makes the reported file the same on every run.
	private string? _unscannablePath;
	private bool _completed;

	internal SecretRedactionScope(
		SecretRedactionSession session,
		string projectRoot,
		IReadOnlyList<string> orderedFilePaths,
		IReadOnlySet<string> keptOccurrenceIds,
		long overrideRevision,
		MarkedSecretsMatcher markedSecretsMatcher,
		int markedSecretsRevision,
		string transformIdentity = "")
	{
		_session = session;
		_transformIdentity = transformIdentity;
		_projectRoot = Path.GetFullPath(projectRoot);
		_keptOccurrenceIds = keptOccurrenceIds;
		_overrideRevision = overrideRevision;
		_markedSecretsMatcher = markedSecretsMatcher;
		_markedSecretsRevision = markedSecretsRevision;
		_detectorScope = session.CreateDetectorScope(_projectRoot);
		SelectionKey = SecretRedactionSession.BuildSelectionKey(_projectRoot, orderedFilePaths);
	}

	public string SelectionKey { get; }
	public int DetectedCount => _detectedCount;
	public int RedactedCount => _redactedCount;

	public bool TryAnalyzeCached(string filePath)
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
		return _session.TryGetCachedFindings(
			_projectRoot,
			filePath,
			metadata,
			_detectorScope,
			_markedSecretsRevision,
			_transformIdentity,
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
		return _session.GetOrDetectFindings(
			_projectRoot,
			filePath,
			content,
			metadata,
			_detectorScope,
			_markedSecretsMatcher,
			_markedSecretsRevision,
			_transformIdentity,
			cancellationToken);
	}

	internal void AnalyzeBinary(string filePath, SecretFileMetadata metadata)
	{
		EnsureActive();
		_session.StoreBinary(
			_projectRoot,
			filePath,
			metadata,
			_detectorScope,
			_markedSecretsRevision);
	}

	internal SecretScanCacheEntry StoreBinary(string filePath, SecretFileMetadata metadata)
	{
		EnsureActive();
		return _session.StoreBinary(
			_projectRoot,
			filePath,
			metadata,
			_detectorScope,
			_markedSecretsRevision);
	}

	internal SecretScanCacheEntry StoreUnscannable(string filePath, SecretFileMetadata metadata)
	{
		EnsureActive();
		RecordUnscannable(filePath);
		return _session.StoreUnscannable(
			_projectRoot,
			filePath,
			metadata,
			_detectorScope,
			_markedSecretsRevision);
	}

	internal void AnalyzeUnscannable(string filePath, SecretFileMetadata metadata)
	{
		EnsureActive();
		RecordUnscannable(filePath);
		_session.StoreUnscannable(
			_projectRoot,
			filePath,
			metadata,
			_detectorScope,
			_markedSecretsRevision);
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
		EnsureActive();
		// Also runs for entries served from the cache, which is the only way a second scan of the
		// same unchanged file would otherwise forget that it was never read.
		if (entry.IsUnscannable)
			RecordUnscannable(filePath);
		ProcessFindings(filePath, entry.Findings);
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
		var plan = CreatePlan(filePath, content, transformMap, cancellationToken);
		return plan.BuildResult(content);
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
		CancellationToken cancellationToken = default)
	{
		EnsureActive();
		var metadata = SecretFileMetadata.Capture(filePath);
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
			_markedSecretsRevision,
			_transformIdentity,
			cancellationToken,
			transformMap);
		return ProcessFindings(filePath, entry.Findings);
	}

	internal IDisposable TrackFullContentBuffer() => _session.TrackFullContentBuffer();

	public SecretRedactionSnapshot Complete()
	{
		EnsureActive();
		_completed = true;
		var snapshot = new SecretRedactionSnapshot(
			SelectionKey,
			_detectedCount,
			_redactedCount,
			new Dictionary<string, int>(_markedSecretCounts, StringComparer.OrdinalIgnoreCase),
			Volatile.Read(ref _unscannablePath));
		_session.Publish(snapshot, _overrideRevision);
		return snapshot;
	}

	private SecretFileRedactionPlan ProcessFindings(
		string filePath,
		IReadOnlyList<SecretFindingMetadata> findings)
	{
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

			var occurrenceId = SecretRedactionSession.HashValue(
				$"{_projectRoot}\n{relativePath}\n{finding.RuleId}\n{finding.ValueFingerprint}\n{finding.Start}\n{finding.Length}".AsSpan());
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
				finding.SessionMarkId);
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

	private void EnsureActive()
	{
		if (_completed)
			throw new InvalidOperationException("The redaction output scope is already complete.");
	}

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
		return winner with
		{
			Source = source,
			PersistentMarkHash = persistentHash,
			SessionMarkId = sessionMarkId
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
			return new SecretTextRedactionResult(content, Spans, 0, 0);

		var estimatedLength = content.Length;
		foreach (var replacement in Replacements)
			estimatedLength = checked(estimatedLength + (replacement.Replacement?.Length ?? replacement.SourceLength) - replacement.SourceLength);
		var builder = new StringBuilder(estimatedLength);
		AppendTo(builder, content, content.Length);
		return new SecretTextRedactionResult(builder.ToString(), Spans, DetectedCount, RedactedCount);
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
