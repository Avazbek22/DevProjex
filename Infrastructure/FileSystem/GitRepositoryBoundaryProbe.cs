namespace DevProjex.Infrastructure.FileSystem;

public static class GitRepositoryBoundaryProbe
{
	public static bool ExistsAtOrAbove(string projectPath) =>
		GitTrackedPathIndexCache.TryFindNearestRepositoryBoundary(
			projectPath,
			CancellationToken.None,
			out _);

	internal static bool ExistsAt(string directoryPath) =>
		GitTrackedPathIndexCache.TryMetadataEntryEstablishesBoundary(
			Path.Combine(directoryPath, ".git"));
}
