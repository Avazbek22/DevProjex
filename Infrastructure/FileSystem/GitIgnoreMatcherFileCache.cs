using DevProjex.Application.Services;
using DevProjex.Infrastructure.Git;

namespace DevProjex.Infrastructure.FileSystem;

internal static class GitIgnoreMatcherFileCache
{
	// Refreshes revisit the same repository scopes. Keeping parsed matchers avoids repeated
	// compiled-regex cost while a bounded LRU prevents unbounded process growth.
	private const int CacheLimit = 512;
	internal const long MaximumRetainedSourceBytes = 16L * 1024 * 1024;
	private static readonly object CacheSync = new();
	private static readonly Dictionary<string, LinkedListNode<CacheEntry>> Cache = new(PathComparer.Default);
	private static readonly LinkedList<CacheEntry> CacheLru = new();
	private static long _retainedSourceBytes;

	public static GitIgnoreMatcherLoadResult Load(
		string scopeRootPath,
		string gitIgnorePath,
		IGitPathComparisonSemanticsResolver? comparisonSemanticsResolver = null)
		=> Load(
			scopeRootPath,
			gitIgnorePath,
			static (path, _) => GitIgnoreFileReader.Read(path),
			CancellationToken.None,
			comparisonSemanticsResolver);

	internal static GitIgnoreMatcherLoadResult LoadWithCancellation(
		string scopeRootPath,
		string gitIgnorePath,
		CancellationToken cancellationToken,
		IGitPathComparisonSemanticsResolver? comparisonSemanticsResolver = null)
		=> Load(
			scopeRootPath,
			gitIgnorePath,
			static (path, token) => GitIgnoreFileReader.ReadWithCancellation(path, token),
			cancellationToken,
			comparisonSemanticsResolver);

	internal static GitIgnoreMatcherLoadResult Load(
		string scopeRootPath,
		string gitIgnorePath,
		Func<string, GitIgnoreFileContent> sourceReader,
		IGitPathComparisonSemanticsResolver? comparisonSemanticsResolver = null)
		=> Load(
			scopeRootPath,
			gitIgnorePath,
			(path, _) => sourceReader(path),
			CancellationToken.None,
			comparisonSemanticsResolver);

	private static GitIgnoreMatcherLoadResult Load(
		string scopeRootPath,
		string gitIgnorePath,
		Func<string, CancellationToken, GitIgnoreFileContent> sourceReader,
		CancellationToken cancellationToken,
		IGitPathComparisonSemanticsResolver? comparisonSemanticsResolver)
	{
		cancellationToken.ThrowIfCancellationRequested();
		try
		{
			var initialProbe = ProbeSource(gitIgnorePath);
			if (initialProbe.Status == SourceProbeStatus.NotFound)
				return GitIgnoreMatcherLoadResult.NotFound;
			if (initialProbe.Status == SourceProbeStatus.SymbolicLink)
				return GitIgnoreMatcherLoadResult.SymbolicLinkSkipped;
			if (initialProbe.Status != SourceProbeStatus.RegularFile)
				return GitIgnoreMatcherLoadResult.ReadFailure;

			var comparisonSemantics = (comparisonSemanticsResolver ??
			                           GitConfigPathComparisonSemanticsResolver.Instance)
				.Resolve(scopeRootPath);
			if (!comparisonSemantics.IsAuthoritative)
				return GitIgnoreMatcherLoadResult.ReadFailure;

			// Exact content comparison handles rewrites that preserve timestamp and length.
			var source = sourceReader(gitIgnorePath, cancellationToken);
			cancellationToken.ThrowIfCancellationRequested();
			var finalProbe = ProbeSource(gitIgnorePath);
			if (finalProbe.Status != SourceProbeStatus.RegularFile ||
			    !finalProbe.Stamp.Equals(initialProbe.Stamp) ||
			    source.LengthBytes != initialProbe.Stamp.LengthBytes)
			{
				return GitIgnoreMatcherLoadResult.ReadFailure;
			}

			var normalizedPath = Path.GetFullPath(gitIgnorePath);
			lock (CacheSync)
			{
				if (Cache.TryGetValue(normalizedPath, out var cachedNode) &&
				    cachedNode.Value.SourceLengthBytes == source.LengthBytes &&
				    string.Equals(
					    cachedNode.Value.ContentFingerprint,
					    source.ContentFingerprint,
					    StringComparison.Ordinal) &&
				    cachedNode.Value.ComparisonSemantics.Equals(comparisonSemantics))
				{
					CacheLru.Remove(cachedNode);
					CacheLru.AddFirst(cachedNode);
					return GitIgnoreMatcherLoadResult.Loaded(cachedNode.Value.Matcher);
				}
			}

			var matcher = GitIgnoreMatcher.Build(
				scopeRootPath,
				GitIgnoreFileReader.EnumerateLinesWithCancellation(source.Content, cancellationToken),
				comparisonSemantics);
			cancellationToken.ThrowIfCancellationRequested();
			var scopedMatcher = new ScopedGitIgnoreMatcher(Path.GetFullPath(scopeRootPath), matcher);
			lock (CacheSync)
			{
				Remove(normalizedPath);
				// Source length is a stable proxy for the parsed matcher's footprint. A single
				// pathological source is still usable, but is never retained by the process cache.
				if (source.LengthBytes <= MaximumRetainedSourceBytes)
				{
					var entry = new CacheEntry(
						normalizedPath,
						source.LengthBytes,
						source.ContentFingerprint,
						comparisonSemantics,
						scopedMatcher);
					Cache[normalizedPath] = CacheLru.AddFirst(entry);
					_retainedSourceBytes += source.LengthBytes;
					TrimCache();
				}
			}

			return GitIgnoreMatcherLoadResult.Loaded(scopedMatcher);
		}
		catch (Exception exception) when (exception is
		       IOException or
		       UnauthorizedAccessException or
		       System.Security.SecurityException or
		       NotSupportedException or
		       ArgumentException)
		{
			return GitIgnoreMatcherLoadResult.ReadFailure;
		}
	}

	private static SourceProbeResult ProbeSource(string path)
	{
		try
		{
			var source = new FileInfo(path);
			source.Refresh();
			if (!string.IsNullOrEmpty(source.LinkTarget))
				return SourceProbeResult.SymbolicLink;

			var attributes = File.GetAttributes(path);
			if (attributes.HasFlag(FileAttributes.ReparsePoint))
				return SourceProbeResult.SymbolicLink;
			if (attributes.HasFlag(FileAttributes.Directory))
				return SourceProbeResult.ReadFailure;
			if (!UnixFileTypeInspector.IsRegularFile(path))
				return SourceProbeResult.ReadFailure;

			return SourceProbeResult.RegularFile(new SourceStamp(
				source.Length,
				source.LastWriteTimeUtc.Ticks,
				source.CreationTimeUtc.Ticks));
		}
		catch (Exception exception) when (exception is
		       FileNotFoundException or
		       DirectoryNotFoundException)
		{
			return SourceProbeResult.NotFound;
		}
		catch (Exception exception) when (exception is
		       IOException or
		       UnauthorizedAccessException or
		       System.Security.SecurityException or
		       NotSupportedException or
		       ArgumentException)
		{
			return SourceProbeResult.ReadFailure;
		}
	}

	private static void TrimCache()
	{
		while ((Cache.Count > CacheLimit || _retainedSourceBytes > MaximumRetainedSourceBytes) &&
		       CacheLru.Last is { } leastRecentlyUsed)
		{
			Remove(leastRecentlyUsed.Value.Path);
		}
	}

	private static void Remove(string path)
	{
		if (!Cache.Remove(path, out var node))
			return;

		_retainedSourceBytes = Math.Max(0, _retainedSourceBytes - node.Value.SourceLengthBytes);
		CacheLru.Remove(node);
	}

	private sealed record CacheEntry(
		string Path,
		long SourceLengthBytes,
		string ContentFingerprint,
		GitPathComparisonSemantics ComparisonSemantics,
		ScopedGitIgnoreMatcher Matcher);

	private enum SourceProbeStatus
	{
		RegularFile,
		NotFound,
		SymbolicLink,
		ReadFailure
	}

	private readonly record struct SourceStamp(
		long LengthBytes,
		long LastWriteTicksUtc,
		long CreationTicksUtc);

	private readonly record struct SourceProbeResult(
		SourceProbeStatus Status,
		SourceStamp Stamp)
	{
		public static SourceProbeResult NotFound { get; } =
			new(SourceProbeStatus.NotFound, default);

		public static SourceProbeResult SymbolicLink { get; } =
			new(SourceProbeStatus.SymbolicLink, default);

		public static SourceProbeResult ReadFailure { get; } =
			new(SourceProbeStatus.ReadFailure, default);

		public static SourceProbeResult RegularFile(SourceStamp stamp) =>
			new(SourceProbeStatus.RegularFile, stamp);
	}
}

internal enum GitIgnoreMatcherLoadStatus
{
	Loaded,
	NotFound,
	SymbolicLinkSkipped,
	ReadFailure
}

internal readonly record struct GitIgnoreMatcherLoadResult(
	GitIgnoreMatcherLoadStatus Status,
	ScopedGitIgnoreMatcher? Matcher)
{
	public static GitIgnoreMatcherLoadResult NotFound { get; } =
		new(GitIgnoreMatcherLoadStatus.NotFound, null);

	public static GitIgnoreMatcherLoadResult SymbolicLinkSkipped { get; } =
		new(GitIgnoreMatcherLoadStatus.SymbolicLinkSkipped, null);

	public static GitIgnoreMatcherLoadResult ReadFailure { get; } =
		new(GitIgnoreMatcherLoadStatus.ReadFailure, null);

	public static GitIgnoreMatcherLoadResult Loaded(ScopedGitIgnoreMatcher matcher) =>
		new(GitIgnoreMatcherLoadStatus.Loaded, matcher);
}
