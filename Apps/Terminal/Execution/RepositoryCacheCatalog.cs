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
		var indexed = repoCacheService.FindIndexedRepository(safeUrl);
		if (indexed is not null)
		{
			if (!Directory.Exists(indexed.LocalPath))
			{
				repoCacheService.RemoveIndexedRepository(indexed.LocalPath);
			}
			else
			{
				var indexedResult = await ResolveCandidateAsync(
						safeUrl,
						repositoryName,
						indexed.LocalPath,
						cancellationToken)
					.ConfigureAwait(false);
				if (indexedResult is not null)
					return indexedResult;

				repoCacheService.RemoveIndexedRepository(indexed.LocalPath);
			}
		}

		var candidates = EnumerateCandidates(repositoryName);
		CachedRepository? damaged = null;
		foreach (var candidate in candidates)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var resolved = await ResolveCandidateAsync(
					safeUrl,
					repositoryName,
					candidate,
					cancellationToken)
				.ConfigureAwait(false);
			if (resolved is null)
			{
				continue;
			}

			if (resolved.State == RepositoryCacheState.Ready)
				return resolved;
			damaged ??= resolved;
		}

		return damaged ?? new CachedRepository(
			safeUrl,
			repositoryName,
			RepositoryCacheState.Missing);
	}

	private async Task<CachedRepository?> ResolveCandidateAsync(
		string repositoryUrl,
		string repositoryName,
		string candidate,
		CancellationToken cancellationToken)
	{
		if (!IsSafeCacheCandidate(candidate) || HasLinkedGitMetadata(candidate))
			return null;

		var remoteUrl = await gitRepositoryService
			.GetRemoteUrlAsync(candidate, cancellationToken)
			.ConfigureAwait(false);
		if (!RepositoryUrlUtility.AreEquivalent(remoteUrl, repositoryUrl))
		{
			return remoteUrl is null
				? RecordDamaged(repositoryUrl, repositoryName, candidate)
				: null;
		}

		if (!HasGitMetadata(candidate))
			return RecordDamaged(repositoryUrl, repositoryName, candidate);

		var branch = await gitRepositoryService
			.GetCurrentBranchAsync(candidate, cancellationToken)
			.ConfigureAwait(false);
		var commitHash = await gitRepositoryService
			.GetHeadCommitAsync(candidate, cancellationToken)
			.ConfigureAwait(false);
		repoCacheService.RecordIndexedRepository(
			repositoryUrl,
			candidate,
			branch,
			commitHash);
		return new CachedRepository(
			repositoryUrl,
			repositoryName,
			RepositoryCacheState.Ready,
			candidate,
			branch,
			commitHash,
			GetLastModified(candidate));
	}

	private IReadOnlyList<string> EnumerateCandidates(string repositoryName)
	{
		var candidates = new List<string>();
		foreach (var searchRoot in repoCacheService.CacheSearchRootPaths)
		{
			try
			{
				if (!IsSafeCacheCandidate(searchRoot))
					continue;

				candidates.AddRange(
					Directory
						.EnumerateDirectories(searchRoot)
						.Where(path => !string.Equals(
							Path.GetFileName(path),
							".staging",
							StringComparison.Ordinal)));
			}
			catch (Exception ex) when (ex is
				IOException or
				UnauthorizedAccessException or
				ArgumentException or
				NotSupportedException or
				System.Security.SecurityException)
			{
				// An unavailable compatibility root must not hide healthy roots.
			}
		}

		var prefix = repositoryName + "_";
		return candidates
			.Distinct(PathComparer.Default)
			.OrderByDescending(path =>
				Path.GetFileName(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
			.ThenByDescending(GetLastModified)
			.ToArray();
	}

	private static bool IsSafeCacheCandidate(string path)
	{
		try
		{
			return Directory.Exists(path) &&
			       !File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
		}
		catch (Exception exception) when (exception is
			       IOException or
			       UnauthorizedAccessException or
			       ArgumentException or
			       NotSupportedException or
			       System.Security.SecurityException)
		{
			return false;
		}
	}

	private static bool HasLinkedGitMetadata(string path)
	{
		var metadataPath = Path.Combine(path, ".git");
		try
		{
			return (Directory.Exists(metadataPath) || File.Exists(metadataPath)) &&
			       File.GetAttributes(metadataPath).HasFlag(FileAttributes.ReparsePoint);
		}
		catch (Exception exception) when (exception is
			       IOException or
			       UnauthorizedAccessException or
			       ArgumentException or
			       NotSupportedException or
			       System.Security.SecurityException)
		{
			return true;
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

	private CachedRepository RecordDamaged(
		string repositoryUrl,
		string repositoryName,
		string path)
	{
		repoCacheService.RecordIndexedRepository(
			repositoryUrl,
			path,
			state: RepositoryCacheEntryState.Damaged);
		return CreateDamaged(repositoryUrl, repositoryName, path);
	}

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
