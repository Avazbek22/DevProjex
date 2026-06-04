namespace DevProjex.Avalonia.Coordinators;

internal interface IRefreshTreePipelineHost
{
    MainWindowViewModel ViewModel { get; }

    TreeRefreshInput? CaptureTreeRefreshInput();

    void BeforeFullTreeRefresh();

    bool TryBuildInteractiveFilteredTreeResult(
        string? nameFilter,
        CancellationToken cancellationToken,
        out BuildTreeResult result);

    BuildTreeResult BuildTree(TreeRefreshInput input, CancellationToken cancellationToken);

    bool TryHandleRootAccessDenied(TreeRefreshInput input, BuildTreeResult result);

    TreeNodeViewModel BuildTreeViewModel(TreeRefreshInput input, BuildTreeResult result);

    void ApplyTreeRefreshResult(
        TreeRefreshInput input,
        BuildTreeResult result,
        TreeNodeViewModel root,
        bool interactiveFilter,
        bool usedInMemoryFilter,
        CancellationToken cancellationToken);
}
