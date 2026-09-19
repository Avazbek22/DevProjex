namespace DevProjex.Mcp;

internal sealed class McpGlobSet
{
	private const int MaximumPatterns = 256;
	// One brace group per file class is the realistic shape ("**/*.{ts,tsx}"); the caps keep a
	// hostile nested group from compiling thousands of automata per call.
	internal const int MaximumBraceAlternatives = ProjectRelativeGlob.MaximumBraceAlternatives;
	internal const int MaximumExpandedPatterns = 1024;
	private const int MaximumCachedPatternSets = 64;
	private static readonly object CacheSync = new();
	private static readonly Dictionary<string, Lazy<McpGlobSet>> Cache = new(StringComparer.Ordinal);
	private static readonly Queue<string> CacheOrder = new();
	private static int _compiledRegexCount;
	private readonly IReadOnlyList<Regex> _includes;
	private readonly IReadOnlyList<Regex> _excludes;

	private McpGlobSet(IReadOnlyList<Regex> includes, IReadOnlyList<Regex> excludes)
	{
		_includes = includes;
		_excludes = excludes;
	}

	internal static int CompiledRegexCount => Volatile.Read(ref _compiledRegexCount);

	public static McpGlobSet Create(
		IReadOnlyList<string>? includePatterns,
		IReadOnlyList<string>? excludePatterns)
	{
		var includes = ValidateAndExpand(includePatterns, "include_patterns");
		var excludes = ValidateAndExpand(excludePatterns, "exclude_patterns");
		var key = CacheKey(includes, excludes);
		Lazy<McpGlobSet> entry;
		lock (CacheSync)
		{
			if (!Cache.TryGetValue(key, out entry!))
			{
				entry = new Lazy<McpGlobSet>(
					() => new McpGlobSet(Compile(includes), Compile(excludes)),
					LazyThreadSafetyMode.ExecutionAndPublication);
				Cache.Add(key, entry);
				CacheOrder.Enqueue(key);
				while (CacheOrder.Count > MaximumCachedPatternSets)
					Cache.Remove(CacheOrder.Dequeue());
			}
		}
		return entry.Value;
	}

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

	private static IReadOnlyList<string> ValidateAndExpand(IReadOnlyList<string>? patterns, string parameter)
	{
		if (patterns is null || patterns.Count == 0)
			return [];
		if (patterns.Count > MaximumPatterns)
			throw Invalid(parameter, $"at most {MaximumPatterns} patterns are allowed");

		var result = new List<string>(patterns.Count);
		foreach (var pattern in patterns)
		{
			Validate(pattern, parameter);
			foreach (var expanded in ExpandBraces(pattern, parameter))
			{
				if (result.Count == MaximumExpandedPatterns)
					throw Invalid(parameter, $"at most {MaximumExpandedPatterns} patterns are allowed after brace expansion");
				result.Add(expanded);
			}
		}
		return result;
	}

	private static IReadOnlyList<Regex> Compile(IReadOnlyList<string> patterns)
	{
		if (patterns.Count == 0)
			return [];
		var result = new Regex[patterns.Count];
		for (var index = 0; index < patterns.Count; index++)
		{
			result[index] = ProjectRelativeGlob.Compile(patterns[index]);
			Interlocked.Increment(ref _compiledRegexCount);
		}
		return result;
	}

	private static string CacheKey(IReadOnlyList<string> includes, IReadOnlyList<string> excludes) =>
		$"{includes.Count}:I\0{string.Join('\0', includes)}\0{excludes.Count}:E\0{string.Join('\0', excludes)}";

	private static void Validate(string pattern, string parameter)
	{
		try
		{
			ProjectRelativeGlob.Validate(pattern);
		}
		catch (ProjectRelativeGlobException failure)
		{
			throw Invalid(parameter, failure.Reason);
		}
	}

	/// <summary>
	/// Expands every <c>{a,b}</c> group into its alternatives, nested groups included, so
	/// <c>**/*.{ts,tsx}</c> means the two patterns an agent expects it to mean.
	/// </summary>
	internal static IReadOnlyList<string> ExpandBraces(string pattern, string parameter)
	{
		try
		{
			return ProjectRelativeGlob.ExpandBraces(pattern);
		}
		catch (ProjectRelativeGlobException failure)
		{
			throw Invalid(parameter, failure.Reason);
		}
	}

	private static McpToolException Invalid(string parameter, string reason) =>
		new(
			McpErrorCodes.InvalidPattern,
			$"{McpErrorCodes.InvalidPattern}: invalid '{parameter}': {reason}. " +
			"Patterns are project-relative globs with '/' separators: '*' and '?' stay inside one path segment, " +
			"'**/' spans any depth, '{a,b}' lists alternatives, and matching is case-sensitive.");
}
