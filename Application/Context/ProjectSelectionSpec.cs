using System.Text.Json.Serialization;

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
	ExtensionlessFiles,
	HideSecrets
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
	bool? HideSecrets = null,
	bool? CompressCode = null,
	bool? StripComments = null,
	bool? StripBlankLines = null,
	ProjectProfileReference? ProfileSource = null,
	bool? HidePrivateData = null,
	string? GitDiffRange = null)
{
	/// <summary>
	/// Preserves which resolved components came from explicit/profile intent when a selection
	/// crosses the Desktop JSON boundary. A null value keeps the legacy rule where non-null
	/// components are applied.
	/// </summary>
	public ProjectSelectionApplicationIntent? ApplicationIntent { get; init; }

	/// <summary>
	/// The content transformations the resolved profile mandates on its own, before any call-level
	/// toggle. The three booleans above hold profile OR call, and once merged the profile's share
	/// cannot be recovered from them - which matters because a per-file detail override back to
	/// <c>full</c> must add nothing while still never removing what the profile requires.
	///
	/// Null on a specification that never crossed the selection resolver. Excluded from JSON: this
	/// is an in-process resolution detail, not part of the desktop request shape.
	/// </summary>
	[JsonIgnore]
	public CodeTransformKinds? ProfileContentKinds { get; init; }

	/// <summary>
	/// Ordered per-file detail overrides, when the call asked for a mix. Null means one detail level
	/// applies to the whole selection, which is the historical behaviour and stays byte-for-byte
	/// identical. Only the overrides live here; each surface unions them with its own call level
	/// through <see cref="ContentDetailSelection"/>, so the two cannot drift apart.
	/// Excluded from JSON for the same reason as <see cref="ProfileContentKinds"/>.
	/// </summary>
	[JsonIgnore]
	public IReadOnlyList<ContentDetailOverride>? ContentDetailOverrides { get; init; }

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
		HideSecrets: false,
		HidePrivateData: false,
		CompressCode: false,
		StripComments: false,
		StripBlankLines: false,
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
	ProjectSelectionApplicationMode Exclusions,
	ProjectSelectionApplicationMode HideSecrets = ProjectSelectionApplicationMode.Preserve,
	ProjectSelectionApplicationMode CompressCode = ProjectSelectionApplicationMode.Preserve,
	ProjectSelectionApplicationMode StripComments = ProjectSelectionApplicationMode.Preserve,
	ProjectSelectionApplicationMode StripBlankLines = ProjectSelectionApplicationMode.Preserve,
	ProjectSelectionApplicationMode HidePrivateData = ProjectSelectionApplicationMode.Preserve);

internal sealed record LocalProjectSelectionState(
	ProjectSelectionProfile Profile,
	bool RootsOverridden = false,
	bool ExtensionsOverridden = false,
	bool IgnoreOptionsOverridden = false);
