using System.Collections.Concurrent;
using DevProjex.Application.Diagnostics;
using DevProjex.Infrastructure.Git;

namespace DevProjex.Infrastructure.FileSystem;

/// <summary>
/// Gives one filesystem operation a stable, single-flight view of each .gitignore source.
/// Process-wide matcher caching remains responsible for cross-operation reuse and revalidation.
/// </summary>
internal sealed class GitIgnoreMatcherLoadSession
{
	private readonly ConcurrentDictionary<string, Lazy<GitIgnoreMatcherLoadResult>> _loads =
		new(ProjectTreePathIdentity.CanonicalComparer);
	private readonly Func<string, string, CancellationToken, GitIgnoreMatcherLoadResult> _loader;
	private readonly ConcurrentDictionary<string, Lazy<GitIgnoreMatcherLoadResult>> _repositoryScopes =
		new(ProjectTreePathIdentity.CanonicalComparer);
	private readonly ConcurrentDictionary<string, Lazy<GitSubmoduleManifest>> _submodules =
		new(ProjectTreePathIdentity.CanonicalComparer);

	public GitIgnoreMatcherLoadResult LoadScope(string directoryPath, string? gitIgnorePath,
		string? gitMetadataPath, string? owner, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if (string.IsNullOrWhiteSpace(gitMetadataPath))
			return string.IsNullOrWhiteSpace(gitIgnorePath) ? GitIgnoreMatcherLoadResult.NotFound :
				LoadWithCancellation(directoryPath, gitIgnorePath, cancellationToken);
		if (owner is not null && !PathComparer.Default.Equals(directoryPath, owner))
		{
			var manifest = GetOrLoad(_submodules, owner, () => GitSubmoduleManifest.Read(owner, cancellationToken));
			if (manifest.ReadFailed)
				return RepositoryFailure(directoryPath, opaque: true);
			if (!manifest.Paths.Contains(PathUtility.GetPortableRelativePath(owner, directoryPath)))
				return GitIgnoreMatcherLoadResult.Loaded(new ScopedGitIgnoreMatcher(directoryPath, GitIgnoreMatcher.Empty)
				{
					IsRepositoryBoundary = true,
					IsOpaqueRepository = true
				});
		}
		return LoadRepositoryScope(directoryPath, gitMetadataPath, gitIgnorePath, cancellationToken);
	}

	public GitIgnoreMatcherLoadResult LoadRepositoryScope(
		string repositoryRoot, string gitMetadataPath, string? gitIgnorePath, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		return GetOrLoad(_repositoryScopes, repositoryRoot, () =>
		{
			try
			{
				if (!GitLocalConfigSemanticsReader.TryResolveGitDirectory(repositoryRoot, gitMetadataPath, out var gitDirectory) ||
				    !GitLocalConfigSemanticsReader.TryResolveCommonDirectory(gitDirectory, out var commonDirectory))
					return RepositoryFailure(repositoryRoot);
				var exclude = LoadWithCancellation(repositoryRoot, Path.Combine(commonDirectory, "info", "exclude"), cancellationToken);
				var ignore = LoadWithCancellation(repositoryRoot, gitIgnorePath ?? Path.Combine(repositoryRoot, ".gitignore"), cancellationToken);
				if (exclude.Status == GitIgnoreMatcherLoadStatus.ReadFailure || ignore.Status == GitIgnoreMatcherLoadStatus.ReadFailure)
					return RepositoryFailure(repositoryRoot);
				return GitIgnoreMatcherLoadResult.Loaded(new ScopedGitIgnoreMatcher(repositoryRoot,
					GitIgnoreMatcher.Combine(exclude.Matcher?.Matcher ?? GitIgnoreMatcher.Empty,
						ignore.Matcher?.Matcher ?? GitIgnoreMatcher.Empty)) { IsRepositoryBoundary = true });
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
			       System.Security.SecurityException or ArgumentException or NotSupportedException)
			{
				return RepositoryFailure(repositoryRoot);
			}
		});
	}

	private static GitIgnoreMatcherLoadResult RepositoryFailure(string root, bool opaque = false) =>
		new(GitIgnoreMatcherLoadStatus.ReadFailure, new ScopedGitIgnoreMatcher(root, GitIgnoreMatcher.Empty)
		{
			IsRepositoryBoundary = true, IsOpaqueRepository = opaque
		});

	private static T GetOrLoad<T>(ConcurrentDictionary<string, Lazy<T>> cache, string path, Func<T> loader)
	{
		var load = cache.GetOrAdd(path, _ => new Lazy<T>(loader, LazyThreadSafetyMode.ExecutionAndPublication));
		try
		{
			return load.Value;
		}
		catch (OperationCanceledException)
		{
			cache.TryRemove(new KeyValuePair<string, Lazy<T>>(path, load));
			throw;
		}
	}

	public GitIgnoreMatcherLoadSession()
		: this(static (scopeRootPath, gitIgnorePath, cancellationToken) =>
			GitIgnoreMatcherFileCache.LoadWithCancellation(
				scopeRootPath,
				gitIgnorePath,
				cancellationToken))
	{
	}

	internal GitIgnoreMatcherLoadSession(Func<string, string, GitIgnoreMatcherLoadResult> loader)
		: this((scopeRootPath, gitIgnorePath, _) => loader(scopeRootPath, gitIgnorePath))
	{
	}

	internal GitIgnoreMatcherLoadSession(
		Func<string, string, CancellationToken, GitIgnoreMatcherLoadResult> loader)
	{
		_loader = loader ?? throw new ArgumentNullException(nameof(loader));
	}

	internal void Seed(IReadOnlyList<ScopedGitIgnoreMatcher> matchers)
	{
		foreach (var matcher in matchers)
		{
			if (matcher.IsRepositoryBoundary)
				continue;
			string sourcePath;
			string cacheKey;
			try
			{
				sourcePath = Path.GetFullPath(Path.Combine(matcher.ScopeRootPath, ".gitignore"));
				cacheKey = GitIgnoreMatcherFileCache.CreateCacheKey(matcher.ScopeRootPath, sourcePath);
			}
			catch (Exception exception) when (exception is
			       NotSupportedException or
			       ArgumentException or
			       System.Security.SecurityException)
			{
				continue;
			}

			if (_loads.ContainsKey(cacheKey))
				continue;

			// Rules created for this operation already own the parsed matcher. Seeding keeps
			// later traversal from revalidating the same source while preserving the exact
			// matcher instance and typed load result for every parallel consumer.
			_loads.TryAdd(
				cacheKey,
				new Lazy<GitIgnoreMatcherLoadResult>(
					() => GitIgnoreMatcherLoadResult.Loaded(matcher),
					LazyThreadSafetyMode.ExecutionAndPublication));
		}
	}

	public GitIgnoreMatcherLoadResult Load(string scopeRootPath, string gitIgnorePath) =>
		LoadWithCancellation(scopeRootPath, gitIgnorePath, CancellationToken.None);

	public GitIgnoreMatcherLoadResult LoadWithCancellation(
		string scopeRootPath,
		string gitIgnorePath,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		IgnorePipelineDiagnostics.RecordGitIgnoreLoadRequest();

		string normalizedPath;
		string cacheKey;
		try
		{
			normalizedPath = Path.GetFullPath(gitIgnorePath);
			cacheKey = GitIgnoreMatcherFileCache.CreateCacheKey(scopeRootPath, normalizedPath);
		}
		catch (Exception exception) when (exception is
		       NotSupportedException or
		       ArgumentException or
		       System.Security.SecurityException)
		{
			return Execute(scopeRootPath, gitIgnorePath, cancellationToken);
		}

		if (_loads.TryGetValue(cacheKey, out var cached))
		{
			IgnorePipelineDiagnostics.RecordGitIgnoreLoadReuse();
			cancellationToken.ThrowIfCancellationRequested();
			return GetValueOrRemoveCanceled(cacheKey, cached);
		}

		var candidate = new Lazy<GitIgnoreMatcherLoadResult>(
			() => Execute(scopeRootPath, normalizedPath, cancellationToken),
			LazyThreadSafetyMode.ExecutionAndPublication);
		var selected = _loads.GetOrAdd(cacheKey, candidate);
		if (!ReferenceEquals(selected, candidate))
			IgnorePipelineDiagnostics.RecordGitIgnoreLoadReuse();

		if (cancellationToken.IsCancellationRequested)
		{
			if (ReferenceEquals(selected, candidate))
				RemoveExact(cacheKey, selected);
			cancellationToken.ThrowIfCancellationRequested();
		}

		return GetValueOrRemoveCanceled(cacheKey, selected);
	}

	private GitIgnoreMatcherLoadResult GetValueOrRemoveCanceled(
		string normalizedPath,
		Lazy<GitIgnoreMatcherLoadResult> load)
	{
		try
		{
			return load.Value;
		}
		catch (OperationCanceledException)
		{
			RemoveExact(normalizedPath, load);
			throw;
		}
	}

	private void RemoveExact(string normalizedPath, Lazy<GitIgnoreMatcherLoadResult> load) =>
		((ICollection<KeyValuePair<string, Lazy<GitIgnoreMatcherLoadResult>>>)_loads)
		.Remove(new KeyValuePair<string, Lazy<GitIgnoreMatcherLoadResult>>(normalizedPath, load));

	private GitIgnoreMatcherLoadResult Execute(
		string scopeRootPath,
		string gitIgnorePath,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		IgnorePipelineDiagnostics.RecordGitIgnoreLoadExecution();
		return _loader(scopeRootPath, gitIgnorePath, cancellationToken);
	}
}
