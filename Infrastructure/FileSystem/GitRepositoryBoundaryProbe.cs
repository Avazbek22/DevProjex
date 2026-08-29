namespace DevProjex.Infrastructure.FileSystem;

public static class GitRepositoryBoundaryProbe
{
	public static bool ExistsAtOrAbove(string projectPath) =>
		GitTrackedPathIndexCache.TryFindNearestRepositoryBoundary(
			projectPath,
			CancellationToken.None,
			out _);
}
