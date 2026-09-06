using DevProjex.Infrastructure.FileSystem;
using DevProjex.Application.Services;

namespace DevProjex.Infrastructure.Git;

internal static class GitRemoteIdentityStore
{
	private const string IdentityFileName = "devprojex.remote-identity";
	private const int MaximumIdentityLength = 4096;

	public static void Write(string repositoryPath, string remoteUrl)
	{
		var gitDirectory = ResolveCommonGitDirectory(repositoryPath);
		if (!Directory.Exists(gitDirectory))
			throw new InvalidOperationException("The cloned repository metadata is unavailable.");
		var safeUrl = GitNetworkPolicy.ValidateUrl(remoteUrl);
		var path = Path.Combine(gitDirectory, IdentityFileName);
		File.WriteAllText(path, safeUrl, new UTF8Encoding(false));
		if (!OperatingSystem.IsWindows())
			File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
	}

	public static bool Matches(string repositoryPath, string remoteUrl)
	{
		try
		{
			var path = Path.Combine(ResolveCommonGitDirectory(repositoryPath), IdentityFileName);
			var info = new FileInfo(path);
			if (!info.Exists || info.Length <= 0 || info.Length > MaximumIdentityLength ||
			    !UnixFileTypeInspector.IsRegularFile(path))
			{
				return false;
			}
			var saved = File.ReadAllText(path).Trim();
			return string.Equals(
				RepositoryUrlUtility.GetComparisonKey(saved),
				RepositoryUrlUtility.GetComparisonKey(remoteUrl),
				StringComparison.Ordinal);
		}
		catch
		{
			return false;
		}
	}

	private static string ResolveCommonGitDirectory(string repositoryPath)
	{
		var normalized = Path.GetFullPath(repositoryPath);
		if (RepositoryCacheLayout.IsManaged(normalized))
		{
			var managed = Path.Combine(
				RepositoryCacheLayout.GetContainer(normalized),
				RepositoryCacheLayout.BaseDirectoryName,
				".git");
			if (Directory.Exists(managed))
				return managed;
		}

		var metadata = Path.Combine(normalized, ".git");
		if (Directory.Exists(metadata))
			return metadata;
		if (!File.Exists(metadata))
			throw new InvalidOperationException("The Git metadata directory is unavailable.");
		using var stream = new FileStream(
			metadata,
			FileMode.Open,
			FileAccess.Read,
			FileShare.ReadWrite | FileShare.Delete);
		if (!GitTrackedPathIndexCache.TryReadGitDirectoryPointer(stream, out var pointer))
		{
			throw new InvalidOperationException("The Git metadata directory is unavailable.");
		}

		var gitDirectory = Path.GetFullPath(Path.Combine(normalized, pointer));
		var commonDirectoryFile = Path.Combine(gitDirectory, "commondir");
		if (!File.Exists(commonDirectoryFile))
			return gitDirectory;
		var common = File.ReadAllText(commonDirectoryFile).Trim();
		return Path.GetFullPath(Path.Combine(gitDirectory, common));
	}
}
