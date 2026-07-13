namespace DevProjex.Kernel.Models;

/// <summary>
/// Per-root scan products retained only for an immediate selection projection.
/// </summary>
public sealed record ProjectWorkspaceRootScanSnapshot(
	IgnoreSectionScanData IgnoreSection,
	IgnoreOptionCounts DirectoryToggleProbeCounts,
	IgnoreControllerImpactCounts ControllerImpactProbeCounts,
	bool RootAccessDenied,
	bool HadAccessDenied);

/// <summary>
/// Decomposed workspace totals used to remove projected roots without filesystem IO.
/// </summary>
public sealed record ProjectWorkspaceScanBreakdown(
	IgnoreSectionScanData RootFiles,
	IReadOnlyDictionary<string, ProjectWorkspaceRootScanSnapshot> SelectedRoots,
	IgnoreOptionCounts UnselectedDirectoryToggleProbeCounts,
	IgnoreControllerImpactCounts UnselectedControllerImpactProbeCounts,
	bool IncludesDirectoryToggleProbeRoots,
	bool IncludesControllerImpactProbeRoots,
	bool RootEnumerationAccessDenied,
	bool RootEnumerationHadAccessDenied,
	bool RootFilesAccessDenied,
	bool RootFilesHadAccessDenied);
