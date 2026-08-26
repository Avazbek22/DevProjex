namespace DevProjex.Infrastructure.FileSystem;

public sealed record GitTrackedModeReadiness(
	bool HasReadableIndex,
	string? RepositoryRoot,
	int TrackedPathCount);

public sealed class GitTrackedModeReadinessProbe
{
	public GitTrackedModeReadiness Probe(
		string path,
		CancellationToken cancellationToken = default)
	{
		if (PathUtility.IsMissingPath(path) || !Directory.Exists(path))
			return new GitTrackedModeReadiness(false, null, 0);

		return GitTrackedPathIndexCache.TryLoadNearest(path, cancellationToken, out var index) &&
		       index.IsAvailable
			? new GitTrackedModeReadiness(true, index.RepositoryRootPath, index.Count)
			: new GitTrackedModeReadiness(false, null, 0);
	}
}
