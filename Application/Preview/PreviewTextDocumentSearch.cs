namespace DevProjex.Application.Preview;

public readonly record struct PreviewTextSearchMatch(int Line, int Column);

public static class PreviewTextDocumentSearch
{
	public static IReadOnlyList<PreviewTextSearchMatch> FindAll(
		IPreviewTextDocument document,
		string query,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(document);
		if (string.IsNullOrWhiteSpace(query))
			return [];

		var normalizedQuery = query.Trim();
		var matches = new List<PreviewTextSearchMatch>();
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

					matches.Add(new PreviewTextSearchMatch(lineNumber - 1, columnOffset + match));
					var consumed = match + normalizedQuery.Length;
					columnOffset += consumed;
					line = line[consumed..];
				}

				return true;
			},
			cancellationToken);

		return matches;
	}
}
