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
	internal const int MinimumQueryLength = PreviewTextDocumentSearch.MinimumQueryLength;
	internal const int MaximumMatches = PreviewTextDocumentSearch.MaximumMatches;

	public static bool CanSearch(string? query)
	{
		return PreviewTextDocumentSearch.CanSearch(query);
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
			var capped = false;
			document.VisitLines(
				firstContentLine,
				lastContentLine,
				(lineNumber, line) =>
				{
					var columnOffset = 0;
					while (line.Length >= query.Length)
					{
						var matchColumn = line.IndexOf(
							query.AsSpan(),
							StringComparison.OrdinalIgnoreCase);
						if (matchColumn < 0)
							break;

						matches ??= new List<PreviewSearchMatch>(64);
						if (matches.Count == MaximumMatches)
						{
							capped = true;
							return false;
						}

						matches.Add(new PreviewSearchMatch(lineNumber, columnOffset + matchColumn, query.Length));
						var consumed = matchColumn + query.Length;
						columnOffset += consumed;
						line = line[consumed..];
					}

					return true;
				},
				cancellationToken);
			if (capped)
				return new PreviewSearchResult(matches!.ToArray(), IsCapped: true);

			lastScannedLine = Math.Max(lastScannedLine, lastContentLine);
		}

		return new PreviewSearchResult(
			matches?.ToArray() ?? [],
			IsCapped: false);
	}
}
