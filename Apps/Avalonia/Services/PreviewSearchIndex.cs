namespace DevProjex.Avalonia.Services;

internal readonly record struct PreviewSearchMatch(
	int LineNumber,
	int StartColumn,
	int Length);

internal readonly record struct PreviewSearchResult(
	PreviewSearchMatch[] Matches,
	bool IsCapped);

internal static class PreviewSearchIndex
{
	internal const int MinimumQueryLength = 2;
	internal const int MaximumMatches = 10_000;

	public static bool CanSearch(string? query)
	{
		if (string.IsNullOrWhiteSpace(query) ||
		    query.AsSpan().IndexOfAny('\r', '\n') >= 0)
		{
			return false;
		}

		var runeCount = 0;
		foreach (var _ in query.EnumerateRunes())
		{
			if (++runeCount == MinimumQueryLength)
				return true;
		}

		return false;
	}

	public static PreviewSearchResult Find(
		IPreviewTextDocument document,
		string query,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(document);
		if (!CanSearch(query))
		{
			return new PreviewSearchResult([], IsCapped: false);
		}

		List<PreviewSearchMatch>? matches = null;
		var lastScannedLine = 0;
		foreach (var section in document.Sections)
		{
			var firstContentLine = Math.Max(lastScannedLine + 1, Math.Max(1, section.ContentStartLine));
			var lastContentLine = Math.Min(document.LineCount, section.EndLine);
			for (var lineNumber = firstContentLine; lineNumber <= lastContentLine; lineNumber++)
			{
				cancellationToken.ThrowIfCancellationRequested();
				var line = document.GetLineText(lineNumber);
				var searchStart = 0;
				while (searchStart <= line.Length - query.Length)
				{
					var matchColumn = line.IndexOf(
						query,
						searchStart,
						StringComparison.OrdinalIgnoreCase);
					if (matchColumn < 0)
						break;

					matches ??= new List<PreviewSearchMatch>(64);
					if (matches.Count == MaximumMatches)
						return new PreviewSearchResult(matches.ToArray(), IsCapped: true);

					matches.Add(new PreviewSearchMatch(lineNumber, matchColumn, query.Length));
					searchStart = matchColumn + query.Length;
				}
			}

			lastScannedLine = Math.Max(lastScannedLine, lastContentLine);
		}

		return new PreviewSearchResult(
			matches?.ToArray() ?? [],
			IsCapped: false);
	}
}
