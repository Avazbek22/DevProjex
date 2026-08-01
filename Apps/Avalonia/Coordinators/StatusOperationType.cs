namespace DevProjex.Avalonia.Coordinators;

public enum StatusOperationType
{
    None = 0,
    LoadProject = 1,
    RefreshProject = 2,
    MetricsCalculation = 3,
    GitPullUpdates = 4,
    GitSwitchBranch = 5,
    PreviewBuild = 6,
    SelectionRefresh = 7,
    ApplySettings = 8,
    ProjectCopyExport = 9
}
