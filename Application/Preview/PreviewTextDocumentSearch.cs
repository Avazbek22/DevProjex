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
		for (var lineIndex = 0; lineIndex < document.LineCount; lineIndex++)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var line = document.GetLineText(lineIndex + 1);
			var searchStart = 0;
			while (searchStart <= line.Length)
			{
				var match = line.IndexOf(
					normalizedQuery,
					searchStart,
					StringComparison.OrdinalIgnoreCase);
				if (match < 0)
					break;

				matches.Add(new PreviewTextSearchMatch(lineIndex, match));
				searchStart = match + Math.Max(1, normalizedQuery.Length);
			}
		}

		return matches;
	}
}
