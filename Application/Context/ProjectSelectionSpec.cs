namespace DevProjex.Application.Context;

public enum ProjectExclusion
{
	SmartIgnore,
	HiddenFolders,
	HiddenFiles,
	DotFolders,
	DotFiles,
	EmptyFolders,
	EmptyFiles,
	ExtensionlessFiles
}

public enum ProjectProfileSourceKind
{
	Standard,
	Local,
	Portable
}

public sealed record ProjectProfileReference(
	ProjectProfileSourceKind Kind,
	string? Path = null)
{
	public static ProjectProfileReference Standard { get; } = new(ProjectProfileSourceKind.Standard);
	public static ProjectProfileReference Local { get; } = new(ProjectProfileSourceKind.Local);
}

/// <summary>
/// Describes user selection intent independently from GUI checkbox implementation.
/// Null collections inherit the selected profile; empty collections are explicit empty sets.
/// </summary>
public sealed record ProjectSelectionSpec(
	IReadOnlyCollection<string>? Roots = null,
	IReadOnlyCollection<string>? Extensions = null,
	IReadOnlyCollection<string>? SelectedPaths = null,
	GitFilteringMode? GitMode = null,
	IReadOnlyCollection<ProjectExclusion>? Exclusions = null,
	ProjectProfileReference? ProfileSource = null)
{
	public static IReadOnlyCollection<ProjectExclusion> StandardExclusions { get; } =
	[
		ProjectExclusion.SmartIgnore,
		ProjectExclusion.HiddenFolders,
		ProjectExclusion.HiddenFiles,
		ProjectExclusion.DotFolders,
		ProjectExclusion.DotFiles,
		ProjectExclusion.EmptyFolders,
		ProjectExclusion.EmptyFiles,
		ProjectExclusion.ExtensionlessFiles
	];

	public static ProjectSelectionSpec Standard { get; } = new(
		GitMode: GitFilteringMode.RespectGitIgnore,
		Exclusions: StandardExclusions,
		ProfileSource: ProjectProfileReference.Standard);
}
