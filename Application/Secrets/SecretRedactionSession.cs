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
	private readonly Func<SecretRedactionLegendText> _legendTextProvider;
	private readonly SecretScanCache _scanCache;
	private readonly object _sync = new();
	private readonly HashSet<string> _keptOccurrenceIds = new(StringComparer.Ordinal);
	private readonly Dictionary<string, SecretRedactionSnapshot> _snapshots = new(StringComparer.Ordinal);
	private Task? _detectorWarmUpTask;
	private long _overrideRevision;
	private int _activeFullContentBuffers;
	private int _peakFullContentBuffers;
	private bool _disposed;

	public SecretRedactionSession(
		ISecretDetector detector,
		SecretRedactionLegendText? legendText = null)
		: this(detector, () => legendText ?? SecretRedactionLegendText.English)
	{
	}

	public SecretRedactionSession(
		ISecretDetector detector,
		Func<SecretRedactionLegendText> legendTextProvider)
		: this(detector, legendTextProvider, new SecretScanCache())
	{
	}

	internal SecretRedactionSession(
		ISecretDetector detector,
		Func<SecretRedactionLegendText> legendTextProvider,
		SecretScanCache scanCache)
	{
		_detector = detector ?? throw new ArgumentNullException(nameof(detector));
		_legendTextProvider = legendTextProvider ??
		                      throw new ArgumentNullException(nameof(legendTextProvider));
		_scanCache = scanCache ?? throw new ArgumentNullException(nameof(scanCache));
	}

	public SecretRedactionLegendText LegendText =>
		_legendTextProvider() ??
		throw new InvalidOperationException("The secret-redaction legend provider returned null.");

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
		IReadOnlyList<string> orderedFilePaths)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
		ArgumentNullException.ThrowIfNull(orderedFilePaths);
		_scanCache.SynchronizeSelection(projectRoot, orderedFilePaths);

		HashSet<string> keptOccurrences;
		long overrideRevision;
		lock (_sync)
		{
			keptOccurrences = new HashSet<string>(_keptOccurrenceIds, StringComparer.Ordinal);
			overrideRevision = _overrideRevision;
		}

		return new SecretRedactionScope(
			this,
			projectRoot,
			orderedFilePaths,
			LegendText,
			keptOccurrences,
			overrideRevision);
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
	{
		var key = BuildSelectionKey(projectRoot, orderedFilePaths);
		lock (_sync)
			return _snapshots.TryGetValue(key, out var snapshot) ? snapshot.RedactedCount : null;
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
		out SecretScanCacheEntry entry) =>
		_scanCache.TryGetByMetadata(
			filePath,
			metadata,
			detectorScope.GetRulesIdentity(
				filePath,
				NormalizeRelativePath(projectRoot, filePath)),
			out entry);

	internal SecretScanCacheEntry GetOrDetectFindings(
		string projectRoot,
		string filePath,
		string content,
		SecretFileMetadata metadata,
		ISecretDetectionScope detectorScope,
		CancellationToken cancellationToken) =>
		GetOrDetectFindings(
			projectRoot,
			filePath,
			content.AsSpan(),
			metadata,
			detectorScope,
			cancellationToken);

	internal SecretScanCacheEntry GetOrDetectFindings(
		string projectRoot,
		string filePath,
		ReadOnlySpan<char> content,
		SecretFileMetadata metadata,
		ISecretDetectionScope detectorScope,
		CancellationToken cancellationToken)
	{
		var relativePath = NormalizeRelativePath(projectRoot, filePath);
		var rulesIdentity = detectorScope.GetRulesIdentity(filePath, relativePath);
		var contentFingerprint = HashText(content);
		if (_scanCache.TryGetByContent(
			    filePath,
			    metadata,
			    contentFingerprint,
			    rulesIdentity,
			    out var cached))
		{
			return cached;
		}

		var detected = SecretRedactionScope.ResolveNonOverlappingMatches(
			detectorScope.Detect(filePath, relativePath, content, cancellationToken));
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
				finding.RuleOrder);
		}

		var normalizedPath = Path.GetFullPath(filePath);
		var entry = new SecretScanCacheEntry(
			normalizedPath,
			metadata,
			contentFingerprint,
			rulesIdentity,
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
		ISecretDetectionScope detectorScope)
	{
		var normalizedPath = Path.GetFullPath(filePath);
		var relativePath = NormalizeRelativePath(projectRoot, filePath);
		var rulesIdentity = detectorScope.GetRulesIdentity(filePath, relativePath);
		var entry = new SecretScanCacheEntry(
			normalizedPath,
			metadata,
			ContentFingerprint: string.Empty,
			rulesIdentity,
			IsBinary: true,
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
	private readonly ISecretDetectionScope _detectorScope;
	private readonly Dictionary<string, int> _identityIndexes = new(StringComparer.Ordinal);
	private readonly Dictionary<string, int> _ruleIdentityCounts = new(StringComparer.Ordinal);
	private int _detectedCount;
	private int _redactedCount;
	private string? _placeholderExample;
	private bool _completed;

	internal SecretRedactionScope(
		SecretRedactionSession session,
		string projectRoot,
		IReadOnlyList<string> orderedFilePaths,
		SecretRedactionLegendText legendText,
		IReadOnlySet<string> keptOccurrenceIds,
		long overrideRevision)
	{
		_session = session;
		_projectRoot = Path.GetFullPath(projectRoot);
		_keptOccurrenceIds = keptOccurrenceIds;
		_overrideRevision = overrideRevision;
		_detectorScope = session.CreateDetectorScope(_projectRoot);
		LegendText = legendText;
		SelectionKey = SecretRedactionSession.BuildSelectionKey(_projectRoot, orderedFilePaths);
	}

	public string SelectionKey { get; }
	public int DetectedCount => _detectedCount;
	public int RedactedCount => _redactedCount;
	public string? PlaceholderExample => _placeholderExample;
	public SecretRedactionLegendText LegendText { get; }

	public bool TryAnalyzeCached(string filePath)
	{
		EnsureActive();
		if (!TryGetCachedEntry(filePath, SecretFileMetadata.Capture(filePath), out var entry))
			return false;
		ProcessFindings(filePath, entry.Findings);
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
			cancellationToken);
	}

	internal void AnalyzeBinary(string filePath, SecretFileMetadata metadata)
	{
		EnsureActive();
		_session.StoreBinary(_projectRoot, filePath, metadata, _detectorScope);
	}

	internal SecretScanCacheEntry StoreBinary(string filePath, SecretFileMetadata metadata)
	{
		EnsureActive();
		return _session.StoreBinary(_projectRoot, filePath, metadata, _detectorScope);
	}

	internal void ProcessEntry(string filePath, SecretScanCacheEntry entry)
	{
		EnsureActive();
		ProcessFindings(filePath, entry.Findings);
	}

	public SecretTextRedactionResult Redact(
		string filePath,
		string content,
		CancellationToken cancellationToken = default)
	{
		var plan = CreatePlan(filePath, content, cancellationToken);
		return plan.BuildResult(content);
	}

	internal SecretFileRedactionPlan CreatePlan(
		string filePath,
		string content,
		CancellationToken cancellationToken = default)
	{
		EnsureActive();
		var metadata = SecretFileMetadata.Capture(filePath);
		if (metadata.Length > SecretRedactionOutputPreparer.MaximumScannableFileBytes)
		{
			throw new SecretScanLimitExceededException(
				filePath,
				metadata.Length,
				SecretRedactionOutputPreparer.MaximumScannableFileBytes);
		}
		var entry = _session.GetOrDetectFindings(
			_projectRoot,
			filePath,
			content,
			metadata,
			_detectorScope,
			cancellationToken);
		return ProcessFindings(filePath, entry.Findings);
	}

	internal IDisposable TrackFullContentBuffer() => _session.TrackFullContentBuffer();

	public SecretRedactionSnapshot Complete()
	{
		EnsureActive();
		_completed = true;
		var snapshot = new SecretRedactionSnapshot(SelectionKey, _detectedCount, _redactedCount);
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
				kept ? SecretPreviewSpanState.KeptAsIs : SecretPreviewSpanState.Redacted);
			outputDelta = checked(outputDelta + outputLength - finding.Length);
			_detectedCount++;
			if (!kept)
			{
				_redactedCount++;
				redactedInFile++;
				_placeholderExample ??= replacement;
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

		var candidates = matches
			.OrderBy(static match => IsGenericRule(match.RuleId))
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
