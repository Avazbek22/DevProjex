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

	private readonly IReadOnlyDictionary<int, HashSet<string>> _persistentHashesByLength;
	private readonly IReadOnlyDictionary<string, SessionMarkedSecret[]> _sessionMarksByPath;

	public MarkedSecretsMatcher(
		IEnumerable<MarkedSecretProfileEntry> persistentMarks,
		IEnumerable<SessionMarkedSecret> sessionMarks)
	{
		_persistentHashesByLength = persistentMarks
			.Where(IsValid)
			.GroupBy(static mark => mark.Length)
			.ToDictionary(
				static group => group.Key,
				static group => group.Select(static mark => mark.H)
					.ToHashSet(StringComparer.OrdinalIgnoreCase));
		_sessionMarksByPath = sessionMarks
			.GroupBy(static mark => NormalizePath(mark.RelativePath), PathComparer.Default)
			.ToDictionary(
				static group => group.Key,
				static group => group.ToArray(),
				PathComparer.Default);
	}

	public IReadOnlyList<DetectedSecret> Match(
		string relativePath,
		ReadOnlySpan<char> content,
		CancellationToken cancellationToken)
	{
		if (_persistentHashesByLength.Count == 0 && _sessionMarksByPath.Count == 0)
			return [];

		var findings = new List<DetectedSecret>();
		MatchPersistent(content, findings, cancellationToken);
		MatchSession(relativePath, content, findings);
		return findings;
	}

	private void MatchPersistent(
		ReadOnlySpan<char> content,
		ICollection<DetectedSecret> findings,
		CancellationToken cancellationToken)
	{
		foreach (var (length, hashes) in _persistentHashesByLength)
		{
			if (length > content.Length)
				continue;

			for (var start = 0; start <= content.Length - length; start++)
			{
				if ((start & 0xFFF) == 0)
					cancellationToken.ThrowIfCancellationRequested();
				var end = start + length;
				if (!SecretTokenBoundary.HasBoundaries(content, start, length) ||
				    content.Slice(start, length).IndexOfAny('\r', '\n') >= 0)
				{
					continue;
				}

				var hash = MarkedSecretValueNormalizer.ComputeHash(content.Slice(start, length));
				if (!hashes.Contains(hash))
					continue;

				findings.Add(CreateFinding(content, start, length, hash, SecretFindingSource.PersistentMark));
			}
		}
	}

	private void MatchSession(
		string relativePath,
		ReadOnlySpan<char> content,
		ICollection<DetectedSecret> findings)
	{
		if (!_sessionMarksByPath.TryGetValue(NormalizePath(relativePath), out var marks))
			return;

		foreach (var mark in marks)
		{
			var start = ResolveOffset(content, mark.LineIndex, mark.Column);
			if (start < 0 || start > content.Length - mark.Length ||
			    !SecretTokenBoundary.HasBoundaries(content, start, mark.Length))
			{
				continue;
			}

			var value = content.Slice(start, mark.Length);
			if (!MarkedSecretValueNormalizer.ComputeHash(value).Equals(mark.Hash, StringComparison.OrdinalIgnoreCase))
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

	private static int ResolveOffset(ReadOnlySpan<char> content, int lineIndex, int column)
	{
		if (lineIndex < 0 || column < 0)
			return -1;

		var offset = 0;
		for (var currentLine = 0; currentLine < lineIndex; currentLine++)
		{
			var newline = content[offset..].IndexOf('\n');
			if (newline < 0)
				return -1;
			offset += newline + 1;
		}

		var lineEnd = content[offset..].IndexOf('\n');
		lineEnd = lineEnd < 0 ? content.Length : offset + lineEnd;
		if (lineEnd > offset && content[lineEnd - 1] == '\r')
			lineEnd--;
		return column <= lineEnd - offset ? offset + column : -1;
	}

	private static bool IsValid(MarkedSecretProfileEntry mark) =>
		mark.Length is >= MarkedSecretValueNormalizer.MinimumLength and <= MarkedSecretValueNormalizer.MaximumLength &&
		mark.H.Length == MarkedSecretValueNormalizer.PersistedHashLength &&
		mark.H.All(char.IsAsciiHexDigit);

	private static string NormalizePath(string path) => path.Replace('\\', '/');
}
