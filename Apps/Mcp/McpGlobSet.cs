namespace DevProjex.Mcp;

internal sealed class McpGlobSet
{
	private const int MaximumPatterns = 256;
	private const int MaximumPatternLength = 512;
	private readonly IReadOnlyList<Regex> _includes;
	private readonly IReadOnlyList<Regex> _excludes;

	private McpGlobSet(IReadOnlyList<Regex> includes, IReadOnlyList<Regex> excludes)
	{
		_includes = includes;
		_excludes = excludes;
	}

	public static McpGlobSet Create(
		IReadOnlyList<string>? includePatterns,
		IReadOnlyList<string>? excludePatterns) =>
		new(Compile(includePatterns, "include_patterns"), Compile(excludePatterns, "exclude_patterns"));

	public bool Includes(string relativePath)
	{
		var normalized = PathUtility.NormalizeSeparators(relativePath);
		return (_includes.Count == 0 || _includes.Any(regex => regex.IsMatch(normalized))) &&
		       !_excludes.Any(regex => regex.IsMatch(normalized));
	}

	private static IReadOnlyList<Regex> Compile(IReadOnlyList<string>? patterns, string parameter)
	{
		if (patterns is null || patterns.Count == 0)
			return [];
		if (patterns.Count > MaximumPatterns)
			throw Invalid(parameter, $"at most {MaximumPatterns} patterns are allowed");

		var result = new List<Regex>(patterns.Count);
		foreach (var pattern in patterns)
		{
			Validate(pattern, parameter);
			result.Add(new Regex(
				ToRegex(pattern),
				RegexOptions.CultureInvariant,
				TimeSpan.FromSeconds(2)));
		}
		return result;
	}

	private static void Validate(string pattern, string parameter)
	{
		if (string.IsNullOrWhiteSpace(pattern))
			throw Invalid(parameter, "patterns must not be empty");
		if (pattern.Length > MaximumPatternLength)
			throw Invalid(parameter, $"patterns must be at most {MaximumPatternLength} characters");
		if (pattern.Contains('\\'))
			throw Invalid(parameter, "use '/' as the path separator");
		if (Path.IsPathFullyQualified(pattern) || pattern.StartsWith('/'))
			throw Invalid(parameter, "patterns must be project-relative");
		if (pattern.Split('/').Any(static segment => segment == ".."))
			throw Invalid(parameter, "'..' path segments are not allowed");
		if (pattern.Contains('\0'))
			throw Invalid(parameter, "NUL characters are not allowed");
	}

	private static string ToRegex(string pattern)
	{
		var builder = new StringBuilder("^");
		for (var index = 0; index < pattern.Length; index++)
		{
			var character = pattern[index];
			if (character == '*')
			{
				var doubleStar = index + 1 < pattern.Length && pattern[index + 1] == '*';
				if (doubleStar)
				{
					index++;
					if (index + 1 < pattern.Length && pattern[index + 1] == '/')
					{
						index++;
						builder.Append("(?:.*/)?");
					}
					else
					{
						builder.Append(".*");
					}
				}
				else
				{
					builder.Append("[^/]*");
				}
			}
			else if (character == '?')
			{
				builder.Append("[^/]");
			}
			else
			{
				builder.Append(Regex.Escape(character.ToString()));
			}
		}
		return builder.Append('$').ToString();
	}

	private static McpToolException Invalid(string parameter, string reason) =>
		new(
			McpErrorCodes.InvalidPattern,
			$"{McpErrorCodes.InvalidPattern}: invalid '{parameter}': {reason}. Use project-relative glob patterns with '/' separators.");
}
