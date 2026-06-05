using DevProjex.Avalonia.Services;

namespace DevProjex.Avalonia.Coordinators;

internal sealed class ProjectLoadSnapshotPipeline(IProjectLoadSnapshotPipelineHost host)
{
    public async Task ReloadAsync(string currentPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(currentPath))
            return;

        cancellationToken.ThrowIfCancellationRequested();

        using var _ = PerformanceMetrics.Measure("ProjectLoadSnapshotPipeline.ReloadAsync");

        var selectionSnapshot = await host.BuildSelectionSnapshotAsync(currentPath, cancellationToken);
        if (selectionSnapshot is null)
            return;

        cancellationToken.ThrowIfCancellationRequested();
        if (host.TryHandleSelectionRootAccessDenied(currentPath, selectionSnapshot))
            return;

        var treeInput = host.CreateTreeRefreshInput(currentPath, selectionSnapshot);
        host.BeforeProjectLoadTreeRefresh();

        BuildTreeResult treeResult;
        using (PerformanceMetrics.Measure("ProjectLoadSnapshotPipeline.BuildTree"))
        {
            // Match RefreshTreePipeline semantics: heavy work runs in the background,
            // while the continuation returns to the caller context so UI state is applied
            // only from the UI thread.
            treeResult = await Task.Run(
                () => host.BuildTree(treeInput, cancellationToken),
                cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (host.TryHandleTreeRootAccessDenied(treeInput, treeResult))
            return;

        TreeNodeViewModel treeRoot;
        using (PerformanceMetrics.Measure("ProjectLoadSnapshotPipeline.BuildTreeViewModel"))
        {
            treeRoot = await Task.Run(
                () => host.BuildTreeViewModel(treeInput, treeResult),
                cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        host.ApplyProjectLoadSnapshot(
            new ProjectLoadSnapshot(selectionSnapshot, treeInput, treeResult, treeRoot),
            cancellationToken);
    }
}
