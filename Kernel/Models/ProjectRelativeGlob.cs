using System.Text;
using System.Text.RegularExpressions;

namespace DevProjex.Kernel.Models;

/// <summary>
/// Thrown when a project-relative glob uses syntax this matcher does not implement. The reason is
/// the exact caller-facing sentence, so every surface reports the same wording for the same mistake
/// while keeping its own error code.
/// </summary>
public sealed class ProjectRelativeGlobException(string reason)
	: Exception($"invalid glob: {reason}")
{
	public string Reason { get; } = reason;
}

/// <summary>
/// The one project-relative glob syntax in the product: '*' and '?' stay inside a path segment,
/// '**/' spans any depth, '{a,b}' lists alternatives, matching is case-sensitive on every platform.
///
/// Syntax the matcher does not implement is refused rather than matched literally: a silently empty
/// result reads to a caller as "the project has no such files".
/// </summary>
public static class ProjectRelativeGlob
{
	public const int MaximumPatternLength = 512;

	// One brace group per file class is the realistic shape ("**/*.{ts,tsx}"); the caps keep a
	// hostile nested group from compiling thousands of automata per call.
	public const int MaximumBraceAlternatives = 64;

	private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(2);

	public static void Validate(string pattern)
	{
		ArgumentNullException.ThrowIfNull(pattern);
		if (string.IsNullOrWhiteSpace(pattern))
			throw new ProjectRelativeGlobException("patterns must not be empty");
		if (ExceedsScalarValueCount(pattern, MaximumPatternLength))
			throw new ProjectRelativeGlobException($"patterns must be at most {MaximumPatternLength} characters");
		if (pattern.Contains('\\'))
			throw new ProjectRelativeGlobException("use '/' as the path separator");
		if (Path.IsPathFullyQualified(pattern) || pattern.StartsWith('/'))
			throw new ProjectRelativeGlobException("patterns must be project-relative");
		if (pattern.Split('/').Any(static segment => segment == ".."))
			throw new ProjectRelativeGlobException("'..' path segments are not allowed");
		if (pattern.Contains('\0'))
			throw new ProjectRelativeGlobException("NUL characters are not allowed");
		if (pattern.StartsWith('!'))
		{
			throw new ProjectRelativeGlobException(
				"negation ('!') is not supported; list the pattern in exclude_patterns instead");
		}
		if (pattern.Contains('[') || pattern.Contains(']'))
		{
			throw new ProjectRelativeGlobException(
				"character classes ('[...]') are not supported; use '?' or several patterns");
		}
	}

	/// <summary>
	/// Expands every <c>{a,b}</c> group into its alternatives, nested groups included, so
	/// <c>**/*.{ts,tsx}</c> means the two patterns a caller expects it to mean.
	/// </summary>
	public static IReadOnlyList<string> ExpandBraces(string pattern)
	{
		ArgumentNullException.ThrowIfNull(pattern);
		var open = pattern.IndexOf('{');
		if (open < 0)
		{
			if (pattern.Contains('}'))
				throw new ProjectRelativeGlobException("unbalanced '}' in a brace group");
			return [pattern];
		}

		var close = FindClosingBrace(pattern, open) ??
		            throw new ProjectRelativeGlobException("unbalanced '{' in a brace group");
		var prefix = pattern[..open];
		var suffix = pattern[(close + 1)..];
		var alternatives = SplitTopLevel(pattern[(open + 1)..close]);
		var expanded = new List<string>();
		foreach (var alternative in alternatives)
		{
			foreach (var tail in ExpandBraces(alternative + suffix))
			{
				if (expanded.Count == MaximumBraceAlternatives)
				{
					throw new ProjectRelativeGlobException(
						$"a pattern expands to at most {MaximumBraceAlternatives} brace alternatives");
				}
				expanded.Add(prefix + tail);
			}
		}
		return expanded;
	}

	public static Regex Compile(string expandedPattern)
	{
		ArgumentNullException.ThrowIfNull(expandedPattern);
		return new Regex(
			ToRegexPattern(expandedPattern),
			RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
			MatchTimeout);
	}

	public static string ToRegexPattern(string expandedPattern)
	{
		ArgumentNullException.ThrowIfNull(expandedPattern);
		var builder = new StringBuilder("^");
		for (var index = 0; index < expandedPattern.Length; index++)
		{
			var character = expandedPattern[index];
			if (character == '*')
			{
				var doubleStar = index + 1 < expandedPattern.Length && expandedPattern[index + 1] == '*';
				if (doubleStar)
				{
					index++;
					if (index + 1 < expandedPattern.Length && expandedPattern[index + 1] == '/')
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

	private static bool ExceedsScalarValueCount(string value, int maximum)
	{
		if (value.Length <= maximum)
			return false;

		var count = 0;
		foreach (var _ in value.EnumerateRunes())
		{
			if (++count > maximum)
				return true;
		}

		return false;
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
}
