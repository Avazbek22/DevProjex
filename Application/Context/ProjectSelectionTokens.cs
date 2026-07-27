namespace DevProjex.Application.Context;

public static class ProjectSelectionTokens
{
	public static IReadOnlyList<string> GitModes { get; } =
		["none", "gitignore", "tracked"];

	public static IReadOnlyList<string> Exclusions { get; } =
	[
		"smart-ignore",
		"hidden-folders",
		"hidden-files",
		"dot-folders",
		"dot-files",
		"empty-folders",
		"empty-files",
		"extensionless-files"
	];

	public static bool TryParseGitMode(string? value, out GitFilteringMode mode)
	{
		mode = value?.ToLowerInvariant() switch
		{
			"none" => GitFilteringMode.None,
			"gitignore" => GitFilteringMode.RespectGitIgnore,
			"tracked" => GitFilteringMode.TrackedFilesOnly,
			_ => (GitFilteringMode)(-1)
		};
		return Enum.IsDefined(mode);
	}

	public static string ToToken(GitFilteringMode mode) =>
		mode switch
		{
			GitFilteringMode.RespectGitIgnore => "gitignore",
			GitFilteringMode.TrackedFilesOnly => "tracked",
			_ => "none"
		};

	public static bool TryParseExclusion(string? value, out ProjectExclusion exclusion)
	{
		exclusion = value?.ToLowerInvariant() switch
		{
			"smart-ignore" => ProjectExclusion.SmartIgnore,
			"hidden-folders" => ProjectExclusion.HiddenFolders,
			"hidden-files" => ProjectExclusion.HiddenFiles,
			"dot-folders" => ProjectExclusion.DotFolders,
			"dot-files" => ProjectExclusion.DotFiles,
			"empty-folders" => ProjectExclusion.EmptyFolders,
			"empty-files" => ProjectExclusion.EmptyFiles,
			"extensionless-files" => ProjectExclusion.ExtensionlessFiles,
			_ => (ProjectExclusion)(-1)
		};
		return Enum.IsDefined(exclusion);
	}

	public static string ToToken(ProjectExclusion exclusion) =>
		exclusion switch
		{
			ProjectExclusion.SmartIgnore => "smart-ignore",
			ProjectExclusion.HiddenFolders => "hidden-folders",
			ProjectExclusion.HiddenFiles => "hidden-files",
			ProjectExclusion.DotFolders => "dot-folders",
			ProjectExclusion.DotFiles => "dot-files",
			ProjectExclusion.EmptyFolders => "empty-folders",
			ProjectExclusion.EmptyFiles => "empty-files",
			ProjectExclusion.ExtensionlessFiles => "extensionless-files",
			_ => throw new ArgumentOutOfRangeException(nameof(exclusion), exclusion, null)
		};
}
