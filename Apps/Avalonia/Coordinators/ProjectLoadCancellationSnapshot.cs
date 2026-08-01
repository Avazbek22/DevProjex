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
    bool AllRootFoldersChecked,
    bool AllExtensionsChecked,
    bool AllIgnoreChecked,
    bool HasCompleteMetricsBaseline,
    IReadOnlyList<SelectionOptionSnapshot> RootFolders,
    IReadOnlyList<SelectionOptionSnapshot> Extensions,
    IReadOnlyList<IgnoreOptionSnapshot> IgnoreOptions)
{
    // Internal coordinator state augments, but does not replace, the existing public snapshot
    // contract. External callers can continue constructing and observing the legacy shape.
    internal SelectionSyncCoordinator.ProjectCheckpoint? SelectionCheckpoint { get; init; }
}
