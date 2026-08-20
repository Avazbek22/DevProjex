using DevProjex.Avalonia.Services;

namespace DevProjex.Avalonia.Coordinators;

public sealed record ProjectLoadCancellationSnapshot(
    bool HadLoadedProjectBefore,
    string? Path,
    string? ProjectDisplayName,
    string? RepositoryUrl,
    BuildTreeResult? Tree,
    ProjectSourceType ProjectSourceType,
    string CurrentBranch,
    IReadOnlyList<GitBranch> GitBranches,
    bool SettingsVisible,
    bool SearchVisible,
    bool FilterVisible,
    PreviewWorkspaceMode PreviewWorkspaceMode,
    bool StatusMetricsVisible,
    string StatusTreeStatsText,
    string StatusContentStatsText,
    bool AllExtensionsChecked,
    bool AllIgnoreChecked,
    bool HasCompleteMetricsBaseline,
    IReadOnlyList<SelectionOptionSnapshot> Extensions,
    IReadOnlyList<IgnoreOptionSnapshot> IgnoreOptions)
{
    // Internal coordinator state augments, but does not replace, the existing public snapshot
    // contract. External callers can continue constructing and observing the legacy shape.
    internal SelectionSyncCoordinator.ProjectCheckpoint? SelectionCheckpoint { get; init; }
	internal ProjectRuntimeStateSnapshot RuntimeState { get; init; } = ProjectRuntimeStateSnapshot.Cleared;
	internal ProjectTreeSelectionSnapshot? TreeSelection { get; init; }
	internal ProjectTreeExpansionSnapshot? TreeExpansion { get; init; }
	internal string SearchQuery { get; init; } = string.Empty;
	internal string NameFilter { get; init; } = string.Empty;
	internal bool PreviewSearchVisible { get; init; }
	internal string PreviewSearchQuery { get; init; } = string.Empty;
}

internal sealed record ProjectRuntimeStateSnapshot(
	bool HideSecretsApplied,
	bool HidePrivateDataApplied,
	bool CompressCodeApplied,
	bool StripCommentsApplied,
	bool StripBlankLinesApplied,
	int? SecretRedactedCount,
	int? SecretDetectedCount,
	int? PrivateDataRedactedCount,
	int? PrivateDataDetectedCount,
	SecretScanState SecretScanState,
	CodeCompressionSnapshot? CompressionSnapshot)
{
	public static ProjectRuntimeStateSnapshot Cleared { get; } = new(
		HideSecretsApplied: false,
		HidePrivateDataApplied: false,
		CompressCodeApplied: false,
		StripCommentsApplied: false,
		StripBlankLinesApplied: false,
		SecretRedactedCount: null,
		SecretDetectedCount: null,
		PrivateDataRedactedCount: null,
		PrivateDataDetectedCount: null,
		SecretScanState.Disabled,
		CompressionSnapshot: null);
}
