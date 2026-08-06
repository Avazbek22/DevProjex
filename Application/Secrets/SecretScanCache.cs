namespace DevProjex.Application.Secrets;

public enum SecretScanState
{
	Disabled = 0,
	Pending = 1,
	Scanning = 2,
	Completed = 3,
	Failed = 4
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
	public static SecretFileMetadata Capture(string path)
	{
		var info = new FileInfo(path);
		if (!info.Exists)
			throw new FileNotFoundException("The selected file no longer exists.", path);
		return new SecretFileMetadata(info.Length, info.LastWriteTimeUtc.Ticks);
	}
}

internal sealed record SecretFindingMetadata(
	string RuleId,
	int Start,
	int Length,
	string ValueFingerprint,
	int RuleOrder,
	SecretFindingSource Source,
	string? PersistentMarkHash,
	string? SessionMarkId);

internal sealed record SecretScanCacheEntry(
	string NormalizedPath,
	SecretFileMetadata FileMetadata,
	string ContentFingerprint,
	string RulesIdentity,
	int MarkedSecretsRevision,
	bool IsBinary,
	IReadOnlyList<SecretFindingMetadata> Findings,
	long ApproximateRetainedBytes);

internal sealed class SecretScanCache
{
	public const int DefaultMaximumEntries = 4_096;
	public const long DefaultMaximumRetainedBytes = 16L * 1024 * 1024;

	private readonly object _sync = new();
	private readonly int _maximumEntries;
	private readonly long _maximumRetainedBytes;
	private readonly Dictionary<string, LinkedListNode<SecretScanCacheEntry>> _entries =
		new(PathComparer.Default);
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

	public void SynchronizeSelection(string projectRoot, IReadOnlyList<string> selectedFiles)
	{
		var canonicalRoot = Path.GetFullPath(projectRoot);
		var selected = new HashSet<string>(selectedFiles.Select(Path.GetFullPath), PathComparer.Default);
		lock (_sync)
		{
			if (_projectRoot is null || !PathComparer.Default.Equals(_projectRoot, canonicalRoot))
			{
				ClearEntriesLocked();
				_projectRoot = canonicalRoot;
			}

			var stale = _entries.Keys.Where(path => !selected.Contains(path)).ToArray();
			foreach (var path in stale)
				RemoveLocked(path);
		}
	}

	public bool TryGetByMetadata(
		string path,
		SecretFileMetadata metadata,
		string rulesIdentity,
		int markedSecretsRevision,
		out SecretScanCacheEntry entry)
	{
		lock (_sync)
		{
			if (TryGetNodeLocked(path, metadata, rulesIdentity, markedSecretsRevision, out var node))
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
		int markedSecretsRevision,
		out SecretScanCacheEntry entry)
	{
		lock (_sync)
		{
			if (TryGetNodeLocked(path, metadata, rulesIdentity, markedSecretsRevision, out var node) &&
			    node.Value.ContentFingerprint.Equals(contentFingerprint, StringComparison.Ordinal))
			{
				_cacheHits++;
				TouchLocked(node);
				entry = node.Value;
				return true;
			}

			_cacheMisses++;
			RemoveLocked(Path.GetFullPath(path));
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
			RemoveLocked(entry.NormalizedPath);
			if (entry.ApproximateRetainedBytes > _maximumRetainedBytes)
				return;

			var node = _lru.AddFirst(entry);
			_entries.Add(entry.NormalizedPath, node);
			_retainedBytes += entry.ApproximateRetainedBytes;
			while (_entries.Count > _maximumEntries || _retainedBytes > _maximumRetainedBytes)
			{
				var oldest = _lru.Last;
				if (oldest is null)
					break;
				RemoveLocked(oldest.Value.NormalizedPath);
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
		string path,
		SecretFileMetadata metadata,
		string rulesIdentity,
		int markedSecretsRevision,
		out LinkedListNode<SecretScanCacheEntry> node)
	{
		var normalizedPath = Path.GetFullPath(path);
		if (_entries.TryGetValue(normalizedPath, out node!) &&
		    node.Value.FileMetadata == metadata &&
		    node.Value.RulesIdentity.Equals(rulesIdentity, StringComparison.Ordinal) &&
		    node.Value.MarkedSecretsRevision == markedSecretsRevision)
		{
			return true;
		}

		if (node is not null)
			RemoveLocked(normalizedPath);
		node = null!;
		return false;
	}

	private void TouchLocked(LinkedListNode<SecretScanCacheEntry> node)
	{
		_lru.Remove(node);
		_lru.AddFirst(node);
	}

	private void RemoveLocked(string path)
	{
		if (!_entries.Remove(path, out var node))
			return;
		_lru.Remove(node);
		_retainedBytes -= node.Value.ApproximateRetainedBytes;
	}

	private void ClearEntriesLocked()
	{
		_entries.Clear();
		_lru.Clear();
		_retainedBytes = 0;
	}
}
