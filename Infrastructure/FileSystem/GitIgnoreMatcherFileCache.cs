namespace DevProjex.Infrastructure.FileSystem;

internal static class GitIgnoreMatcherFileCache
{
	// Refreshes revisit the same repository scopes. Keeping parsed matchers avoids repeated
	// compiled-regex cost, while the bounded generation queue prevents unbounded process growth.
	private const int CacheLimit = 512;
	private static readonly object CacheSync = new();
	private static readonly Dictionary<string, CacheEntry> Cache = new(PathComparer.Default);
	private static readonly Queue<CacheOrderEntry> CacheOrder = new();
	private static long _generation;

	public static bool TryLoad(
		string scopeRootPath,
		string gitIgnorePath,
		out ScopedGitIgnoreMatcher scopedMatcher)
	{
		scopedMatcher = null!;
		try
		{
			// Exact content comparison handles rewrites that preserve timestamp and length.
			var content = File.ReadAllText(gitIgnorePath);
			var normalizedPath = Path.GetFullPath(gitIgnorePath);
			lock (CacheSync)
			{
				if (Cache.TryGetValue(normalizedPath, out var cached) &&
				    string.Equals(cached.Content, content, StringComparison.Ordinal))
				{
					scopedMatcher = cached.Matcher;
					return true;
				}
			}

			var matcher = GitIgnoreMatcher.Build(scopeRootPath, ReadLines(content));
			scopedMatcher = new ScopedGitIgnoreMatcher(Path.GetFullPath(scopeRootPath), matcher);
			lock (CacheSync)
			{
				var generation = ++_generation;
				Cache[normalizedPath] = new CacheEntry(content, scopedMatcher, generation);
				CacheOrder.Enqueue(new CacheOrderEntry(normalizedPath, generation));
				TrimCache();
			}

			return true;
		}
		catch
		{
			return false;
		}
	}

	private static IEnumerable<string> ReadLines(string content)
	{
		using var reader = new StringReader(content);
		while (reader.ReadLine() is { } line)
			yield return line;
	}

	private static void TrimCache()
	{
		// A path can appear in the FIFO more than once after edits. Generation matching keeps
		// an old queue entry from evicting the current matcher without maintaining a linked LRU.
		while (Cache.Count > CacheLimit && CacheOrder.TryDequeue(out var oldest))
		{
			if (Cache.TryGetValue(oldest.Path, out var cached) && cached.Generation == oldest.Generation)
				Cache.Remove(oldest.Path);
		}
	}

	private sealed record CacheEntry(
		string Content,
		ScopedGitIgnoreMatcher Matcher,
		long Generation);

	private readonly record struct CacheOrderEntry(string Path, long Generation);
}
