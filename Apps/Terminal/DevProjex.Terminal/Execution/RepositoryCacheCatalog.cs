namespace DevProjex.Terminal.Execution;

public enum RepositoryCacheState
{
	Missing,
	Ready,
	Damaged
}

public sealed record CachedRepository(
	string RepositoryUrl,
	string RepositoryName,
	RepositoryCacheState State,
	string? LocalPath = null,
	string? Branch = null,
	string? CommitHash = null,
	DateTimeOffset? LastModifiedUtc = null);

public sealed class RepositoryCacheCatalog(
	IGitRepositoryService gitRepositoryService,
	IRepoCacheService repoCacheService)
{
	public async Task<CachedRepository> FindAsync(
		string repositoryUrl,
		CancellationToken cancellationToken = default)
	{
		var safeUrl = RepositoryUrlUtility.ToSafeDisplay(repositoryUrl);
		var repositoryName = RepositoryUrlUtility.GetRepositoryName(safeUrl);
		var candidates = EnumerateCandidates(repositoryName);
		CachedRepository? damaged = null;
		foreach (var candidate in candidates)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var remoteUrl = await gitRepositoryService
				.GetRemoteUrlAsync(candidate, cancellationToken)
				.ConfigureAwait(false);
			if (!RepositoryUrlUtility.AreEquivalent(remoteUrl, safeUrl))
			{
				if (remoteUrl is null && damaged is null)
					damaged = CreateDamaged(safeUrl, repositoryName, candidate);
				continue;
			}

			if (!HasGitMetadata(candidate))
				return CreateDamaged(safeUrl, repositoryName, candidate);

			var branch = await gitRepositoryService
				.GetCurrentBranchAsync(candidate, cancellationToken)
				.ConfigureAwait(false);
			var commitHash = await gitRepositoryService
				.GetHeadCommitAsync(candidate, cancellationToken)
				.ConfigureAwait(false);
			return new CachedRepository(
				safeUrl,
				repositoryName,
				RepositoryCacheState.Ready,
				candidate,
				branch,
				commitHash,
				GetLastModified(candidate));
		}

		return damaged ?? new CachedRepository(
			safeUrl,
			repositoryName,
			RepositoryCacheState.Missing);
	}

	private IReadOnlyList<string> EnumerateCandidates(string repositoryName)
	{
		try
		{
			if (!Directory.Exists(repoCacheService.CacheRootPath))
				return [];

			var prefix = repositoryName + "_";
			return Directory
				.EnumerateDirectories(repoCacheService.CacheRootPath)
				.OrderByDescending(path =>
					Path.GetFileName(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
				.ThenByDescending(GetLastModified)
				.ToArray();
		}
		catch
		{
			return [];
		}
	}

	private static bool HasGitMetadata(string path) =>
		Directory.Exists(Path.Combine(path, ".git")) ||
		File.Exists(Path.Combine(path, ".git"));

	private static CachedRepository CreateDamaged(
		string repositoryUrl,
		string repositoryName,
		string path) =>
		new(
			repositoryUrl,
			repositoryName,
			RepositoryCacheState.Damaged,
			path,
			LastModifiedUtc: GetLastModified(path));

	private static DateTimeOffset? GetLastModified(string path)
	{
		try
		{
			return Directory.GetLastWriteTimeUtc(path);
		}
		catch
		{
			return null;
		}
	}
}
