using DevProjex.Application.Compression;

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
	string Hash)
{
	public string Id { get; } = SecretRedactionSession.HashValue(
		$"{NormalizePath(RelativePath)}\n{SourceOffset}\n{Length}\n{Hash}".AsSpan());

	private static string NormalizePath(string path) => path.Replace('\\', '/');
}

internal sealed class MarkedSecretsMatcher
{
	internal const string RuleId = "manual-secret";
	internal const int RuleOrder = int.MinValue;
	private const int CancellationCheckMask = 0xFFF;

	private readonly PersistentHashGroup[] _persistentHashGroups;
	private readonly IReadOnlyDictionary<string, PreparedSessionMark[]> _sessionMarksByPath;
	private int _persistentIndexBuildCount;

	internal int PersistentIndexBuildCount => Volatile.Read(ref _persistentIndexBuildCount);

	public MarkedSecretsMatcher(
		IEnumerable<MarkedSecretProfileEntry> persistentMarks,
		IEnumerable<SessionMarkedSecret> sessionMarks)
	{
		_persistentHashGroups = persistentMarks
			.Where(IsValid)
			.GroupBy(static mark => mark.Length)
			.Select(static group => new PersistentHashGroup(
				group.Key,
				group
					.GroupBy(static mark => mark.H, StringComparer.OrdinalIgnoreCase)
					.Select(static marks => PersistentHash.Create(marks.First().H))
					.ToArray()))
			.OrderBy(static group => group.Length)
			.ToArray();
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
		Match(relativePath, content, null, cancellationToken);

	/// <param name="content">The text being scanned, after every enabled transformation.</param>
	/// <param name="transformMap">Translation from canonical source offsets into this text.</param>
	public IReadOnlyList<DetectedSecret> Match(
		string relativePath,
		ReadOnlySpan<char> content,
		ContentTransformMap? transformMap,
		CancellationToken cancellationToken)
	{
		_sessionMarksByPath.TryGetValue(
			NormalizePath(relativePath),
			out var sessionMarks);
		if (_persistentHashGroups.Length == 0 && sessionMarks is null)
			return [];

		var findings = new List<DetectedSecret>();
		if (_persistentHashGroups.Length > 0)
		{
			var positions = TextPositionIndex.Create(content, cancellationToken);
			Interlocked.Increment(ref _persistentIndexBuildCount);
			MatchPersistent(content, positions, findings, cancellationToken);
		}
		MatchSession(
			sessionMarks,
			content,
			transformMap,
			findings,
			cancellationToken);
		return findings;
	}

	private void MatchPersistent(
		ReadOnlySpan<char> content,
		TextPositionIndex positions,
		ICollection<DetectedSecret> findings,
		CancellationToken cancellationToken)
	{
		if (_persistentHashGroups.Length == 0)
			return;

		Span<byte> candidateHash = stackalloc byte[MarkedSecretValueNormalizer.PersistedHashByteLength];
		var candidateIndex = 0;
		var lineBreakIndex = 0;
		for (var start = 0; start <= content.Length; start++)
		{
			if (!positions.IsBoundary(start))
				continue;

			if ((candidateIndex++ & CancellationCheckMask) == 0)
				cancellationToken.ThrowIfCancellationRequested();
			while (lineBreakIndex < positions.LineBreakPositions.Length &&
			       positions.LineBreakPositions[lineBreakIndex] < start)
			{
				lineBreakIndex++;
			}
			var nextLineBreak = lineBreakIndex < positions.LineBreakPositions.Length
				? positions.LineBreakPositions[lineBreakIndex]
				: int.MaxValue;

			foreach (var group in _persistentHashGroups)
			{
				var end = start + group.Length;
				if (end > content.Length ||
				    !positions.IsBoundary(end) ||
				    nextLineBreak < end)
				{
					continue;
				}

				MarkedSecretValueNormalizer.ComputeHash(
					content.Slice(start, group.Length),
					candidateHash);
				if (!TryResolveHash(group.Hashes, candidateHash, out var persistedHash))
					continue;

				findings.Add(CreateFinding(
					content,
					start,
					group.Length,
					persistedHash,
					SecretFindingSource.PersistentMark));
			}
		}
	}

	private void MatchSession(
		IReadOnlyList<PreparedSessionMark>? marks,
		ReadOnlySpan<char> content,
		ContentTransformMap? transformMap,
		ICollection<DetectedSecret> findings,
		CancellationToken cancellationToken)
	{
		if (marks is null)
			return;

		Span<byte> candidateHash = stackalloc byte[MarkedSecretValueNormalizer.PersistedHashByteLength];
		for (var index = 0; index < marks.Count; index++)
		{
			if ((index & 0xFF) == 0)
				cancellationToken.ThrowIfCancellationRequested();
			var preparedMark = marks[index];
			var mark = preparedMark.Mark;
			if (!TryResolveAnchor(
				    mark,
				    transformMap,
				    out var start))
			{
				continue;
			}

			if (start > content.Length - mark.Length ||
			    !SecretTokenBoundary.IsBoundary(content, start) ||
			    !SecretTokenBoundary.IsBoundary(content, start + mark.Length))
			{
				continue;
			}

			var value = content.Slice(start, mark.Length);
			if (value.IndexOfAny('\r', '\n') >= 0)
				continue;
			MarkedSecretValueNormalizer.ComputeHash(value, candidateHash);
			// The hash is the last gate, not the only one. Translating the anchor puts the mark on
			// the right characters when compression shifts them; verifying the value here means a
			// mark that still cannot be placed is skipped rather than applied to whatever now sits
			// at those coordinates.
			if (!candidateHash.SequenceEqual(preparedMark.HashBytes))
				continue;

			findings.Add(CreateFinding(
				content,
				start,
				mark.Length,
				mark.Hash,
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
		out int start)
	{
		if (transformMap is null or { IsIdentity: true })
		{
			start = mark.SourceOffset;
			return true;
		}

		return transformMap.TryToTransformed(mark.SourceOffset, out start);
	}

	private static DetectedSecret CreateFinding(
		ReadOnlySpan<char> content,
		int start,
		int length,
		string hash,
		SecretFindingSource source,
		string? sessionMarkId = null) =>
		new(
			RuleId,
			start,
			length,
			content.Slice(start, length).ToString(),
			RuleOrder,
			source,
			source == SecretFindingSource.PersistentMark ? hash : null,
			sessionMarkId);

	private static bool TryResolveHash(
		IReadOnlyList<PersistentHash> hashes,
		ReadOnlySpan<byte> candidate,
		out string persistedHash)
	{
		foreach (var hash in hashes)
		{
			if (!candidate.SequenceEqual(hash.Bytes))
				continue;

			persistedHash = hash.Hex;
			return true;
		}

		persistedHash = string.Empty;
		return false;
	}

	private static bool IsValid(MarkedSecretProfileEntry mark) =>
		mark.Length is >= MarkedSecretValueNormalizer.MinimumLength and <= MarkedSecretValueNormalizer.MaximumLength &&
		IsValidHash(mark.H);

	private static bool IsValidHash(string? hash) =>
		hash is not null &&
		hash.Length == MarkedSecretValueNormalizer.PersistedHashLength &&
		hash.All(char.IsAsciiHexDigit);

	private static string NormalizePath(string path) => path.Replace('\\', '/');

	private sealed record PersistentHashGroup(int Length, PersistentHash[] Hashes);

	private sealed record PersistentHash(string Hex, byte[] Bytes)
	{
		public static PersistentHash Create(string hex) =>
			new(hex.ToLowerInvariant(), Convert.FromHexString(hex));
	}

	private sealed record PreparedSessionMark(SessionMarkedSecret Mark, byte[] HashBytes);

	private sealed class TextPositionIndex(
		ulong[] boundaryBits,
		int[] lineBreakPositions)
	{
		public int[] LineBreakPositions { get; } = lineBreakPositions;

		public static TextPositionIndex Create(
			ReadOnlySpan<char> content,
			CancellationToken cancellationToken)
		{
			var boundaryBits = new ulong[((content.Length + 1) + 63) >> 6];
			var lineBreakPositions = new List<int>();

			for (var position = 0; position <= content.Length; position++)
			{
				if ((position & CancellationCheckMask) == 0)
					cancellationToken.ThrowIfCancellationRequested();

				if (SecretTokenBoundary.IsBoundary(content, position))
					boundaryBits[position >> 6] |= 1UL << (position & 63);

				if (position == content.Length)
					continue;

				if (content[position] is '\r' or '\n')
					lineBreakPositions.Add(position);
			}

			return new TextPositionIndex(
				boundaryBits,
				lineBreakPositions.ToArray());
		}

		public bool IsBoundary(int position) =>
			position >= 0 &&
			(uint)(position >> 6) < (uint)boundaryBits.Length &&
			(boundaryBits[position >> 6] & (1UL << (position & 63))) != 0;
	}
}
