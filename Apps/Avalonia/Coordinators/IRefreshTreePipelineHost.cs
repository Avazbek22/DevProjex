using DevProjex.Avalonia.Services;

namespace DevProjex.Avalonia.Coordinators;

internal interface IRefreshTreePipelineHost
{
    MainWindowViewModel ViewModel { get; }

    TreeRefreshInput? CaptureTreeRefreshInput(bool preserveCheckedPaths);

    void BeforeFullTreeRefresh(bool preserveStatusMetrics = false);

    void BeforeInteractiveFilterRefresh();

    BuildTreeSnapshotResult BuildTree(TreeRefreshInput input, CancellationToken cancellationToken);

    bool TryHandleRootAccessDenied(TreeRefreshInput input, BuildTreeResult result);

	void ReportIncompleteTreeScan();

    TreeNodeViewModel BuildTreeViewModel(TreeRefreshInput input, BuildTreeResult result);

    bool IsTreeRefreshInputCurrent(TreeRefreshInput input);

    void ApplyTreeRefreshResult(
        TreeRefreshInput input,
        BuildTreeSnapshotResult result,
        TreeNodeViewModel root,
        bool interactiveFilter,
        bool usedInMemoryFilter,
        MemoryCleanupReason? postLoadCleanupReason,
        CancellationToken cancellationToken);
}
