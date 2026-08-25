using System.Security.Cryptography;

namespace DevProjex.Application.Secrets;

/// <summary>
/// A value the user marked by hand during this session, anchored in canonical source coordinates.
/// Every transformed preview maps the click back to this offset before the mark enters the session,
/// so enabling and disabling transformations are symmetric.
/// </summary>
internal sealed record SessionMarkedSecret(
	string RelativePath,
	int SourceOffset,
	int Length,
	string Hash,
	ManualRedactionClass Class = ManualRedactionClass.Secret)
{
	public string Id { get; } = SecretRedactionSession.HashValue(
		$"{NormalizePath(RelativePath)}\n{SourceOffset}\n{Length}\n{Hash}\n{Class}".AsSpan());

	private static string NormalizePath(string path) => PathUtility.NormalizeSeparators(path);
}

internal sealed class MarkedSecretsMatcher
{
	internal const string RuleId = "manual-secret";
	internal const string PrivateDataRuleId = "manual-private-data";
	internal const int RuleOrder = int.MinValue;
	private const int CancellationCheckMask = 0xFFF;

	private readonly PersistentHashGroup[] _persistentHashGroups;
	private readonly IReadOnlyDictionary<string, PreparedPersistentSourceMark[]> _persistentSourceMarksByPath;
	private readonly IReadOnlyDictionary<string, PreparedSessionMark[]> _sessionMarksByPath;
	private readonly IPersistentSecretIdentityProvider? _identityProvider;
	private readonly Action<MarkedSecretProfileEntry, string>? _legacyMigration;
	private int _persistentIndexBuildCount;

	internal int PersistentIndexBuildCount => Volatile.Read(ref _persistentIndexBuildCount);

	public MarkedSecretsMatcher(
		IEnumerable<MarkedSecretProfileEntry> persistentMarks,
		IEnumerable<SessionMarkedSecret> sessionMarks,
		IPersistentSecretIdentityProvider? identityProvider = null,
		Action<MarkedSecretProfileEntry, string>? legacyMigration = null)
	{
		_identityProvider = identityProvider;
		_legacyMigration = legacyMigration;
		var normalizedPersistentMarks = persistentMarks
			.Where(IsValid)
			.Take(SecretInspectionLimits.MaximumPersistentMarksPerProject + 1)
			.ToArray();
		if (normalizedPersistentMarks.Length > SecretInspectionLimits.MaximumPersistentMarksPerProject)
			throw SecretInspectionBudgetExceededException.PersistentMarks();
		var distinctPersistentMarkLengths = normalizedPersistentMarks
			.Where(static mark => mark.RelativePath is null)
			.Select(static mark => mark.Length)
			.Distinct()
			.Take(SecretInspectionLimits.MaximumDistinctPersistentMarkLengths + 1)
			.Count();
		if (distinctPersistentMarkLengths > SecretInspectionLimits.MaximumDistinctPersistentMarkLengths)
			throw SecretInspectionBudgetExceededException.DistinctPersistentMarkLengths();

		_persistentHashGroups = normalizedPersistentMarks
			.Where(static mark => mark.RelativePath is null)
			.GroupBy(static mark => (mark.Length, mark.Class))
			.Select(static group => new PersistentHashGroup(
				group.Key.Length,
				group.Key.Class,
				group
					.GroupBy(
						static mark => new PersistentSecretMarkId(
							mark.H.ToLowerInvariant(),
							mark.Length,
							Class: mark.Class))
					.Select(static marks => PersistentHash.Create(marks.First()))
					.ToArray()))
			.OrderBy(static group => group.Length)
			.ThenBy(static group => group.Class)
			.ToArray();
		if (normalizedPersistentMarks.Any(static mark => PersistentSecretIdentity.IsV2(mark.H)) &&
		    identityProvider is not { IsAvailable: true })
		{
			throw new SecretDetectionException(
				"The installation key required for persistent secret marks is unavailable.");
		}
		_persistentSourceMarksByPath = normalizedPersistentMarks
			.Where(static mark => mark.RelativePath is not null)
			.GroupBy(static mark => NormalizePath(mark.RelativePath!), PathComparer.Default)
			.ToDictionary(
				static group => group.Key,
				static group => group
					.Select(static mark => new PreparedPersistentSourceMark(
						mark,
						PersistentHash.Create(mark)))
					.ToArray(),
				PathComparer.Default);
		_sessionMarksByPath = sessionMarks
			.Where(static mark => IsValidHash(mark.Hash))
			.GroupBy(static mark => NormalizePath(mark.RelativePath), PathComparer.Default)
			.ToDictionary(
				static group => group.Key,
				static group => group
					.Select(static mark => new PreparedSessionMark(
						mark,
						Convert.FromHexString(mark.Hash)))
					.ToArray(),
				PathComparer.Default);
	}

	public IReadOnlyList<DetectedSecret> Match(
		string relativePath,
		ReadOnlySpan<char> content,
		CancellationToken cancellationToken) =>
		Match(relativePath, content, null, new SecretFileInspectionBudget(), cancellationToken);

	public bool RequiresContentInspection(string relativePath) =>
		_persistentHashGroups.Length > 0 ||
		_persistentSourceMarksByPath.ContainsKey(NormalizePath(relativePath)) ||
		_sessionMarksByPath.ContainsKey(NormalizePath(relativePath));

	/// <param name="content">The text being scanned, after every enabled transformation.</param>
	/// <param name="transformMap">Translation from canonical source offsets into this text.</param>
	public IReadOnlyList<DetectedSecret> Match(
		string relativePath,
		ReadOnlySpan<char> content,
		ContentTransformMap? transformMap,
		CancellationToken cancellationToken) =>
		Match(
			relativePath,
			content,
			transformMap,
			new SecretFileInspectionBudget(),
			cancellationToken);

	public IReadOnlyList<DetectedSecret> Match(
		string relativePath,
		ReadOnlySpan<char> content,
		ContentTransformMap? transformMap,
		SecretFileInspectionBudget budget,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(budget);
		budget.Checkpoint(cancellationToken);
		var normalizedPath = NormalizePath(relativePath);
		_persistentSourceMarksByPath.TryGetValue(
			normalizedPath,
			out var persistentSourceMarks);
		_sessionMarksByPath.TryGetValue(
			normalizedPath,
			out var sessionMarks);
		if (_persistentHashGroups.Length == 0 && persistentSourceMarks is null && sessionMarks is null)
			return [];

		var findings = new List<DetectedSecret>();
		if (_persistentHashGroups.Length > 0)
		{
			var positions = TextPositionIndex.Create(content, budget, cancellationToken);
			Interlocked.Increment(ref _persistentIndexBuildCount);
			MatchPersistent(content, positions, findings, budget, cancellationToken);
		}
		MatchPersistentSource(
			persistentSourceMarks,
			content,
			transformMap,
			findings,
			budget,
			cancellationToken);
		MatchSession(
			sessionMarks,
			content,
			transformMap,
			findings,
			budget,
			cancellationToken);
		return findings;
	}

	private void MatchPersistentSource(
		IReadOnlyList<PreparedPersistentSourceMark>? marks,
		ReadOnlySpan<char> content,
		ContentTransformMap? transformMap,
		ICollection<DetectedSecret> findings,
		SecretFileInspectionBudget budget,
		CancellationToken cancellationToken)
	{
		if (marks is null)
			return;

		Span<byte> candidateDigest = stackalloc byte[PersistentSecretIdentity.V2DigestByteLength];
		try
		{
			for (var index = 0; index < marks.Count; index++)
			{
				if ((index & 0xFF) == 0)
				{
					budget.RegisterMatcherWork(
						Math.Min(0x100, marks.Count - index),
						cancellationToken);
				}
				var prepared = marks[index];
				var mark = prepared.Mark;
				if (!TryResolveAnchor(mark.SourceOffset!.Value, transformMap, out var start) ||
				    start > content.Length - mark.Length)
				{
					continue;
				}

				var value = content.Slice(start, mark.Length);
				if (value.IndexOfAny('\r', '\n') >= 0 ||
				    _identityProvider is null ||
				    !_identityProvider.TryComputeDigest(value, candidateDigest) ||
				    !candidateDigest.SequenceEqual(prepared.Hash.Bytes))
				{
					continue;
				}

				budget.RegisterFinding(cancellationToken);
				var markId = new PersistentSecretMarkId(
					mark.H,
					mark.Length,
					mark.RelativePath,
					mark.SourceOffset,
					mark.Class);
				findings.Add(CreateFinding(
					content,
					start,
					mark.Length,
					mark.H,
					mark.Class,
					SecretFindingSource.PersistentMark,
					persistentMarkId: markId));
			}
		}
		finally
		{
			CryptographicOperations.ZeroMemory(candidateDigest);
		}
	}

	private void MatchPersistent(
		ReadOnlySpan<char> content,
		TextPositionIndex positions,
		ICollection<DetectedSecret> findings,
		SecretFileInspectionBudget budget,
		CancellationToken cancellationToken)
	{
		if (_persistentHashGroups.Length == 0)
			return;

		Span<byte> legacyDigest = stackalloc byte[MarkedSecretValueNormalizer.PersistedHashByteLength];
		Span<byte> v2Digest = stackalloc byte[PersistentSecretIdentity.V2DigestByteLength];
		var candidateIndex = 0;
		var nextLineBreak = positions.FindNextLineBreak(0);
		for (var start = 0; start <= content.Length; start++)
		{
			if (!positions.IsBoundary(start))
				continue;

			if ((candidateIndex++ & CancellationCheckMask) == 0)
				budget.Checkpoint(cancellationToken);
			budget.RegisterMatcherWork(_persistentHashGroups.Length, cancellationToken);
			while (nextLineBreak < start)
				nextLineBreak = positions.FindNextLineBreak(nextLineBreak + 1);

			foreach (var group in _persistentHashGroups)
			{
				var end = start + group.Length;
				if (end > content.Length ||
				    !positions.IsBoundary(end) ||
				    nextLineBreak < end)
				{
					continue;
				}

				var candidate = content.Slice(start, group.Length);
				var legacyComputed = false;
				var v2Computed = false;
				MarkedSecretProfileEntry? resolvedMark = null;
				foreach (var hash in group.Hashes)
				{
					var matches = false;
					if (hash.IsV2)
					{
						if (!v2Computed)
						{
							if (_identityProvider is null ||
							    !_identityProvider.TryComputeDigest(candidate, v2Digest))
							{
								throw new SecretDetectionException(
									"The installation key required for persistent secret marks is unavailable.");
							}
							v2Computed = true;
						}
						matches = v2Digest.SequenceEqual(hash.Bytes);
					}
					else
					{
						if (!legacyComputed)
						{
							MarkedSecretValueNormalizer.ComputeHash(candidate, legacyDigest);
							legacyComputed = true;
						}
						matches = legacyDigest.SequenceEqual(hash.Bytes);
					}

					if (!matches)
						continue;
					resolvedMark = hash.Mark;
					break;
				}
				if (resolvedMark is null)
					continue;
				if (PersistentSecretIdentity.IsLegacy(resolvedMark.H) &&
				    PersistentSecretIdentity.TryCreateV2(_identityProvider, candidate, out var migratedIdentity))
				{
					_legacyMigration?.Invoke(resolvedMark, migratedIdentity);
				}

				budget.RegisterFinding(cancellationToken);
				findings.Add(CreateFinding(
					content,
					start,
					group.Length,
					resolvedMark.H,
					resolvedMark.Class,
					SecretFindingSource.PersistentMark,
					persistentMarkId: new PersistentSecretMarkId(
						resolvedMark.H,
						resolvedMark.Length,
						Class: resolvedMark.Class)));
			}
		}
	}

	private void MatchSession(
		IReadOnlyList<PreparedSessionMark>? marks,
		ReadOnlySpan<char> content,
		ContentTransformMap? transformMap,
		ICollection<DetectedSecret> findings,
		SecretFileInspectionBudget budget,
		CancellationToken cancellationToken)
	{
		if (marks is null)
			return;

		Span<byte> candidateHash = stackalloc byte[MarkedSecretValueNormalizer.PersistedHashByteLength];
		for (var index = 0; index < marks.Count; index++)
		{
			if ((index & 0xFF) == 0)
			{
				budget.RegisterMatcherWork(
					Math.Min(0x100, marks.Count - index),
					cancellationToken);
			}
			var preparedMark = marks[index];
			var mark = preparedMark.Mark;
			if (!TryResolveAnchor(
				    mark,
				    transformMap,
				    out var start))
			{
				continue;
			}

			if (start > content.Length - mark.Length)
			{
				continue;
			}

			var value = content.Slice(start, mark.Length);
			if (value.IndexOfAny('\r', '\n') >= 0)
				continue;
			MarkedSecretValueNormalizer.ComputeHash(value, candidateHash);
			// Session marks are exact source ranges, so token boundaries would reject a valid user-
			// selected substring. The translated anchor plus the value hash prevents range drift.
			if (!candidateHash.SequenceEqual(preparedMark.HashBytes))
				continue;

			budget.RegisterFinding(cancellationToken);
			findings.Add(CreateFinding(
				content,
				start,
				mark.Length,
				mark.Hash,
				mark.Class,
				SecretFindingSource.SessionMark,
				mark.Id));
		}
	}

	/// <summary>
	/// Places a mark in the text being scanned.
	///
	/// The source offset is used directly for an identity transform and carried through the current
	/// map otherwise. A value inside a removed body has no counterpart, and the map says so rather
	/// than allowing the mark to drift onto replacement text.
	/// </summary>
	private static bool TryResolveAnchor(
		SessionMarkedSecret mark,
		ContentTransformMap? transformMap,
		out int start) =>
		TryResolveAnchor(mark.SourceOffset, transformMap, out start);

	private static bool TryResolveAnchor(
		int sourceOffset,
		ContentTransformMap? transformMap,
		out int start)
	{
		if (transformMap is null or { IsIdentity: true })
		{
			start = sourceOffset;
			return true;
		}

		return transformMap.TryToTransformed(sourceOffset, out start);
	}

	private static DetectedSecret CreateFinding(
		ReadOnlySpan<char> content,
		int start,
		int length,
		string hash,
		ManualRedactionClass classification,
		SecretFindingSource source,
		string? sessionMarkId = null,
		PersistentSecretMarkId? persistentMarkId = null) =>
		new(
			classification == ManualRedactionClass.Secret ? RuleId : PrivateDataRuleId,
			start,
			length,
			content.Slice(start, length).ToString(),
			RuleOrder,
			source,
			source == SecretFindingSource.PersistentMark ? hash : null,
			sessionMarkId,
			persistentMarkId,
			classification == ManualRedactionClass.Secret
				? RedactionFindingCategory.Secrets
				: RedactionFindingCategory.PrivateData);

	private static bool IsValid(MarkedSecretProfileEntry mark) =>
		Enum.IsDefined(mark.Class) &&
		mark.Length is >= MarkedSecretValueNormalizer.MinimumLength and <= MarkedSecretValueNormalizer.MaximumLength &&
		PersistentSecretIdentity.IsSupported(mark.H) &&
		(mark.RelativePath is null && mark.SourceOffset is null ||
		 PersistentSecretIdentity.IsV2(mark.H) &&
		 !string.IsNullOrEmpty(mark.RelativePath) &&
		 mark.SourceOffset is >= 0);

	private static bool IsValidHash(string? hash) =>
		hash is not null &&
		hash.Length == MarkedSecretValueNormalizer.PersistedHashLength &&
		hash.All(char.IsAsciiHexDigit);

	private static string NormalizePath(string path) => PathUtility.NormalizeSeparators(path);

	private sealed record PersistentHashGroup(
		int Length,
		ManualRedactionClass Class,
		PersistentHash[] Hashes);

	private sealed record PersistentHash(
		MarkedSecretProfileEntry Mark,
		byte[] Bytes,
		bool IsV2)
	{
		public static PersistentHash Create(MarkedSecretProfileEntry mark)
		{
			var digestLength = PersistentSecretIdentity.IsV2(mark.H)
				? PersistentSecretIdentity.V2DigestByteLength
				: MarkedSecretValueNormalizer.PersistedHashByteLength;
			var bytes = new byte[digestLength];
			if (!PersistentSecretIdentity.TryDecodeDigest(mark.H, bytes))
				throw new SecretDetectionException("A persistent secret identity is invalid.");
			return new PersistentHash(mark, bytes, PersistentSecretIdentity.IsV2(mark.H));
		}
	}

	private sealed record PreparedSessionMark(SessionMarkedSecret Mark, byte[] HashBytes);

	private sealed record PreparedPersistentSourceMark(
		MarkedSecretProfileEntry Mark,
		PersistentHash Hash);

	private sealed class TextPositionIndex(
		ulong[] boundaryBits,
		ulong[] lineBreakBits)
	{
		public static TextPositionIndex Create(
			ReadOnlySpan<char> content,
			SecretFileInspectionBudget budget,
			CancellationToken cancellationToken)
		{
			var bitCount = ((content.Length + 1) + 63) >> 6;
			var boundaryBits = new ulong[bitCount];
			var lineBreakBits = new ulong[bitCount];

			for (var position = 0; position <= content.Length; position++)
			{
				if ((position & CancellationCheckMask) == 0)
					budget.Checkpoint(cancellationToken);

				if (SecretTokenBoundary.IsBoundary(content, position))
					boundaryBits[position >> 6] |= 1UL << (position & 63);

				if (position == content.Length)
					continue;

				if (content[position] is '\r' or '\n')
					lineBreakBits[position >> 6] |= 1UL << (position & 63);
			}

			return new TextPositionIndex(
				boundaryBits,
				lineBreakBits);
		}

		public bool IsBoundary(int position) =>
			position >= 0 &&
			(uint)(position >> 6) < (uint)boundaryBits.Length &&
			(boundaryBits[position >> 6] & (1UL << (position & 63))) != 0;

		public int FindNextLineBreak(int start)
		{
			if (start < 0 || (uint)(start >> 6) >= (uint)lineBreakBits.Length)
				return int.MaxValue;
			var wordIndex = start >> 6;
			var word = lineBreakBits[wordIndex] & (ulong.MaxValue << (start & 63));
			while (word == 0)
			{
				wordIndex++;
				if (wordIndex >= lineBreakBits.Length)
					return int.MaxValue;
				word = lineBreakBits[wordIndex];
			}
			return checked((wordIndex << 6) + System.Numerics.BitOperations.TrailingZeroCount(word));
		}
	}
}
