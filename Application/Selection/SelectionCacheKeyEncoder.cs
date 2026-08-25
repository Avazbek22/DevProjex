namespace DevProjex.Application.Selection;

internal static class SelectionCacheKeyEncoder
{
	private const char NullCollectionTag = 'N';
	private const char ValueCollectionTag = 'V';

	public static string EncodeStrings(IReadOnlyCollection<string>? values)
	{
		if (values is null)
			return NullCollectionTag.ToString();

		var unique = new HashSet<string>(PathComparer.Default);
		foreach (var value in values)
		{
			// Empty tokens are not selections. Every non-empty name remains exact because
			// whitespace-only names, pipes, and sentinel-looking text are legal on POSIX.
			if (!string.IsNullOrEmpty(value))
				unique.Add(NormalizeForPlatform(value));
		}

		var ordered = unique.ToList();
		ordered.Sort(PathComparer.Default);

		var builder = new StringBuilder();
		builder.Append(ValueCollectionTag).Append(ordered.Count).Append(':');
		foreach (var value in ordered)
			AppendLengthPrefixed(builder, value);

		return builder.ToString();
	}

	public static string Combine(params string[] components)
	{
		var builder = new StringBuilder();
		foreach (var component in components)
			AppendLengthPrefixed(builder, component);
		return builder.ToString();
	}

	private static void AppendLengthPrefixed(StringBuilder builder, string value) =>
		builder.Append(value.Length).Append(':').Append(value);

	private static string NormalizeForPlatform(string value) =>
		OperatingSystem.IsWindows() ? value.ToUpperInvariant() : value;
}
