namespace DevProjex.Avalonia.Coordinators;

internal interface IProjectLoadSnapshotPipelineHost
{
    Task<SelectionRefreshSnapshot?> BuildSelectionSnapshotAsync(
        string currentPath,
        CancellationToken cancellationToken);

    bool TryHandleSelectionRootAccessDenied(
        string currentPath,
        SelectionRefreshSnapshot snapshot);

    TreeRefreshInput CreateTreeRefreshInput(
        string currentPath,
        SelectionRefreshSnapshot selectionSnapshot);

    void BeforeProjectLoadTreeRefresh();

    BuildTreeResult BuildTree(TreeRefreshInput input, CancellationToken cancellationToken);

    bool TryHandleTreeRootAccessDenied(TreeRefreshInput input, BuildTreeResult result);

    TreeNodeViewModel BuildTreeViewModel(TreeRefreshInput input, BuildTreeResult result);

    void ApplyProjectLoadSnapshot(ProjectLoadSnapshot snapshot, CancellationToken cancellationToken);
}
