namespace DevProjex.Application.Compression;

internal static class ContentPathOrdering
{
	public static string[] BuildOrderedUnique(
		IEnumerable<string> paths,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(paths);
		cancellationToken.ThrowIfCancellationRequested();
		if (paths is IReadOnlyList<string> orderedPaths &&
		    IsStrictlyOrderedUnique(orderedPaths, cancellationToken))
		{
			var orderedResult = new string[orderedPaths.Count];
			for (var index = 0; index < orderedResult.Length; index++)
			{
				cancellationToken.ThrowIfCancellationRequested();
				orderedResult[index] = orderedPaths[index];
			}
			return orderedResult;
		}

		var uniquePaths = new HashSet<string>(ProjectTreePathIdentity.CanonicalComparer);
		foreach (var path in paths)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (!string.IsNullOrWhiteSpace(path))
				uniquePaths.Add(path);
		}

		var result = uniquePaths.ToArray();
		CancellationAwareSort.Sort(result, ProjectTreePathIdentity.CanonicalComparer, cancellationToken);
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
			    previousPath is not null && ProjectTreePathIdentity.CanonicalComparer.Compare(previousPath, path) >= 0)
			{
				return false;
			}

			previousPath = path;
		}

		return true;
	}
}
