namespace DevProjex.Application.Secrets;

internal sealed record SessionMarkedSecret(
	string RelativePath,
	int LineIndex,
	int Column,
	int Length,
	string Hash);

internal sealed class MarkedSecretsMatcher
{
	internal const string RuleId = "manual-secret";
	internal const int RuleOrder = int.MinValue;
	private const int CancellationCheckMask = 0xFFF;

	private readonly PersistentHashGroup[] _persistentHashGroups;
	private readonly IReadOnlyDictionary<string, PreparedSessionMark[]> _sessionMarksByPath;

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
		CancellationToken cancellationToken)
	{
		if (_persistentHashGroups.Length == 0 && _sessionMarksByPath.Count == 0)
			return [];

		var positions = TextPositionIndex.Create(content, cancellationToken);
		var findings = new List<DetectedSecret>();
		MatchPersistent(content, positions, findings, cancellationToken);
		MatchSession(relativePath, content, positions, findings);
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
		foreach (var start in positions.BoundaryPositions)
		{
			if ((candidateIndex++ & CancellationCheckMask) == 0)
				cancellationToken.ThrowIfCancellationRequested();

			foreach (var group in _persistentHashGroups)
			{
				var end = start + group.Length;
				if (end > content.Length ||
				    !positions.IsBoundary(end) ||
				    positions.ContainsLineBreak(start, end))
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
		string relativePath,
		ReadOnlySpan<char> content,
		TextPositionIndex positions,
		ICollection<DetectedSecret> findings)
	{
		if (!_sessionMarksByPath.TryGetValue(NormalizePath(relativePath), out var marks))
			return;

		Span<byte> candidateHash = stackalloc byte[MarkedSecretValueNormalizer.PersistedHashByteLength];
		foreach (var preparedMark in marks)
		{
			var mark = preparedMark.Mark;
			var start = positions.ResolveOffset(content, mark.LineIndex, mark.Column);
			if (start < 0 || start > content.Length - mark.Length ||
			    !positions.IsBoundary(start) ||
			    !positions.IsBoundary(start + mark.Length))
			{
				continue;
			}

			var value = content.Slice(start, mark.Length);
			MarkedSecretValueNormalizer.ComputeHash(value, candidateHash);
			if (!candidateHash.SequenceEqual(preparedMark.HashBytes))
				continue;

			findings.Add(CreateFinding(content, start, mark.Length, mark.Hash, SecretFindingSource.SessionMark));
		}
	}

	private static DetectedSecret CreateFinding(
		ReadOnlySpan<char> content,
		int start,
		int length,
		string hash,
		SecretFindingSource source) =>
		new(
			RuleId,
			start,
			length,
			content.Slice(start, length).ToString(),
			RuleOrder,
			source,
			source == SecretFindingSource.PersistentMark ? hash : null);

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
		bool[] boundaries,
		int[] boundaryPositions,
		int[] lineBreakPrefixCounts,
		int[] newlinePositions)
	{
		public IReadOnlyList<int> BoundaryPositions { get; } = boundaryPositions;

		public static TextPositionIndex Create(
			ReadOnlySpan<char> content,
			CancellationToken cancellationToken)
		{
			var boundaries = new bool[content.Length + 1];
			var boundaryPositions = new List<int>();
			var lineBreakPrefixCounts = new int[content.Length + 1];
			var newlinePositions = new List<int>();

			for (var position = 0; position <= content.Length; position++)
			{
				if ((position & CancellationCheckMask) == 0)
					cancellationToken.ThrowIfCancellationRequested();

				if (SecretTokenBoundary.IsBoundary(content, position))
				{
					boundaries[position] = true;
					boundaryPositions.Add(position);
				}

				if (position == content.Length)
					continue;

				var character = content[position];
				lineBreakPrefixCounts[position + 1] =
					lineBreakPrefixCounts[position] + (character is '\r' or '\n' ? 1 : 0);
				if (character == '\n')
					newlinePositions.Add(position);
			}

			return new TextPositionIndex(
				boundaries,
				boundaryPositions.ToArray(),
				lineBreakPrefixCounts,
				newlinePositions.ToArray());
		}

		public bool IsBoundary(int position) =>
			(uint)position < (uint)boundaries.Length && boundaries[position];

		public bool ContainsLineBreak(int start, int end) =>
			lineBreakPrefixCounts[end] != lineBreakPrefixCounts[start];

		public int ResolveOffset(ReadOnlySpan<char> content, int lineIndex, int column)
		{
			if (lineIndex < 0 || column < 0 || lineIndex > newlinePositions.Length)
				return -1;

			var lineStart = lineIndex == 0 ? 0 : newlinePositions[lineIndex - 1] + 1;
			var lineEnd = lineIndex < newlinePositions.Length
				? newlinePositions[lineIndex]
				: content.Length;
			if (lineEnd > lineStart && content[lineEnd - 1] == '\r')
				lineEnd--;

			return column <= lineEnd - lineStart ? lineStart + column : -1;
		}
	}
}
