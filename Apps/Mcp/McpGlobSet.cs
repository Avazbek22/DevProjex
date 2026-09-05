namespace DevProjex.Mcp;

internal sealed class McpGlobSet
{
	private const int MaximumPatterns = 256;
	private const int MaximumPatternLength = 512;
	// One brace group per file class is the realistic shape ("**/*.{ts,tsx}"); the caps keep a
	// hostile nested group from compiling thousands of automata per call.
	internal const int MaximumBraceAlternatives = 64;
	internal const int MaximumExpandedPatterns = 1024;
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

	public bool IncludesDirectory(string relativePath)
	{
		var normalized = PathUtility.NormalizeSeparators(relativePath).TrimEnd('/');
		var subtreeBoundary = normalized + "/";
		return (_includes.Count == 0 || MatchesPathOrSubtreeBoundary(_includes, normalized, subtreeBoundary)) &&
		       !MatchesPathOrSubtreeBoundary(_excludes, normalized, subtreeBoundary);
	}

	private static bool MatchesPathOrSubtreeBoundary(
		IReadOnlyList<Regex> patterns,
		string path,
		string subtreeBoundary) =>
		patterns.Any(regex => regex.IsMatch(path) || regex.IsMatch(subtreeBoundary));

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
			foreach (var expanded in ExpandBraces(pattern, parameter))
			{
				if (result.Count == MaximumExpandedPatterns)
					throw Invalid(parameter, $"at most {MaximumExpandedPatterns} patterns are allowed after brace expansion");
				result.Add(new Regex(
					ToRegex(expanded),
					RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
					TimeSpan.FromSeconds(2)));
			}
		}
		return result;
	}

	private static void Validate(string pattern, string parameter)
	{
		if (string.IsNullOrWhiteSpace(pattern))
			throw Invalid(parameter, "patterns must not be empty");
		if (McpUnicodeLength.ExceedsScalarValueCount(pattern, MaximumPatternLength))
			throw Invalid(parameter, $"patterns must be at most {MaximumPatternLength} characters");
		if (pattern.Contains('\\'))
			throw Invalid(parameter, "use '/' as the path separator");
		if (Path.IsPathFullyQualified(pattern) || pattern.StartsWith('/'))
			throw Invalid(parameter, "patterns must be project-relative");
		if (pattern.Split('/').Any(static segment => segment == ".."))
			throw Invalid(parameter, "'..' path segments are not allowed");
		if (pattern.Contains('\0'))
			throw Invalid(parameter, "NUL characters are not allowed");
		// Syntax this matcher does not implement is refused, never matched literally: a
		// silently empty result reads to an agent as "the project has no such files".
		if (pattern.StartsWith('!'))
			throw Invalid(parameter, "negation ('!') is not supported; list the pattern in exclude_patterns instead");
		if (pattern.Contains('[') || pattern.Contains(']'))
			throw Invalid(parameter, "character classes ('[...]') are not supported; use '?' or several patterns");
	}

	/// <summary>
	/// Expands every <c>{a,b}</c> group into its alternatives, nested groups included, so
	/// <c>**/*.{ts,tsx}</c> means the two patterns an agent expects it to mean.
	/// </summary>
	internal static IReadOnlyList<string> ExpandBraces(string pattern, string parameter)
	{
		var open = pattern.IndexOf('{');
		if (open < 0)
		{
			if (pattern.Contains('}'))
				throw Invalid(parameter, "unbalanced '}' in a brace group");
			return [pattern];
		}

		var close = FindClosingBrace(pattern, open) ??
		            throw Invalid(parameter, "unbalanced '{' in a brace group");
		var prefix = pattern[..open];
		var suffix = pattern[(close + 1)..];
		var alternatives = SplitTopLevel(pattern[(open + 1)..close]);
		var expanded = new List<string>();
		foreach (var alternative in alternatives)
		{
			foreach (var tail in ExpandBraces(alternative + suffix, parameter))
			{
				if (expanded.Count == MaximumBraceAlternatives)
					throw Invalid(parameter, $"a pattern expands to at most {MaximumBraceAlternatives} brace alternatives");
				expanded.Add(prefix + tail);
			}
		}
		return expanded;
	}

	private static int? FindClosingBrace(string pattern, int open)
	{
		var depth = 0;
		for (var index = open; index < pattern.Length; index++)
		{
			switch (pattern[index])
			{
				case '{':
					depth++;
					break;
				case '}':
					depth--;
					if (depth == 0)
						return index;
					break;
			}
		}
		return null;
	}

	private static List<string> SplitTopLevel(string group)
	{
		var alternatives = new List<string>();
		var depth = 0;
		var start = 0;
		for (var index = 0; index < group.Length; index++)
		{
			switch (group[index])
			{
				case '{':
					depth++;
					break;
				case '}':
					depth--;
					break;
				case ',' when depth == 0:
					alternatives.Add(group[start..index]);
					start = index + 1;
					break;
			}
		}
		alternatives.Add(group[start..]);
		return alternatives;
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
				builder.Append("(?:[^/\\uD800-\\uDFFF]|[\\uD800-\\uDBFF][\\uDC00-\\uDFFF])");
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
			$"{McpErrorCodes.InvalidPattern}: invalid '{parameter}': {reason}. " +
			"Patterns are project-relative globs with '/' separators: '*' and '?' stay inside one path segment, " +
			"'**/' spans any depth, '{a,b}' lists alternatives, and matching is case-sensitive.");
}
