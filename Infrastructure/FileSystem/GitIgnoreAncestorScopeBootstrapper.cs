namespace DevProjex.Infrastructure.FileSystem;

internal static class GitIgnoreAncestorScopeBootstrapper
{
	public static GitIgnoreAncestorScopeBootstrapResult Apply(
		string scanRootPath,
		IgnoreRules.GitIgnoreScanContext activeContext,
		IgnoreRules.GitIgnoreScanContext candidateContext,
		CancellationToken cancellationToken,
		ScopedGitIgnoreMatcherAccumulator? discoveredMatchers = null,
		GitIgnoreMatcherLoadSession? loadSession = null)
	{
		loadSession ??= new GitIgnoreMatcherLoadSession();
		cancellationToken.ThrowIfCancellationRequested();
		if (!GitTrackedPathIndexCache.TryFindNearestRepositoryBoundary(
			    scanRootPath,
			    cancellationToken,
			    out var repositoryRootPath))
		{
			return new GitIgnoreAncestorScopeBootstrapResult(
				activeContext,
				candidateContext,
				GitIgnoreMatcherLoadStatus.NotFound);
		}

		string normalizedScanRoot;
		try
		{
			normalizedScanRoot = PathUtility.Normalize(scanRootPath);
		}
		catch
		{
			return new GitIgnoreAncestorScopeBootstrapResult(
				activeContext,
				candidateContext,
				GitIgnoreMatcherLoadStatus.NotFound);
		}

		var scopePaths = BuildScopePathChain(repositoryRootPath, normalizedScanRoot);
		if (scopePaths.Count == 0)
		{
			return new GitIgnoreAncestorScopeBootstrapResult(
				activeContext,
				candidateContext,
				GitIgnoreMatcherLoadStatus.NotFound);
		}

		var lastStatus = GitIgnoreMatcherLoadStatus.NotFound;
		foreach (var scopePath in scopePaths)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var loadResult = PathComparer.Default.Equals(scopePath, repositoryRootPath)
				? loadSession.LoadRepositoryScope(scopePath, Path.Combine(scopePath, ".git"),
					Path.Combine(scopePath, ".gitignore"), cancellationToken)
				: loadSession.LoadWithCancellation(
				scopePath,
				Path.Combine(scopePath, ".gitignore"),
				cancellationToken);
			lastStatus = loadResult.Status;
			if (loadResult.Status == GitIgnoreMatcherLoadStatus.ReadFailure)
			{
				if (loadResult.Matcher is { IsRepositoryBoundary: true } boundary)
				{
					activeContext = activeContext.WithAncestorScope(boundary, normalizedScanRoot);
					candidateContext = candidateContext.WithAncestorScope(boundary, normalizedScanRoot);
					discoveredMatchers?.Add(boundary);
				}
				return new GitIgnoreAncestorScopeBootstrapResult(
					activeContext,
					candidateContext,
					loadResult.Status);
			}

			if (loadResult.Matcher is not { } matcher)
				continue;

			activeContext = activeContext.WithAncestorScope(matcher, normalizedScanRoot);
			candidateContext = candidateContext.WithAncestorScope(matcher, normalizedScanRoot);
			discoveredMatchers?.Add(matcher);
		}

		return new GitIgnoreAncestorScopeBootstrapResult(
			activeContext,
			candidateContext,
			lastStatus);
	}

	private static List<string> BuildScopePathChain(string repositoryRootPath, string scanRootPath)
	{
		var paths = new List<string>();
		var currentPath = scanRootPath;
		while (true)
		{
			paths.Add(currentPath);
			if (PathComparer.Default.Equals(currentPath, repositoryRootPath))
				break;

			var parentPath = Path.GetDirectoryName(currentPath);
			if (string.IsNullOrWhiteSpace(parentPath) || PathComparer.Default.Equals(parentPath, currentPath))
				return [];
			currentPath = parentPath;
		}

		paths.Reverse();
		return paths;
	}

}

internal readonly record struct GitIgnoreAncestorScopeBootstrapResult(
	IgnoreRules.GitIgnoreScanContext Active,
	IgnoreRules.GitIgnoreScanContext Candidate,
	GitIgnoreMatcherLoadStatus LoadStatus);
