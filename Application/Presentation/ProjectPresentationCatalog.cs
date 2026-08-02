namespace DevProjex.Application.Presentation;

public sealed record GitFilteringDescriptor(
	GitFilteringMode Id,
	IgnoreOptionId? LegacyOptionId,
	string Token,
	string LabelKey,
	int Order);

public sealed record ProjectExclusionDescriptor(
	ProjectExclusion Id,
	IgnoreOptionId LegacyOptionId,
	string Token,
	string LabelKey,
	int Order);

public sealed record ProjectContextViewDescriptor(
	ProjectContextView Id,
	string Token,
	string LabelKey,
	int Order);

public sealed record ProjectContextFormatDescriptor(
	ProjectContextDocumentFormat Id,
	string Token,
	string UserLabel,
	int Order);

/// <summary>
/// Defines stable IDs, CLI tokens, localization keys, and display order shared by
/// Desktop, Terminal, and command presentation adapters.
/// </summary>
public static class ProjectPresentationCatalog
{
	public const string NoExclusionsToken = "none";

	public static IReadOnlyList<GitFilteringDescriptor> GitFiltering { get; } =
	[
		new(GitFilteringMode.None, null, "none", "Terminal.Tui.GitNone", 0),
		new(
			GitFilteringMode.RespectGitIgnore,
			IgnoreOptionId.UseGitIgnore,
			"gitignore",
			"Settings.Ignore.UseGitIgnore",
			1),
		new(
			GitFilteringMode.TrackedFilesOnly,
			IgnoreOptionId.TrackedGitFilesOnly,
			"tracked",
			"Settings.Ignore.TrackedGitFilesOnly",
			2)
	];

	public static IReadOnlyList<ProjectExclusionDescriptor> Exclusions { get; } =
	[
		new(
			ProjectExclusion.SmartIgnore,
			IgnoreOptionId.SmartIgnore,
			"smart-ignore",
			"Settings.Ignore.SmartIgnore",
			0),
		new(
			ProjectExclusion.EmptyFolders,
			IgnoreOptionId.EmptyFolders,
			"empty-folders",
			"Settings.Ignore.EmptyFolders",
			1),
		new(
			ProjectExclusion.EmptyFiles,
			IgnoreOptionId.EmptyFiles,
			"empty-files",
			"Settings.Ignore.EmptyFiles",
			2),
		new(
			ProjectExclusion.HiddenFolders,
			IgnoreOptionId.HiddenFolders,
			"hidden-folders",
			"Settings.Ignore.HiddenFolders",
			3),
		new(
			ProjectExclusion.HiddenFiles,
			IgnoreOptionId.HiddenFiles,
			"hidden-files",
			"Settings.Ignore.HiddenFiles",
			4),
		new(
			ProjectExclusion.DotFolders,
			IgnoreOptionId.DotFolders,
			"dot-folders",
			"Settings.Ignore.DotFolders",
			5),
		new(
			ProjectExclusion.DotFiles,
			IgnoreOptionId.DotFiles,
			"dot-files",
			"Settings.Ignore.DotFiles",
			6),
		new(
			ProjectExclusion.ExtensionlessFiles,
			IgnoreOptionId.ExtensionlessFiles,
			"extensionless-files",
			"Settings.Ignore.ExtensionlessFiles",
			7)
	];

	public static IReadOnlyList<ProjectContextViewDescriptor> PreviewModes { get; } =
	[
		new(ProjectContextView.Tree, "tree", "Preview.Mode.Tree", 0),
		new(ProjectContextView.Content, "content", "Preview.Mode.Content", 1),
		new(ProjectContextView.TreeContent, "tree-content", "Preview.Mode.TreeAndContent", 2)
	];

	public static IReadOnlyList<ProjectContextFormatDescriptor> Formats { get; } =
	[
		new(ProjectContextDocumentFormat.Text, "text", "ASCII", 0),
		new(ProjectContextDocumentFormat.Json, "json", "JSON", 1),
		new(ProjectContextDocumentFormat.Xml, "xml", "XML", 2),
		new(ProjectContextDocumentFormat.Markdown, "markdown", "Markdown", 3)
	];

	public static ProjectExclusionDescriptor Get(ProjectExclusion exclusion) =>
		Exclusions.FirstOrDefault(descriptor => descriptor.Id == exclusion) ??
		throw new ArgumentOutOfRangeException(nameof(exclusion), exclusion, null);

	public static GitFilteringDescriptor Get(GitFilteringMode mode) =>
		GitFiltering.FirstOrDefault(descriptor => descriptor.Id == mode) ??
		throw new ArgumentOutOfRangeException(nameof(mode), mode, null);

	public static ProjectContextViewDescriptor Get(ProjectContextView view) =>
		PreviewModes.FirstOrDefault(descriptor => descriptor.Id == view) ??
		throw new ArgumentOutOfRangeException(nameof(view), view, null);

	public static ProjectContextFormatDescriptor Get(ProjectContextDocumentFormat format) =>
		Formats.FirstOrDefault(descriptor => descriptor.Id == format) ??
		throw new ArgumentOutOfRangeException(nameof(format), format, null);
}
