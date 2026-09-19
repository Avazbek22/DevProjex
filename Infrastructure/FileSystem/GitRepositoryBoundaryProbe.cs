using DevProjex.Infrastructure.Git;

namespace DevProjex.Infrastructure.FileSystem;

public static class GitRepositoryBoundaryProbe
{
	public static bool ExistsAtOrAbove(string projectPath) =>
		GitTrackedPathIndexCache.TryFindNearestRepositoryBoundary(
			projectPath,
			CancellationToken.None,
			out _);

	public static bool TryResolveMetadataDirectories(
		string repositoryRoot,
		out string gitDirectory,
		out string commonDirectory)
	{
		gitDirectory = string.Empty;
		commonDirectory = string.Empty;
		try
		{
			var metadataPath = Path.Combine(repositoryRoot, ".git");
			return GitLocalConfigSemanticsReader.TryResolveGitDirectory(
					repositoryRoot,
					metadataPath,
					out gitDirectory) &&
			       GitLocalConfigSemanticsReader.TryResolveCommonDirectory(
				       gitDirectory,
				       out commonDirectory);
		}
		catch (Exception exception) when (exception is
		       IOException or UnauthorizedAccessException or System.Security.SecurityException or
		       NotSupportedException or ArgumentException)
		{
			gitDirectory = string.Empty;
			commonDirectory = string.Empty;
			return false;
		}
	}

	internal static bool ExistsAt(string directoryPath) =>
		GitTrackedPathIndexCache.TryMetadataEntryEstablishesBoundary(
			Path.Combine(directoryPath, ".git"));
}
