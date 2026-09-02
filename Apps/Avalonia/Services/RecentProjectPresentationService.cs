namespace DevProjex.Avalonia.Services;

public static class RecentProjectPresentationService
{
	public static string CreateFolderDisplayText(string path)
	{
		var normalized = NormalizePathForDisplay(path);
		if (string.IsNullOrWhiteSpace(normalized))
			return string.Empty;

		var trimmed = normalized.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		var leaf = Path.GetFileName(trimmed);
		if (string.IsNullOrWhiteSpace(leaf))
			return trimmed;

		var parent = Path.GetFileName(Path.GetDirectoryName(trimmed));
		if (string.IsNullOrWhiteSpace(parent))
			return leaf;

		return $"{parent} / {leaf}";
	}

	public static string CreateFolderToolTip(string path)
		=> NormalizePathForDisplay(path);

	public static string CreateRepositoryDisplayText(string repositoryUrl)
	{
		var normalized = NormalizeRepositoryUrl(repositoryUrl);
		if (string.IsNullOrWhiteSpace(normalized))
			return string.Empty;

		if (Uri.TryCreate(normalized, UriKind.Absolute, out var uri))
		{
			var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
			if (segments.Length >= 2)
			{
				var owner = segments[^2];
				var repo = TrimGitSuffix(segments[^1]);
				return string.IsNullOrWhiteSpace(owner) ? repo : $"{owner} / {repo}";
			}

			if (segments.Length == 1)
				return TrimGitSuffix(segments[0]);

			return uri.Host;
		}

		return normalized;
	}

	public static string CreateRepositoryToolTip(string repositoryUrl)
		=> NormalizeRepositoryUrl(repositoryUrl);

	private static string NormalizePathForDisplay(string path)
	{
		if (string.IsNullOrWhiteSpace(path))
			return string.Empty;

		try
		{
			return PathUtility.Normalize(path);
		}
		catch
		{
			return path.Trim();
		}
	}

	private static string NormalizeRepositoryUrl(string repositoryUrl)
		=> RepositoryUrlUtility.ToSafeDisplay(repositoryUrl);

	private static string TrimGitSuffix(string value)
		=> value.EndsWith(".git", StringComparison.OrdinalIgnoreCase)
			? value[..^4]
			: value;
}
