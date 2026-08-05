using System.Security.Cryptography;

namespace DevProjex.Application.Secrets;

/// <summary>
/// Owns user decisions for one application process. Overrides deliberately do not persist:
/// durable secret fingerprints would create sensitive, stale profile state after source changes.
/// </summary>
public sealed class SecretRedactionSession
{
	private readonly ISecretDetector _detector;
	private readonly Func<SecretRedactionLegendText> _legendTextProvider;
	private readonly object _sync = new();
	private readonly HashSet<string> _keptOccurrenceIds = new(StringComparer.Ordinal);
	private readonly Dictionary<string, SecretRedactionSnapshot> _snapshots = new(StringComparer.Ordinal);
	private long _overrideRevision;

	public SecretRedactionSession(
		ISecretDetector detector,
		SecretRedactionLegendText? legendText = null)
		: this(detector, () => legendText ?? SecretRedactionLegendText.English)
	{
	}

	public SecretRedactionSession(
		ISecretDetector detector,
		Func<SecretRedactionLegendText> legendTextProvider)
	{
		_detector = detector ?? throw new ArgumentNullException(nameof(detector));
		_legendTextProvider = legendTextProvider ??
		                      throw new ArgumentNullException(nameof(legendTextProvider));
	}

	public SecretRedactionLegendText LegendText =>
		_legendTextProvider() ??
		throw new InvalidOperationException("The secret-redaction legend provider returned null.");

	public event EventHandler? OverridesChanged;
	public event EventHandler<SecretRedactionSnapshotPublishedEventArgs>? SnapshotPublished;

	public SecretRedactionScope BeginOutput(
		string projectRoot,
		IReadOnlyList<string> orderedFilePaths)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
		ArgumentNullException.ThrowIfNull(orderedFilePaths);
		HashSet<string> keptOccurrences;
		long overrideRevision;
		lock (_sync)
		{
			keptOccurrences = new HashSet<string>(_keptOccurrenceIds, StringComparer.Ordinal);
			overrideRevision = _overrideRevision;
		}

		return new SecretRedactionScope(
			this,
			_detector,
			projectRoot,
			orderedFilePaths,
			LegendText,
			keptOccurrences,
			overrideRevision);
	}

	public bool ToggleKeepAsIs(string occurrenceId)
	{
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

	public void InvalidateSnapshots()
	{
		lock (_sync)
			_snapshots.Clear();
	}

	internal void Publish(SecretRedactionSnapshot snapshot, long overrideRevision)
	{
		lock (_sync)
		{
			// An output already in flight may finish after a keep-as-is decision changed.
			// Its artifact remains internally coherent, but its count must not replace the
			// snapshot for the newer decision state shown by the interactive surfaces.
			if (overrideRevision != _overrideRevision)
				return;
			_snapshots[snapshot.SelectionKey] = snapshot;
		}
		SnapshotPublished?.Invoke(this, new SecretRedactionSnapshotPublishedEventArgs(snapshot));
	}

	internal static string BuildSelectionKey(string projectRoot, IReadOnlyList<string> orderedFilePaths)
	{
		using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
		AppendHashValue(hash, Path.GetFullPath(projectRoot));
		// Callers may hold the same effective selection in tree order, inventory order, or
		// canonical export order. Snapshot identity describes the selected set, so normalize
		// its ordering here instead of coupling count publication to a particular surface.
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

	internal static string HashValue(string value) =>
		Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

	private static void AppendHashValue(IncrementalHash hash, string value)
	{
		var bytes = Encoding.UTF8.GetBytes(value);
		hash.AppendData(BitConverter.GetBytes(bytes.Length));
		hash.AppendData(bytes);
	}
}

public sealed class SecretRedactionSnapshotPublishedEventArgs(SecretRedactionSnapshot snapshot) : EventArgs
{
	public SecretRedactionSnapshot Snapshot { get; } = snapshot;
}

public sealed class SecretRedactionScope
{
	private readonly SecretRedactionSession _session;
	private readonly ISecretDetector _detector;
	private readonly string _projectRoot;
	private readonly IReadOnlySet<string> _keptOccurrenceIds;
	private readonly long _overrideRevision;
	private readonly Dictionary<string, int> _identityIndexes = new(StringComparer.Ordinal);
	private readonly Dictionary<string, int> _ruleIdentityCounts = new(StringComparer.Ordinal);
	private int _detectedCount;
	private int _redactedCount;
	private string? _placeholderExample;
	private bool _completed;

	internal SecretRedactionScope(
		SecretRedactionSession session,
		ISecretDetector detector,
		string projectRoot,
		IReadOnlyList<string> orderedFilePaths,
		SecretRedactionLegendText legendText,
		IReadOnlySet<string> keptOccurrenceIds,
		long overrideRevision)
	{
		_session = session;
		_detector = detector;
		_projectRoot = Path.GetFullPath(projectRoot);
		_keptOccurrenceIds = keptOccurrenceIds;
		_overrideRevision = overrideRevision;
		LegendText = legendText;
		SelectionKey = SecretRedactionSession.BuildSelectionKey(_projectRoot, orderedFilePaths);
	}

	public string SelectionKey { get; }
	public int DetectedCount => _detectedCount;
	public int RedactedCount => _redactedCount;
	public string? PlaceholderExample => _placeholderExample;
	public SecretRedactionLegendText LegendText { get; }

	public SecretTextRedactionResult Redact(
		string filePath,
		string content,
		CancellationToken cancellationToken = default)
	{
		if (_completed)
			throw new InvalidOperationException("The redaction output scope is already complete.");

		var relativePath = SecretRedactionSession.NormalizeRelativePath(_projectRoot, filePath);
		var matches = ResolveNonOverlappingMatches(
			_detector.Detect(relativePath, content, cancellationToken));
		if (matches.Count == 0)
			return new SecretTextRedactionResult(content, [], 0, 0);

		var builder = new StringBuilder(content.Length);
		var spans = new List<SecretPreviewSpan>(matches.Count);
		var sourceOffset = 0;
		var redactedInFile = 0;
		foreach (var match in matches)
		{
			cancellationToken.ThrowIfCancellationRequested();
			builder.Append(content, sourceOffset, match.Start - sourceOffset);

			var secretHash = SecretRedactionSession.HashValue(match.Value);
			var identity = $"{match.RuleId}:{secretHash}";
			if (!_identityIndexes.TryGetValue(identity, out var index))
			{
				index = _ruleIdentityCounts.GetValueOrDefault(match.RuleId) + 1;
				_ruleIdentityCounts[match.RuleId] = index;
				_identityIndexes.Add(identity, index);
			}

			var occurrenceId = SecretRedactionSession.HashValue(
				$"{_projectRoot}\n{relativePath}\n{match.RuleId}\n{secretHash}\n{match.Start}\n{match.Length}");
			var kept = _keptOccurrenceIds.Contains(occurrenceId);
			var replacement = kept
				? match.Value
				: SecretRedactionLegend.CreatePlaceholder(match.RuleId, index);
			var outputStart = builder.Length;
			builder.Append(replacement);
			spans.Add(new SecretPreviewSpan(
				occurrenceId,
				match.RuleId,
				outputStart,
				replacement.Length,
				kept ? SecretPreviewSpanState.KeptAsIs : SecretPreviewSpanState.Redacted));

			_detectedCount++;
			if (!kept)
			{
				_redactedCount++;
				redactedInFile++;
				_placeholderExample ??= replacement;
			}
			sourceOffset = match.Start + match.Length;
		}
		builder.Append(content, sourceOffset, content.Length - sourceOffset);

		return new SecretTextRedactionResult(
			builder.ToString(),
			spans,
			matches.Count,
			redactedInFile);
	}

	public SecretRedactionSnapshot Complete()
	{
		if (_completed)
			throw new InvalidOperationException("The redaction output scope is already complete.");
		_completed = true;
		var snapshot = new SecretRedactionSnapshot(SelectionKey, _detectedCount, _redactedCount);
		_session.Publish(snapshot, _overrideRevision);
		return snapshot;
	}

	private static IReadOnlyList<DetectedSecret> ResolveNonOverlappingMatches(
		IReadOnlyList<DetectedSecret> matches)
	{
		if (matches.Count <= 1)
			return matches;

		// Specific rules win over the generic fallback for the same value. Afterwards,
		// source position and upstream rule order make overlap resolution deterministic.
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
			if (predecessorView.Count > 0 && predecessorView.Max.End > candidate.Start)
				continue;

			var successorView = accepted.GetViewBetween(
				new AcceptedInterval(candidate.Start, candidate.Start, null),
				maximum);
			if (successorView.Count > 0 && successorView.Min.Start < candidateEnd)
				continue;

			accepted.Add(new AcceptedInterval(candidate.Start, candidateEnd, candidate));
		}

		return accepted.Select(static interval => interval.Match!).ToArray();
	}

	private static bool IsGenericRule(string ruleId) =>
		ruleId.Equals("generic-api-key", StringComparison.Ordinal);

	private readonly record struct AcceptedInterval(int Start, int End, DetectedSecret? Match);

	private sealed class AcceptedIntervalStartComparer : IComparer<AcceptedInterval>
	{
		public static AcceptedIntervalStartComparer Instance { get; } = new();

		public int Compare(AcceptedInterval left, AcceptedInterval right) =>
			left.Start.CompareTo(right.Start);
	}
}
