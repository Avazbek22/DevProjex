namespace DevProjex.Infrastructure.Git;

internal sealed record GitRepositoryResourceLimits
{
	private const long GiB = 1024L * 1024 * 1024;

	public static GitRepositoryResourceLimits Default { get; } = new();

	public long MaximumRepositoryBytes { get; init; } = RepositoryCachePolicy.DefaultMaximumSizeBytes;
	public long FreeSpaceReserveBytes { get; init; } = GiB;
	public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(100);
	public Func<string, long>? AvailableFreeSpace { get; init; }

	public GitRepositoryResourceLimits Validate()
	{
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumRepositoryBytes);
		ArgumentOutOfRangeException.ThrowIfNegative(FreeSpaceReserveBytes);
		if (PollInterval <= TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(nameof(PollInterval));
		return this;
	}

	public long GetAvailableFreeSpace(string destinationPath)
	{
		var probePath = ResolveExistingDestinationParent(destinationPath);
		if (AvailableFreeSpace is not null)
			return AvailableFreeSpace(probePath);
		var driveRoot = ResolveDestinationDriveRoot(
			probePath,
			DriveInfo.GetDrives().Select(static drive => drive.Name));
		return new DriveInfo(driveRoot).AvailableFreeSpace;
	}

	internal static string ResolveDestinationDriveRoot(
		string destinationPath,
		IEnumerable<string> driveRoots)
	{
		var fullPath = Path.GetFullPath(destinationPath);
		var comparison = OperatingSystem.IsWindows()
			? StringComparison.OrdinalIgnoreCase
			: StringComparison.Ordinal;
		var selected = driveRoots
			.Select(Path.GetFullPath)
			.Where(root => IsWithin(fullPath, root, comparison))
			.OrderByDescending(static root => root.Length)
			.FirstOrDefault();
		return selected ?? Path.GetPathRoot(fullPath) ?? fullPath;
	}

	private static string ResolveExistingDestinationParent(string destinationPath)
	{
		var current = Path.GetFullPath(destinationPath);
		while (!Directory.Exists(current))
		{
			var parent = Path.GetDirectoryName(current);
			if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.Ordinal))
				break;
			current = parent;
		}
		return current;
	}

	private static bool IsWithin(string path, string root, StringComparison comparison)
	{
		if (path.Equals(root, comparison))
			return true;
		var prefix = root.EndsWith(Path.DirectorySeparatorChar)
			? root
			: root + Path.DirectorySeparatorChar;
		return path.StartsWith(prefix, comparison);
	}
}
