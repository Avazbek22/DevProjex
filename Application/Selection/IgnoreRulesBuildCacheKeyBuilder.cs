namespace DevProjex.Application.Selection;

public static class IgnoreRulesBuildCacheKeyBuilder
{
	public static string Build(
		string path,
		IReadOnlyCollection<IgnoreOptionId> selectedIgnoreOptions,
		IReadOnlyCollection<string>? selectedRootFolders)
	{
		var normalizedPath = NormalizePathForCache(path);
		var ignoreOptionsKey = BuildIgnoreOptionSelectionKey(selectedIgnoreOptions);
		var rootSelectionKey = BuildRootSelectionKey(selectedRootFolders);
		return SelectionCacheKeyEncoder.Combine(normalizedPath, ignoreOptionsKey, rootSelectionKey);
	}

	private static string NormalizePathForCache(string path)
	{
		string normalized;
		try
		{
			normalized = Path.GetFullPath(path);
		}
		catch
		{
			normalized = path;
		}

		return PathUtility.NormalizeForCacheKey(normalized);
	}

	private static string BuildIgnoreOptionSelectionKey(IReadOnlyCollection<IgnoreOptionId> selectedIgnoreOptions)
	{
		if (selectedIgnoreOptions.Count == 0)
			return "<none>";

		var unique = new HashSet<int>(selectedIgnoreOptions.Count);
		foreach (var option in selectedIgnoreOptions)
		{
			// Content transformations rewrite selected text after traversal. They must not split or
			// invalidate the path-ignore cache because they cannot change the scanned tree.
			if (!ProjectPresentationCatalog.ContentTransformationOptionIds.Contains(option))
				unique.Add((int)option);
		}
		if (unique.Count == 0)
			return "<none>";

		var ordered = new List<int>(unique);
		ordered.Sort();

		var builder = new StringBuilder(capacity: ordered.Count * 3);
		for (var index = 0; index < ordered.Count; index++)
		{
			if (index > 0)
				builder.Append(',');
			builder.Append(ordered[index]);
		}

		return builder.ToString();
	}

	private static string BuildRootSelectionKey(IReadOnlyCollection<string>? selectedRootFolders)
	{
		return SelectionCacheKeyEncoder.EncodeStrings(selectedRootFolders);
	}
}
