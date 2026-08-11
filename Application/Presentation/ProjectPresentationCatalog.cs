namespace DevProjex.Application.Presentation;

public sealed record GitFilteringDescriptor(
	GitFilteringMode Id,
	IgnoreOptionId? LegacyOptionId,
	string Token,
	string LabelKey,
	int Order);

/// <summary>
/// A row in the ignore/content-processing panel. <see cref="Id"/> is null for transformations that
/// never existed as a v5 --exclude token: compression does not exclude anything, it transforms, and
/// giving it an --exclude alias would freeze that confusion into the CLI contract.
/// </summary>
public sealed record ProjectExclusionDescriptor(
	ProjectExclusion? Id,
	IgnoreOptionId LegacyOptionId,
	string Token,
	string LabelKey,
	int Order)
{
	/// <summary>
	/// The v5 --exclude id. Path exclusions always have one; call this only where the descriptor
	/// is known to come from <see cref="ProjectPresentationCatalog.Exclusions"/>.
	/// </summary>
	public ProjectExclusion RequireId() =>
		Id ?? throw new InvalidOperationException($"'{Token}' is a content transformation and has no exclusion id.");
}

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
			2),
		new(
			ProjectExclusion.EmptyFiles,
			IgnoreOptionId.EmptyFiles,
			"empty-files",
			"Settings.Ignore.EmptyFiles",
			3),
		new(
			ProjectExclusion.HiddenFolders,
			IgnoreOptionId.HiddenFolders,
			"hidden-folders",
			"Settings.Ignore.HiddenFolders",
			4),
		new(
			ProjectExclusion.HiddenFiles,
			IgnoreOptionId.HiddenFiles,
			"hidden-files",
			"Settings.Ignore.HiddenFiles",
			5),
		new(
			ProjectExclusion.DotFolders,
			IgnoreOptionId.DotFolders,
			"dot-folders",
			"Settings.Ignore.DotFolders",
			6),
		new(
			ProjectExclusion.DotFiles,
			IgnoreOptionId.DotFiles,
			"dot-files",
			"Settings.Ignore.DotFiles",
			7),
		new(
			ProjectExclusion.ExtensionlessFiles,
			IgnoreOptionId.ExtensionlessFiles,
			"extensionless-files",
			"Settings.Ignore.ExtensionlessFiles",
			8)
	];

	/// <summary>
	/// Content transformations operate on selected bytes after path filtering. Keeping them out
	/// of <see cref="Exclusions"/> prevents UI and cache consumers from treating them as tree rules.
	/// </summary>
	public static IReadOnlyList<ProjectExclusionDescriptor> ContentTransformations { get; } =
	[
		new(
			ProjectExclusion.HideSecrets,
			IgnoreOptionId.HideSecrets,
			"hide-secrets",
			"Settings.Ignore.HideSecrets",
			0),
		new(
			null,
			IgnoreOptionId.CompressCode,
			"compress-code",
			"Settings.Ignore.CompressCode",
			1),
		new(
			null,
			IgnoreOptionId.StripComments,
			"strip-comments",
			"Settings.Ignore.StripComments",
			2)
	];

	/// <summary>
	/// Single source of truth for "is this row a content transformation". Everything that used to
	/// compare against HideSecrets by name reads this instead, so a second transformation cannot be
	/// half-registered.
	/// </summary>
	public static IReadOnlySet<IgnoreOptionId> ContentTransformationOptionIds { get; } =
		ContentTransformations.Select(static descriptor => descriptor.LegacyOptionId).ToHashSet();

	/// <summary>
	/// Preserves parsing of v5 --exclude tokens while new command surfaces expose transformations
	/// through dedicated additive options.
	/// </summary>
	public static IReadOnlyList<ProjectExclusionDescriptor> LegacyExclusionChoices { get; } =
		[.. Exclusions, .. ContentTransformations.Where(static descriptor => descriptor.Id is not null)];

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
		LegacyExclusionChoices.FirstOrDefault(descriptor => descriptor.Id == exclusion) ??
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
