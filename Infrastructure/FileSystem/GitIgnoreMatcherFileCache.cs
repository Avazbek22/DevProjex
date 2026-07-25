namespace DevProjex.Infrastructure.FileSystem;

internal static class GitIgnoreMatcherFileCache
{
	// Refreshes revisit the same repository scopes. Keeping parsed matchers avoids repeated
	// compiled-regex cost while a bounded LRU prevents unbounded process growth.
	private const int CacheLimit = 512;
	private static readonly object CacheSync = new();
	private static readonly Dictionary<string, LinkedListNode<CacheEntry>> Cache = new(PathComparer.Default);
	private static readonly LinkedList<CacheEntry> CacheLru = new();

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
				if (Cache.TryGetValue(normalizedPath, out var cachedNode) &&
				    string.Equals(cachedNode.Value.Content, content, StringComparison.Ordinal))
				{
					CacheLru.Remove(cachedNode);
					CacheLru.AddFirst(cachedNode);
					scopedMatcher = cachedNode.Value.Matcher;
					return true;
				}
			}

			var matcher = GitIgnoreMatcher.Build(scopeRootPath, ReadLines(content));
			scopedMatcher = new ScopedGitIgnoreMatcher(Path.GetFullPath(scopeRootPath), matcher);
			lock (CacheSync)
			{
				Remove(normalizedPath);
				var entry = new CacheEntry(normalizedPath, content, scopedMatcher);
				Cache[normalizedPath] = CacheLru.AddFirst(entry);
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
		while (Cache.Count > CacheLimit && CacheLru.Last is { } leastRecentlyUsed)
			Remove(leastRecentlyUsed.Value.Path);
	}

	private static void Remove(string path)
	{
		if (!Cache.Remove(path, out var node))
			return;

		CacheLru.Remove(node);
	}

	private sealed record CacheEntry(
		string Path,
		string Content,
		ScopedGitIgnoreMatcher Matcher);
}
