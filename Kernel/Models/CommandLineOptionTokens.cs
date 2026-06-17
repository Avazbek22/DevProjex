namespace DevProjex.Kernel.Models;

// Shared CLI token registry keeps parser, help content, and contract tests from drifting apart.
public static class CommandLineOptionTokens
{
	public const string Path = "--path";
	public const string Language = "--lang";
	public const string Report = "--report";
	public const string ReportPath = "--report-path";
	public const string ReportFormat = "--report-format";
	public const string IncludeRoot = "--include-root";
	public const string IncludeExtension = "--include-extension";
	public const string Ignore = "--ignore";
	public const string NoUi = "--no-ui";
	public const string Silent = "--silent";
	public const string Version = "--version";
	public const string Help = "--help";
	public const string ShortHelp = "-h";
	public const string WindowsHelp = "/?";
	public const string ElevationAttempted = "--elevation-attempted";
	public const string LegacyElevationAttempted = "--elevationAttempted";

	public const string IgnoreNone = "none";
	public const string IgnoreSmartIgnore = "smart-ignore";
	public const string IgnoreGitIgnore = "git-ignore";
	public const string IgnoreHiddenFolders = "hidden-folders";
	public const string IgnoreHiddenFiles = "hidden-files";
	public const string IgnoreDotFolders = "dot-folders";
	public const string IgnoreDotFiles = "dot-files";
	public const string IgnoreEmptyFolders = "empty-folders";
	public const string IgnoreEmptyFiles = "empty-files";
	public const string IgnoreExtensionlessFiles = "extensionless-files";

	public static IReadOnlyList<string> PublicHelpTokens { get; } =
	[
		Path,
		Language,
		Report,
		ReportPath,
		ReportFormat,
		IncludeRoot,
		IncludeExtension,
		Ignore,
		NoUi,
		Silent,
		Version,
		Help,
		ShortHelp,
		WindowsHelp
	];

	public static IReadOnlyList<string> InternalRelaunchTokens { get; } =
	[
		ElevationAttempted,
		LegacyElevationAttempted
	];

	public static IReadOnlyList<string> PublicIgnoreOptionNames { get; } =
	[
		IgnoreSmartIgnore,
		IgnoreGitIgnore,
		IgnoreHiddenFolders,
		IgnoreHiddenFiles,
		IgnoreDotFolders,
		IgnoreDotFiles,
		IgnoreEmptyFolders,
		IgnoreEmptyFiles,
		IgnoreExtensionlessFiles,
		IgnoreNone
	];
}
