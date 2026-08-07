using DevProjex.Application.Secrets;

namespace DevProjex.Avalonia.Coordinators;

internal sealed class ProjectTextOutputPipeline(
    TreeExportService treeExport,
    SelectedContentExportService contentExport,
    TreeAndContentExportService treeAndContentExport)
{
    public Task<ProjectTextOutputResult> BuildAsync(
        ProjectTextOutputMode mode,
        ProjectTextOutputSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();

        var effectiveSnapshot = NormalizeSelection(snapshot);
        // Tree rendering and selection projection are CPU-bound and can be large enough
        // to stall Avalonia input. Start the complete operation outside the UI context.
        return Task.Run(
            () => BuildOnWorkerAsync(mode, effectiveSnapshot, cancellationToken),
            cancellationToken);
    }

    private async Task<ProjectTextOutputResult> BuildOnWorkerAsync(
        ProjectTextOutputMode mode,
        ProjectTextOutputSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return mode switch
        {
            ProjectTextOutputMode.Tree => new ProjectTextOutputResult(
                BuildTree(snapshot, cancellationToken),
                CandidateFileCount: 0),
            ProjectTextOutputMode.Content => await BuildContentAsync(snapshot, cancellationToken)
                .ConfigureAwait(false),
            ProjectTextOutputMode.TreeAndContent => new ProjectTextOutputResult(
                await treeAndContentExport.BuildAsync(
                        snapshot.RootPath,
                        snapshot.Root,
                        snapshot.SelectedPaths,
                        snapshot.TreeFormat,
                        cancellationToken,
						snapshot.PathPresentation,
						snapshot.RedactionContext)
                    .ConfigureAwait(false),
                CandidateFileCount: 0),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unsupported project text output mode.")
        };
    }

    private async Task<ProjectTextOutputResult> BuildContentAsync(
        ProjectTextOutputSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var files = ResolveContentFiles(snapshot);
        if (files.Count == 0)
            return new ProjectTextOutputResult(string.Empty, CandidateFileCount: 0);

        var content = await contentExport.BuildAsync(
                files,
                cancellationToken,
				snapshot.PathPresentation?.MapFilePath,
				snapshot.RedactionContext)
            .ConfigureAwait(false);

        return new ProjectTextOutputResult(content, files.Count);
    }

    public string BuildTree(
        ProjectTextOutputSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();

        snapshot = NormalizeSelection(snapshot);
        var displayRootPath = snapshot.PathPresentation?.DisplayRootPath;
        var displayRootName = snapshot.PathPresentation?.DisplayRootName;
        if (snapshot.SelectedPaths.Count == 0)
        {
            return treeExport.BuildFullTree(
                snapshot.RootPath,
                snapshot.Root,
                snapshot.TreeFormat,
                displayRootPath,
                displayRootName);
        }

        var tree = treeExport.BuildSelectedTree(
            snapshot.RootPath,
            snapshot.Root,
            snapshot.SelectedPaths,
            snapshot.TreeFormat,
            displayRootPath,
            displayRootName);

        return string.IsNullOrWhiteSpace(tree)
            ? treeExport.BuildFullTree(
                snapshot.RootPath,
                snapshot.Root,
                snapshot.TreeFormat,
                displayRootPath,
                displayRootName)
            : tree;
    }

    private static IReadOnlyList<string> ResolveContentFiles(ProjectTextOutputSnapshot snapshot)
    {
        snapshot = NormalizeSelection(snapshot);
        if (snapshot.SelectedPaths.Count > 0)
        {
            return ProjectTreeSelectionProjection.BuildOrderedSelectedFilePaths(
                snapshot.Root,
                snapshot.SelectedPaths,
                ensureExists: true);
        }

        return snapshot.OrderedFilePaths ??
               ProjectTreeSelectionProjection.BuildOrderedSelectedFilePaths(
                   snapshot.Root,
                   snapshot.SelectedPaths,
                   ensureExists: false);
    }

    private static ProjectTextOutputSnapshot NormalizeSelection(
        ProjectTextOutputSnapshot snapshot)
    {
        var effectiveSelectedPaths =
            ProjectTreeSelectionProjection.NormalizeSelectedPaths(
                snapshot.Root,
                snapshot.SelectedPaths);
        return ReferenceEquals(
            effectiveSelectedPaths,
            snapshot.SelectedPaths)
            ? snapshot
            : snapshot with { SelectedPaths = effectiveSelectedPaths };
    }
}

internal sealed record ProjectTextOutputSnapshot(
    string RootPath,
    TreeNodeDescriptor Root,
    IReadOnlySet<string> SelectedPaths,
    IReadOnlyList<string>? OrderedFilePaths,
    TreeTextFormat TreeFormat,
    ExportPathPresentation? PathPresentation,
	ContentTransformationContext? RedactionContext = null);

internal sealed record ProjectTextOutputResult(
    string Content,
    int CandidateFileCount);

internal enum ProjectTextOutputMode
{
    Tree,
    Content,
    TreeAndContent
}
