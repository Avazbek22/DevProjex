namespace DevProjex.Application.Secrets;

public enum SecretScanState
{
	Disabled = 0,
	Pending = 1,
	Scanning = 2,
	Completed = 3,
	Failed = 4,
	Limited = 5
}

public sealed record SecretScanCacheDiagnostics(
	int EntryCount,
	long RetainedBytes,
	int MaximumEntries,
	long MaximumRetainedBytes,
	long CacheHits,
	long CacheMisses,
	long DetectionRuns,
	int ActiveFullContentBuffers,
	int PeakFullContentBuffers);

internal readonly record struct SecretFileMetadata(long Length, long LastWriteUtcTicks)
{
	public static SecretFileMetadata FromIdentity(FileContentIdentity identity) =>
		new(identity.Length, identity.LastWriteTimeUtcTicks);

	public static SecretFileMetadata Capture(string path)
	{
		var info = new FileInfo(path);
		if (!info.Exists)
			throw new FileNotFoundException("The selected file no longer exists.", path);
		return new SecretFileMetadata(info.Length, info.LastWriteTimeUtc.Ticks);
	}
}

internal sealed class SecretFindingCandidateMetadata(
	int RawStart,
	int RawLength,
	string RuleId,
	string ValueFingerprint,
	int RuleOrder,
	SecretFindingSource Source,
	string? PersistentMarkHash,
	string? SessionMarkId,
	PersistentSecretMarkId? PersistentMarkId,
	RedactionFindingCategory Category,
	SecretOccurrenceCoordinateIdentity OccurrenceCoordinateIdentity) :
	IEquatable<SecretFindingCandidateMetadata>
{
	private string? _occurrenceId;

	public int RawStart { get; } = RawStart;
	public int RawLength { get; } = RawLength;
	public string RuleId { get; } = RuleId;
	public string ValueFingerprint { get; } = ValueFingerprint;
	public int RuleOrder { get; } = RuleOrder;
	public SecretFindingSource Source { get; } = Source;
	public string? PersistentMarkHash { get; } = PersistentMarkHash;
	public string? SessionMarkId { get; } = SessionMarkId;
	public PersistentSecretMarkId? PersistentMarkId { get; } = PersistentMarkId;
	public RedactionFindingCategory Category { get; } = Category;
	public SecretOccurrenceCoordinateIdentity OccurrenceCoordinateIdentity { get; } = OccurrenceCoordinateIdentity;
	public SecretFindingIdentity Identity { get; } = new(RuleId, ValueFingerprint);

	public string? GetCachedOccurrenceId() => Volatile.Read(ref _occurrenceId);

	public string CacheOccurrenceId(string occurrenceId) =>
		Interlocked.CompareExchange(ref _occurrenceId, occurrenceId, null) ?? occurrenceId;

	public bool Equals(SecretFindingCandidateMetadata? other) =>
		ReferenceEquals(this, other) ||
		other is not null &&
		RawStart == other.RawStart &&
		RawLength == other.RawLength &&
		RuleOrder == other.RuleOrder &&
		Source == other.Source &&
		PersistentMarkId == other.PersistentMarkId &&
		Category == other.Category &&
		OccurrenceCoordinateIdentity == other.OccurrenceCoordinateIdentity &&
		string.Equals(RuleId, other.RuleId, StringComparison.Ordinal) &&
		string.Equals(ValueFingerprint, other.ValueFingerprint, StringComparison.Ordinal) &&
		string.Equals(PersistentMarkHash, other.PersistentMarkHash, StringComparison.Ordinal) &&
		string.Equals(SessionMarkId, other.SessionMarkId, StringComparison.Ordinal);

	public override bool Equals(object? value) =>
		Equals(value as SecretFindingCandidateMetadata);

	public override int GetHashCode() => HashCode.Combine(
		HashCode.Combine(RawStart, RawLength, RuleOrder, Source),
		HashCode.Combine(PersistentMarkId, Category, OccurrenceCoordinateIdentity),
		HashCode.Combine(
			StringComparer.Ordinal.GetHashCode(RuleId),
			StringComparer.Ordinal.GetHashCode(ValueFingerprint),
			PersistentMarkHash is null ? 0 : StringComparer.Ordinal.GetHashCode(PersistentMarkHash),
			SessionMarkId is null ? 0 : StringComparer.Ordinal.GetHashCode(SessionMarkId)));
}

internal readonly record struct SecretOccurrenceCoordinateIdentity(
	bool IsSourceBacked,
	int Start,
	int Length);

internal sealed class SecretFindingIdentity(
	string ruleId,
	string valueFingerprint) : IEquatable<SecretFindingIdentity>
{
	private readonly string _ruleId = ruleId;
	private readonly string _valueFingerprint = valueFingerprint;
	private readonly int _hashCode = HashCode.Combine(
		StringComparer.Ordinal.GetHashCode(ruleId),
		StringComparer.Ordinal.GetHashCode(valueFingerprint));

	public bool Equals(SecretFindingIdentity? other) =>
		ReferenceEquals(this, other) ||
		other is not null &&
		string.Equals(_ruleId, other._ruleId, StringComparison.Ordinal) &&
		string.Equals(_valueFingerprint, other._valueFingerprint, StringComparison.Ordinal);

	public override bool Equals(object? value) => Equals(value as SecretFindingIdentity);
	public override int GetHashCode() => _hashCode;
}

internal sealed record SecretFindingSegmentMetadata(
	int Start,
	int Length,
	IReadOnlyList<int> CandidateIndexes);

internal sealed record SecretScanCacheEntry(
	string NormalizedPath,
	SecretFileMetadata FileMetadata,
	string ContentFingerprint,
	string RulesIdentity,
	string TransformIdentity,
	string OccurrenceProjectRoot,
	string OccurrenceRelativePath,
	int MarkedSecretsRevision,
	bool IsBinary,
	IReadOnlyList<SecretFindingCandidateMetadata> Candidates,
	IReadOnlyList<SecretFindingSegmentMetadata> Segments,
	long ApproximateRetainedBytes,
	FileContentClassification? UnscannableClassification = null)
{
	/// <summary>
	/// Text the scanner was not allowed to read. Every entry produced from real text carries a
	/// fingerprint of that text, so an empty one on a non-binary entry is what distinguishes
	/// "never looked" from "looked and found nothing" - the two must not read alike.
	/// </summary>
	public bool IsUnscannable => UnscannableClassification is not null;
}

internal sealed class SecretScanCache
{
	public const int DefaultMaximumEntries = 4_096;
	public const long DefaultMaximumRetainedBytes = 16L * 1024 * 1024;

	private readonly object _sync = new();
	private readonly int _maximumEntries;
	private readonly long _maximumRetainedBytes;
	private readonly Dictionary<SecretScanCacheKey, LinkedListNode<SecretScanCacheEntry>> _entries =
		new(SecretScanCacheKeyComparer.Instance);
	private readonly LinkedList<SecretScanCacheEntry> _lru = new();
	private string? _projectRoot;
	private long _retainedBytes;
	private long _cacheHits;
	private long _cacheMisses;
	private long _detectionRuns;

	public SecretScanCache(
		int maximumEntries = DefaultMaximumEntries,
		long maximumRetainedBytes = DefaultMaximumRetainedBytes)
	{
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumEntries);
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumRetainedBytes);
		_maximumEntries = maximumEntries;
		_maximumRetainedBytes = maximumRetainedBytes;
	}

	public int MaximumEntries => _maximumEntries;
	public long MaximumRetainedBytes => _maximumRetainedBytes;

	public void SynchronizeProject(string projectRoot)
	{
		var canonicalRoot = PathUtility.Normalize(projectRoot);
		lock (_sync)
		{
			if (_projectRoot is null || !ProjectTreePathIdentity.CanonicalComparer.Equals(_projectRoot, canonicalRoot))
			{
				ClearEntriesLocked();
				_projectRoot = canonicalRoot;
			}
		}
	}

	public bool TryGetByMetadata(
		string path,
		SecretFileMetadata metadata,
		string rulesIdentity,
		string transformIdentity,
		int markedSecretsRevision,
		out SecretScanCacheEntry entry)
	{
		lock (_sync)
		{
			var key = CreateKey(path, rulesIdentity, transformIdentity, markedSecretsRevision);
			if (TryGetNodeLocked(key, metadata, out var node))
			{
				_cacheHits++;
				TouchLocked(node);
				entry = node.Value;
				return true;
			}

			_cacheMisses++;
			entry = null!;
			return false;
		}
	}

	public bool TryGetByContent(
		string path,
		SecretFileMetadata metadata,
		string contentFingerprint,
		string rulesIdentity,
		string transformIdentity,
		int markedSecretsRevision,
		out SecretScanCacheEntry entry)
	{
		lock (_sync)
		{
			var key = CreateKey(path, rulesIdentity, transformIdentity, markedSecretsRevision);
			if (TryGetNodeLocked(key, metadata, out var node) &&
			    node.Value.ContentFingerprint.Equals(contentFingerprint, StringComparison.Ordinal))
			{
				_cacheHits++;
				TouchLocked(node);
				entry = node.Value;
				return true;
			}

			_cacheMisses++;
			RemoveLocked(key);
			entry = null!;
			return false;
		}
	}

	public void Store(SecretScanCacheEntry entry, bool detectionExecuted)
	{
		lock (_sync)
		{
			if (detectionExecuted)
				_detectionRuns++;
			var key = CreateKey(entry);
			RemoveLocked(key);
			if (entry.ApproximateRetainedBytes > _maximumRetainedBytes)
				return;

			var node = _lru.AddFirst(entry);
			_entries.Add(key, node);
			_retainedBytes += entry.ApproximateRetainedBytes;
			while (_entries.Count > _maximumEntries || _retainedBytes > _maximumRetainedBytes)
			{
				var oldest = _lru.Last;
				if (oldest is null)
					break;
				RemoveLocked(CreateKey(oldest.Value));
			}
		}
	}

	public void Clear()
	{
		lock (_sync)
		{
			ClearEntriesLocked();
			_projectRoot = null;
		}
	}

	public (int EntryCount, long RetainedBytes, long Hits, long Misses, long DetectionRuns) Capture()
	{
		lock (_sync)
			return (_entries.Count, _retainedBytes, _cacheHits, _cacheMisses, _detectionRuns);
	}

	private bool TryGetNodeLocked(
		SecretScanCacheKey key,
		SecretFileMetadata metadata,
		out LinkedListNode<SecretScanCacheEntry> node)
	{
		if (_entries.TryGetValue(key, out node!) && node.Value.FileMetadata == metadata)
		{
			return true;
		}

		if (node is not null)
			RemoveLocked(key);
		node = null!;
		return false;
	}

	private void TouchLocked(LinkedListNode<SecretScanCacheEntry> node)
	{
		_lru.Remove(node);
		_lru.AddFirst(node);
	}

	private void RemoveLocked(SecretScanCacheKey key)
	{
		if (!_entries.Remove(key, out var node))
			return;
		_lru.Remove(node);
		_retainedBytes -= node.Value.ApproximateRetainedBytes;
	}

	private static SecretScanCacheKey CreateKey(
		string path,
		string rulesIdentity,
		string transformIdentity,
		int markedSecretsRevision) =>
		new(PathUtility.Normalize(path), rulesIdentity, transformIdentity, markedSecretsRevision);

	private static SecretScanCacheKey CreateKey(SecretScanCacheEntry entry) =>
		new(
			entry.NormalizedPath,
			entry.RulesIdentity,
			entry.TransformIdentity,
			entry.MarkedSecretsRevision);

	private void ClearEntriesLocked()
	{
		_entries.Clear();
		_lru.Clear();
		_retainedBytes = 0;
	}

	private readonly record struct SecretScanCacheKey(
		string NormalizedPath,
		string RulesIdentity,
		string TransformIdentity,
		int MarkedSecretsRevision);

	private sealed class SecretScanCacheKeyComparer : IEqualityComparer<SecretScanCacheKey>
	{
		public static SecretScanCacheKeyComparer Instance { get; } = new();

		public bool Equals(SecretScanCacheKey x, SecretScanCacheKey y) =>
			x.MarkedSecretsRevision == y.MarkedSecretsRevision &&
			ProjectTreePathIdentity.CanonicalComparer.Equals(x.NormalizedPath, y.NormalizedPath) &&
			x.RulesIdentity.Equals(y.RulesIdentity, StringComparison.Ordinal) &&
			x.TransformIdentity.Equals(y.TransformIdentity, StringComparison.Ordinal);

		public int GetHashCode(SecretScanCacheKey key) => HashCode.Combine(
			ProjectTreePathIdentity.CanonicalComparer.GetHashCode(key.NormalizedPath),
			StringComparer.Ordinal.GetHashCode(key.RulesIdentity),
			StringComparer.Ordinal.GetHashCode(key.TransformIdentity),
			key.MarkedSecretsRevision);
	}
}
