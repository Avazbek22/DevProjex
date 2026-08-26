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
        SelectionRefreshSnapshot selectionSnapshot,
        bool preserveTreeState);

    void BeforeProjectLoadTreeRefresh();

    BuildTreeSnapshotResult BuildTree(TreeRefreshInput input, CancellationToken cancellationToken);

    bool TryHandleTreeRootAccessDenied(TreeRefreshInput input, BuildTreeResult result);

	void ReportIncompleteTreeScan();

	TreeNodeViewModel BuildTreeViewModel(
		TreeRefreshInput input,
		BuildTreeResult result,
		CancellationToken cancellationToken);

    bool ApplyProjectLoadSnapshot(ProjectLoadSnapshot snapshot, CancellationToken cancellationToken);
}
