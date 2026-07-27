namespace DevProjex.Terminal.Tui;

public sealed record TerminalWelcomeContext(
	string CurrentDirectory,
	bool CanOpenCurrentDirectory,
	IReadOnlyList<string> RecentProjects);

public static class TerminalWelcomePolicy
{
	private static readonly string[] ProjectMarkers =
	[
		".git",
		"package.json",
		"pyproject.toml",
		"Cargo.toml",
		"go.mod",
		"pom.xml",
		"build.gradle",
		"build.gradle.kts",
		"composer.json",
		"Gemfile"
	];

	private static readonly string[] ProjectFilePatterns =
	[
		"*.sln",
		"*.slnx",
		"*.csproj",
		"*.fsproj",
		"*.vbproj"
	];

	public static TerminalWelcomeContext Create(
		string currentDirectory,
		IEnumerable<string> recentProjects)
	{
		var normalizedCurrent = PathUtility.Normalize(currentDirectory);
		var recent = recentProjects
			.Select(TryNormalize)
			.Where(static path => path is not null)
			.Select(static path => path!)
			.Where(Directory.Exists)
			.Where(path => !PathComparer.Default.Equals(path, normalizedCurrent))
			.Distinct(PathComparer.Default)
			.Take(15)
			.ToArray();
		return new TerminalWelcomeContext(
			normalizedCurrent,
			IsSafeProjectWorkspace(normalizedCurrent),
			recent);
	}

	public static bool IsSafeProjectWorkspace(string path)
	{
		string normalized;
		try
		{
			normalized = PathUtility.Normalize(path);
		}
		catch
		{
			return false;
		}

		if (!Directory.Exists(normalized) || IsBroadSystemLocation(normalized))
			return false;

		try
		{
			if (ProjectMarkers.Any(marker =>
				    Directory.Exists(Path.Combine(normalized, marker)) ||
				    File.Exists(Path.Combine(normalized, marker))))
			{
				return true;
			}

			foreach (var pattern in ProjectFilePatterns)
			{
				if (Directory.EnumerateFiles(normalized, pattern, SearchOption.TopDirectoryOnly).Any())
					return true;
			}
		}
		catch (IOException)
		{
			return false;
		}
		catch (UnauthorizedAccessException)
		{
			return false;
		}

		return false;
	}

	private static bool IsBroadSystemLocation(string path)
	{
		var root = Path.GetPathRoot(path);
		if (!string.IsNullOrEmpty(root) && PathComparer.Default.Equals(path, PathUtility.Normalize(root)))
			return true;

		var protectedPaths = new[]
		{
			Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
			Environment.GetFolderPath(Environment.SpecialFolder.Windows),
			Environment.GetFolderPath(Environment.SpecialFolder.System),
			Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
			Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
			OperatingSystem.IsWindows() ? null : "/usr",
			OperatingSystem.IsWindows() ? null : "/etc",
			OperatingSystem.IsWindows() ? null : "/var",
			OperatingSystem.IsWindows() ? null : "/private",
			OperatingSystem.IsWindows() ? null : "/System",
			OperatingSystem.IsWindows() ? null : "/Applications",
			OperatingSystem.IsWindows() ? null : "/Library"
		};
		return protectedPaths
			.Where(static candidate => !string.IsNullOrWhiteSpace(candidate))
			.Select(static candidate => TryNormalize(candidate!))
			.Any(candidate => candidate is not null && PathComparer.Default.Equals(path, candidate));
	}

	private static string? TryNormalize(string path)
	{
		try
		{
			return PathUtility.Normalize(path);
		}
		catch
		{
			return null;
		}
	}
}
