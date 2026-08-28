namespace DevProjex.Application.Preview;

public readonly record struct PreviewTextSearchMatch(int Line, int Column);

public readonly record struct PreviewTextSearchResult(
	IReadOnlyList<PreviewTextSearchMatch> Matches,
	bool IsCapped);

public static class PreviewTextDocumentSearch
{
	public const int MinimumQueryLength = 2;
	public const int MaximumMatches = 10_000;

	public static bool CanSearch(string? query)
	{
		if (string.IsNullOrWhiteSpace(query) || query.AsSpan().IndexOfAny('\r', '\n') >= 0)
			return false;

		var normalizedQuery = query.Trim();
		var runeCount = 0;
		foreach (var _ in normalizedQuery.EnumerateRunes())
		{
			if (++runeCount == MinimumQueryLength)
				return true;
		}
		return false;
	}

	public static IReadOnlyList<PreviewTextSearchMatch> FindAll(
		IPreviewTextDocument document,
		string query,
		CancellationToken cancellationToken = default) =>
		Find(document, query, cancellationToken).Matches;

	public static PreviewTextSearchResult Find(
		IPreviewTextDocument document,
		string query,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(document);
		if (!CanSearch(query))
			return new PreviewTextSearchResult([], IsCapped: false);

		var normalizedQuery = query.Trim();
		var matches = new List<PreviewTextSearchMatch>();
		var capped = false;
		document.VisitLines(
			1,
			document.LineCount,
			(lineNumber, line) =>
			{
				var columnOffset = 0;
				while (line.Length >= normalizedQuery.Length)
				{
					var match = line.IndexOf(
						normalizedQuery.AsSpan(),
						StringComparison.OrdinalIgnoreCase);
					if (match < 0)
						break;

					if (matches.Count == MaximumMatches)
					{
						capped = true;
						return false;
					}
					matches.Add(new PreviewTextSearchMatch(lineNumber - 1, columnOffset + match));
					var consumed = match + normalizedQuery.Length;
					columnOffset += consumed;
					line = line[consumed..];
				}

				return true;
			},
			cancellationToken);

		return new PreviewTextSearchResult(matches, capped);
	}
}
