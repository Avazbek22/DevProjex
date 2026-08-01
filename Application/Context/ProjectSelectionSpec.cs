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
	/// <summary>
	/// Preserves which resolved components came from explicit/profile intent when a selection
	/// crosses the Desktop JSON boundary. A null value keeps the legacy rule where non-null
	/// components are applied.
	/// </summary>
	public ProjectSelectionApplicationIntent? ApplicationIntent { get; init; }

	// Local profiles carry complete checkbox state, not merely the currently checked names.
	// This internal payload lets every presentation surface apply the same rule: known rows
	// keep their saved state, while rows discovered after the save use current defaults.
	internal LocalProjectSelectionState? LocalProfileState { get; init; }

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

public enum ProjectSelectionApplicationMode
{
	Preserve,
	ApplyResolvedValue,
	ResetToDefaults
}

public sealed record ProjectSelectionApplicationIntent(
	ProjectSelectionApplicationMode Roots,
	ProjectSelectionApplicationMode Extensions,
	ProjectSelectionApplicationMode GitMode,
	ProjectSelectionApplicationMode Exclusions);

internal sealed record LocalProjectSelectionState(
	ProjectSelectionProfile Profile,
	bool RootsOverridden = false,
	bool ExtensionsOverridden = false,
	bool IgnoreOptionsOverridden = false);
