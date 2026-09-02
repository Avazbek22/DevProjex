using DevProjex.Avalonia.Services;

namespace DevProjex.Avalonia.Coordinators;

internal sealed class ProjectLoadSnapshotPipeline(IProjectLoadSnapshotPipelineHost host)
{
    public async Task<bool> ReloadAsync(
        string currentPath,
        bool preserveTreeState,
		PersistentSecretMarksSnapshot? persistentMarks,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(currentPath))
            return false;

        cancellationToken.ThrowIfCancellationRequested();

        using var _ = PerformanceMetrics.Measure("ProjectLoadSnapshotPipeline.ReloadAsync");

        var selectionSnapshot = await host.BuildSelectionSnapshotAsync(currentPath, cancellationToken);
        if (selectionSnapshot is null)
            return false;

        cancellationToken.ThrowIfCancellationRequested();
        if (host.TryHandleSelectionRootAccessDenied(currentPath, selectionSnapshot))
            return false;

        var treeInput = host.CreateTreeRefreshInput(
            currentPath,
            selectionSnapshot,
            preserveTreeState);
        host.BeforeProjectLoadTreeRefresh();

        BuildTreeSnapshotResult treeBuild;
        using (PerformanceMetrics.Measure("ProjectLoadSnapshotPipeline.BuildTree"))
        {
            // Match RefreshTreePipeline semantics: heavy work runs in the background,
            // while the continuation returns to the caller context so UI state is applied
            // only from the UI thread.
            treeBuild = await Task.Run(
                () => host.BuildTree(treeInput, cancellationToken),
                cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
		if (host.TryHandleGitScopeDiagnostics(treeBuild))
			return false;
        if (host.TryHandleTreeRootAccessDenied(treeInput, treeBuild.Tree))
            return false;

		if (treeBuild.Tree.HadScanFailure)
		{
			if (!selectionSnapshot.HadScanFailure)
				host.ReportIncompleteTreeScan();
			treeBuild = treeBuild with { Inventory = null };
		}

        TreeNodeViewModel treeRoot;
        using (PerformanceMetrics.Measure("ProjectLoadSnapshotPipeline.BuildTreeViewModel"))
        {
            treeRoot = await Task.Run(
				() => host.BuildTreeViewModel(
					treeInput,
					treeBuild.Tree,
					cancellationToken),
                cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
		return host.ApplyProjectLoadSnapshot(
			new ProjectLoadSnapshot(
				selectionSnapshot,
				treeInput,
				treeBuild.Tree,
				treeBuild.Inventory,
				treeBuild.GitScopePresentation,
				treeRoot,
				persistentMarks),
            cancellationToken);
    }
}
