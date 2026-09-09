using DevProjex.Infrastructure.FileSystem;
using DevProjex.Application.Services;

namespace DevProjex.Infrastructure.Git;

internal static class GitRemoteIdentityStore
{
	private const string IdentityFileName = "devprojex.remote-identity";
	private const int MaximumIdentityLength = 4096;

	public static void Write(
		string repositoryPath,
		string remoteUrl,
		string? sourceIdentityUrl = null,
		bool allowFileTransport = false)
	{
		var gitDirectory = ResolveCommonGitDirectory(repositoryPath);
		if (!Directory.Exists(gitDirectory))
			throw new InvalidOperationException("The cloned repository metadata is unavailable.");
		var safeUrl = GitNetworkPolicy.ValidateUrl(remoteUrl, allowFileTransport);
		var path = Path.Combine(gitDirectory, IdentityFileName);
		var safeSourceIdentity = RepositoryUrlUtility.ToSafeSourceIdentity(sourceIdentityUrl ?? safeUrl);
		File.WriteAllText(
			path,
			safeUrl + "\n" + safeSourceIdentity,
			new UTF8Encoding(false));
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
			var saved = File.ReadLines(path).FirstOrDefault()?.Trim();
			if (string.IsNullOrEmpty(saved))
				return false;
			return string.Equals(
				RepositoryUrlUtility.GetSourceCacheKey(saved),
				RepositoryUrlUtility.GetSourceCacheKey(remoteUrl),
				StringComparison.Ordinal);
		}
		catch
		{
			return false;
		}
	}

	public static bool TryReadSourceIdentity(string repositoryPath, out string sourceIdentity)
	{
		sourceIdentity = string.Empty;
		try
		{
			var path = Path.Combine(ResolveCommonGitDirectory(repositoryPath), IdentityFileName);
			var info = new FileInfo(path);
			if (!info.Exists || info.Length <= 0 || info.Length > MaximumIdentityLength ||
			    !UnixFileTypeInspector.IsRegularFile(path))
			{
				return false;
			}
			var lines = File.ReadLines(path).Take(2).ToArray();
			sourceIdentity = (lines.Length > 1 ? lines[1] : lines[0]).Trim();
			return RepositoryUrlUtility.GetSourceCacheKey(sourceIdentity).Length > 0;
		}
		catch
		{
			sourceIdentity = string.Empty;
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
