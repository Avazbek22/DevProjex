namespace DevProjex.Terminal.Execution;

public sealed class ProjectSourceIdentityResolver(
	IGitRepositoryService gitRepositoryService,
	IRepoCacheService repoCacheService)
{
	public async Task<ProjectSourceIdentity> ResolveAsync(
		string projectPath,
		ProjectSourceIdentity? knownIdentity = null,
		CancellationToken cancellationToken = default)
	{
		var normalizedPath = PathUtility.Normalize(projectPath);
		if (knownIdentity is not null)
		{
			var normalizedIdentity = NormalizeKnownIdentity(knownIdentity, normalizedPath);
			RecordCachedIdentity(normalizedIdentity, normalizedPath);
			return normalizedIdentity;
		}

		if (!repoCacheService.IsInCache(normalizedPath))
			return CreateLocalIdentity(normalizedPath);

		var repositoryUrl = await gitRepositoryService
			.GetRemoteUrlAsync(normalizedPath, cancellationToken)
			.ConfigureAwait(false);
		var branch = await gitRepositoryService
			.GetCurrentBranchAsync(normalizedPath, cancellationToken)
			.ConfigureAwait(false);
		var commitHash = await gitRepositoryService
			.GetHeadCommitAsync(normalizedPath, cancellationToken)
			.ConfigureAwait(false);
		var displayName = repositoryUrl is { Length: > 0 }
			? RepositoryUrlUtility.GetRepositoryName(repositoryUrl)
			: RemoveCacheSuffix(GetPathName(normalizedPath));
		var safeUrl = RepositoryUrlUtility.ToSafeDisplay(repositoryUrl);
		if (safeUrl.Length > 0)
		{
			repoCacheService.RecordIndexedRepository(
				safeUrl,
				normalizedPath,
				branch,
				commitHash);
		}

		return new ProjectSourceIdentity(
			displayName,
			ProjectSourceType.GitClone,
			safeUrl.Length > 0 ? safeUrl : displayName,
			safeUrl.Length > 0 ? safeUrl : null,
			branch,
			commitHash,
			IsCachedRepository: true);
	}

	private void RecordCachedIdentity(
		ProjectSourceIdentity identity,
		string normalizedPath)
	{
		if (identity.SourceType != ProjectSourceType.GitClone ||
		    !repoCacheService.IsInCache(normalizedPath))
		{
			return;
		}

		var repositoryUrl = identity.RepositoryUrl ?? identity.SourceReference;
		if (RepositoryUrlUtility.GetComparisonKey(repositoryUrl).Length == 0)
			return;

		repoCacheService.RecordIndexedRepository(
			repositoryUrl,
			normalizedPath,
			identity.Branch,
			identity.CommitHash);
	}

	public static ProjectSourceIdentity CreateCloneIdentity(
		string repositoryUrl,
		string? repositoryName,
		string? branch = null,
		string? commitHash = null)
	{
		var safeUrl = RepositoryUrlUtility.ToSafeDisplay(repositoryUrl);
		var displayName = string.IsNullOrWhiteSpace(repositoryName)
			? RepositoryUrlUtility.GetRepositoryName(safeUrl)
			: repositoryName.Trim();
		return new ProjectSourceIdentity(
			displayName,
			ProjectSourceType.GitClone,
			safeUrl,
			safeUrl,
			branch,
			commitHash,
			IsCachedRepository: true);
	}

	private static ProjectSourceIdentity NormalizeKnownIdentity(
		ProjectSourceIdentity identity,
		string normalizedPath)
	{
		if (identity.SourceType != ProjectSourceType.GitClone)
			return CreateLocalIdentity(normalizedPath);

		var safeUrl = RepositoryUrlUtility.ToSafeDisplay(
			identity.RepositoryUrl ?? identity.SourceReference);
		var displayName = string.IsNullOrWhiteSpace(identity.DisplayName)
			? RepositoryUrlUtility.GetRepositoryName(safeUrl)
			: identity.DisplayName.Trim();
		return identity with
		{
			DisplayName = displayName,
			SourceReference = safeUrl.Length > 0 ? safeUrl : displayName,
			RepositoryUrl = safeUrl.Length > 0 ? safeUrl : null,
			IsCachedRepository = true
		};
	}

	private static ProjectSourceIdentity CreateLocalIdentity(string normalizedPath)
	{
		var displayName = GetPathName(normalizedPath);
		return new ProjectSourceIdentity(
			displayName,
			ProjectSourceType.LocalFolder,
			normalizedPath);
	}

	private static string GetPathName(string path)
	{
		var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
		return string.IsNullOrWhiteSpace(name) ? path : name;
	}

	private static string RemoveCacheSuffix(string name)
	{
		var separator = name.LastIndexOf('_');
		if (separator <= 0 || separator == name.Length - 1)
			return name;

		var suffix = name.AsSpan(separator + 1);
		return suffix.Length >= 12 && IsHexadecimal(suffix)
			? name[..separator]
			: name;
	}

	private static bool IsHexadecimal(ReadOnlySpan<char> value)
	{
		foreach (var character in value)
		{
			if (!char.IsAsciiHexDigit(character))
				return false;
		}

		return true;
	}
}
