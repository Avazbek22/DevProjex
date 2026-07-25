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
		return $"{normalizedPath}|{ignoreOptionsKey}|{rootSelectionKey}";
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
			unique.Add((int)option);

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
		if (selectedRootFolders is null)
			return "<null>";
		if (selectedRootFolders.Count == 0)
			return "<empty>";

		var unique = new HashSet<string>(PathComparer.Default);
		foreach (var root in selectedRootFolders)
		{
			if (string.IsNullOrWhiteSpace(root))
				continue;

			var normalizedRoot = root.Trim();
			if (OperatingSystem.IsWindows())
				normalizedRoot = normalizedRoot.ToUpperInvariant();

			unique.Add(normalizedRoot);
		}

		if (unique.Count == 0)
			return "<empty>";

		var ordered = unique.ToList();
		ordered.Sort(PathComparer.Default);

		var estimatedLength = ordered.Count * 8;
		foreach (var entry in ordered)
			estimatedLength += entry.Length;

		var builder = new StringBuilder(estimatedLength);
		for (var index = 0; index < ordered.Count; index++)
		{
			if (index > 0)
				builder.Append('|');
			builder.Append(ordered[index]);
		}

		return builder.ToString();
	}
}
