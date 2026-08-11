using System.Security.Cryptography;
using System.Globalization;

namespace DevProjex.Infrastructure.Git;

internal static class RepositoryCacheLayout
{
	public const string MarkerFileName = ".devprojex-cache";
	public const string DeletePendingMarkerName = ".devprojex-delete-pending";
	public const string BaseDirectoryName = "b";
	public const string SnapshotDirectoryName = "s";
	public const string WorktreesDirectoryName = "w";
	public const string LeasesDirectoryName = "l";
	public const string StagingDirectoryName = ".staging";
	public const string TrashDirectoryName = ".trash";
	public const string LocksDirectoryName = ".locks";

	public static string GetContainer(string repositoryPath)
	{
		var normalized = PathUtility.Normalize(repositoryPath);
		var parent = Directory.GetParent(normalized)?.FullName;
		if (parent is not null && File.Exists(Path.Combine(parent, MarkerFileName)))
			return parent;

		var ancestor = parent is null ? null : Directory.GetParent(parent)?.FullName;
		if (ancestor is not null && File.Exists(Path.Combine(ancestor, MarkerFileName)))
			return ancestor;

		return normalized;
	}

	public static bool IsManaged(string repositoryPath) =>
		File.Exists(Path.Combine(GetContainer(repositoryPath), MarkerFileName));

	public static string GetLeasePath(string cacheRoot, string repositoryPath)
	{
		var container = GetContainer(repositoryPath);
		if (File.Exists(Path.Combine(container, MarkerFileName)))
		{
			var relative = Path.GetRelativePath(container, repositoryPath);
			var name = relative
				.Replace(Path.DirectorySeparatorChar, '-')
				.Replace(Path.AltDirectorySeparatorChar, '-');
			return Path.Combine(container, LeasesDirectoryName, $"{name}.lock");
		}

		return Path.Combine(
			cacheRoot,
			LeasesDirectoryName,
			$"legacy-{HashPath(repositoryPath)}.lock");
	}

	public static string GetBaseOperationLockPath(string cacheRoot, string repositoryPath)
	{
		var container = GetContainer(repositoryPath);
		return File.Exists(Path.Combine(container, MarkerFileName))
			? Path.Combine(container, LeasesDirectoryName, "base-operation.lock")
			: Path.Combine(cacheRoot, LocksDirectoryName, $"base-{HashPath(repositoryPath)}.lock");
	}

	public static string GetRepositoryOperationLockPath(string cacheRoot, string identity) =>
		Path.Combine(cacheRoot, LocksDirectoryName, $"repo-{HashText(identity)}.lock");

	public static string GetTrashRoot(string cacheRoot) =>
		Path.Combine(cacheRoot, StagingDirectoryName, TrashDirectoryName);

	public static string GetWorktreesRoot(string basePath) =>
		Path.Combine(GetContainer(basePath), WorktreesDirectoryName);

	public static IReadOnlyList<string> EnumerateCopies(string basePath)
	{
		var copies = new List<string>();
		if (Directory.Exists(basePath))
			copies.Add(basePath);

		var worktreesRoot = GetWorktreesRoot(basePath);
		if (Directory.Exists(worktreesRoot))
		{
			try
			{
				copies.AddRange(Directory.EnumerateDirectories(worktreesRoot));
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
			}
		}

		return copies;
	}

	public static string CreateShortWorktreePath(string basePath)
	{
		var root = GetWorktreesRoot(basePath);
		Directory.CreateDirectory(root);
		for (var index = 1; ; index++)
		{
			var candidate = Path.Combine(root, index.ToString(CultureInfo.InvariantCulture));
			if (!Directory.Exists(candidate) && !File.Exists(candidate))
				return candidate;
		}
	}

	private static string HashPath(string path) =>
		HashText(PathUtility.Normalize(path));

	private static string HashText(string value)
	{
		var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
		return Convert.ToHexString(bytes.AsSpan(0, 10)).ToLowerInvariant();
	}
}
