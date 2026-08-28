namespace DevProjex.Application.Compression;

internal static class ContentPathOrdering
{
	public static List<string> BuildOrderedUnique(
		IEnumerable<string> paths,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(paths);
		cancellationToken.ThrowIfCancellationRequested();
		if (paths is IReadOnlyList<string> orderedPaths &&
		    IsStrictlyOrderedUnique(orderedPaths, cancellationToken))
		{
			return new List<string>(orderedPaths);
		}

		var uniquePaths = new HashSet<string>(PathComparer.Default);
		foreach (var path in paths)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (!string.IsNullOrWhiteSpace(path))
				uniquePaths.Add(path);
		}

		var result = new List<string>(uniquePaths.Count);
		result.AddRange(uniquePaths);
		CancellationAwareSort.Sort(result, PathComparer.Default, cancellationToken);
		return result;
	}

	public static bool IsStrictlyOrderedUnique(
		IReadOnlyList<string> paths,
		CancellationToken cancellationToken)
	{
		string? previousPath = null;
		for (var index = 0; index < paths.Count; index++)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var path = paths[index];
			if (string.IsNullOrWhiteSpace(path) ||
			    previousPath is not null && PathComparer.Default.Compare(previousPath, path) >= 0)
			{
				return false;
			}

			previousPath = path;
		}

		return true;
	}
}
