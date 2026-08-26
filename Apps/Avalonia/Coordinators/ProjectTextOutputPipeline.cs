namespace DevProjex.Avalonia.Coordinators;

internal sealed class ProjectTextOutputPipeline(
    TreeExportService treeExport,
    SelectedContentExportService contentExport,
	TreeAndContentExportService treeAndContentExport,
	PreviewDocumentBuilder previewDocumentBuilder,
	TextFileExportService textFileExport)
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

	public Task<ProjectTextDocumentOutputResult> BuildDocumentAsync(
		ProjectTextOutputMode mode,
		ProjectTextOutputSnapshot snapshot,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		cancellationToken.ThrowIfCancellationRequested();
		var effectiveSnapshot = NormalizeSelection(snapshot);
		return Task.Run(
			() => BuildDocumentOnWorkerAsync(mode, effectiveSnapshot, cancellationToken),
			cancellationToken);
	}

	private async Task<ProjectTextDocumentOutputResult> BuildDocumentOnWorkerAsync(
		ProjectTextOutputMode mode,
		ProjectTextOutputSnapshot snapshot,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		var outputPathRedaction = OutputRootPathPresentation.CaptureRedactionDecision(
			snapshot.RedactionContext);
		if (mode == ProjectTextOutputMode.Tree)
		{
			return new ProjectTextDocumentOutputResult(
				previewDocumentBuilder.CreateDocument(
					BuildTree(snapshot, cancellationToken, outputPathRedaction)),
				CandidateFileCount: 0);
		}

		if (mode is not (ProjectTextOutputMode.Content or ProjectTextOutputMode.TreeAndContent))
			throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unsupported project text output mode.");

		var files = ResolveContentFiles(snapshot);
		var contentDocument = await BuildContentDocumentAsync(
			files,
			snapshot,
			mode == ProjectTextOutputMode.Content
				? snapshot.PathPresentation?.MapFilePath
				: TreeAndContentExportService.CreateRelativeContentHeaderPathMapper(
					snapshot.RootPath),
			outputPathRedaction,
			cancellationToken).ConfigureAwait(false);
		if (mode == ProjectTextOutputMode.Content)
		{
			return new ProjectTextDocumentOutputResult(
				contentDocument,
				files.Count);
		}

		using (contentDocument)
		{
			var tree = BuildTree(snapshot, cancellationToken, outputPathRedaction);
			if (contentDocument.CharacterCount == 0)
			{
				return new ProjectTextDocumentOutputResult(
					previewDocumentBuilder.CreateDocument(tree),
					CandidateFileCount: 0);
			}

			var trimmedTree = tree.TrimEnd('\r', '\n');
			var separator = string.Concat(
				Environment.NewLine,
				"\u00A0",
				Environment.NewLine,
				"\u00A0",
				Environment.NewLine);
			var combined = await previewDocumentBuilder.CreateDocumentAsync(
				async (stream, writeCancellationToken) =>
				{
					await textFileExport.AppendAsync(
						stream,
						trimmedTree,
						writeCancellationToken).ConfigureAwait(false);
					await textFileExport.AppendAsync(
						stream,
						separator,
						writeCancellationToken).ConfigureAwait(false);
					await contentDocument
						.WriteToAsync(stream, writeCancellationToken)
						.ConfigureAwait(false);
				},
				cancellationToken).ConfigureAwait(false);
			return new ProjectTextDocumentOutputResult(combined, CandidateFileCount: 0);
		}
	}

	private async Task<IPreviewTextDocument> BuildContentDocumentAsync(
		IReadOnlyList<string> files,
		ProjectTextOutputSnapshot snapshot,
		Func<string, string>? displayPathMapper,
		OutputPathRedactionDecision? outputPathRedaction,
		CancellationToken cancellationToken)
	{
		if (files.Count == 0)
			return previewDocumentBuilder.CreateInMemory(string.Empty);

		return await previewDocumentBuilder.CreateDocumentAsync(
			(stream, writeCancellationToken) => contentExport.WriteAsync(
				stream,
				files,
				writeCancellationToken,
				displayPathMapper,
				snapshot.RedactionContext,
				displayRootPath: null,
				outputPathRedaction),
			cancellationToken).ConfigureAwait(false);
	}

    private async Task<ProjectTextOutputResult> BuildOnWorkerAsync(
        ProjectTextOutputMode mode,
        ProjectTextOutputSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
		var outputPathRedaction = OutputRootPathPresentation.CaptureRedactionDecision(
			snapshot.RedactionContext);

        return mode switch
        {
            ProjectTextOutputMode.Tree => new ProjectTextOutputResult(
				BuildTree(snapshot, cancellationToken, outputPathRedaction),
                CandidateFileCount: 0),
			ProjectTextOutputMode.Content => await BuildContentAsync(
				snapshot,
				outputPathRedaction,
				cancellationToken)
                .ConfigureAwait(false),
            ProjectTextOutputMode.TreeAndContent => new ProjectTextOutputResult(
                await treeAndContentExport.BuildAsync(
                        snapshot.RootPath,
                        snapshot.Root,
                        snapshot.SelectedPaths,
                        snapshot.TreeFormat,
                        cancellationToken,
						snapshot.PathPresentation,
						snapshot.RedactionContext,
						outputPathRedaction)
                    .ConfigureAwait(false),
                CandidateFileCount: 0),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unsupported project text output mode.")
        };
    }

    private async Task<ProjectTextOutputResult> BuildContentAsync(
        ProjectTextOutputSnapshot snapshot,
		OutputPathRedactionDecision? outputPathRedaction,
        CancellationToken cancellationToken)
    {
        var files = ResolveContentFiles(snapshot);
        if (files.Count == 0)
            return new ProjectTextOutputResult(string.Empty, CandidateFileCount: 0);

		var content = await contentExport.BuildAsync(
				files,
				cancellationToken,
				snapshot.PathPresentation?.MapFilePath,
				snapshot.RedactionContext,
				displayRootPath: null,
				outputPathRedaction: outputPathRedaction)
            .ConfigureAwait(false);

        return new ProjectTextOutputResult(content, files.Count);
    }

    public string BuildTree(
        ProjectTextOutputSnapshot snapshot,
		CancellationToken cancellationToken = default,
		OutputPathRedactionDecision? outputPathRedaction = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();

        snapshot = NormalizeSelection(snapshot);
		outputPathRedaction ??= OutputRootPathPresentation.CaptureRedactionDecision(
			snapshot.RedactionContext);
		var displayRootPath = OutputRootPathPresentation.Resolve(
			snapshot.RootPath,
			snapshot.PathPresentation,
			outputPathRedaction);
        var displayRootName = snapshot.PathPresentation?.DisplayRootName;
        if (snapshot.SelectedPaths.Count == 0)
        {
            return treeExport.BuildFullTreeWithCancellation(
                snapshot.RootPath,
                snapshot.Root,
                snapshot.TreeFormat,
                displayRootPath,
                displayRootName,
                includeRootPath: true,
                cancellationToken: cancellationToken);
        }

        var tree = treeExport.BuildSelectedTreeWithCancellation(
            snapshot.RootPath,
            snapshot.Root,
            snapshot.SelectedPaths,
            snapshot.TreeFormat,
            displayRootPath,
            displayRootName,
            cancellationToken);

        return string.IsNullOrWhiteSpace(tree)
            ? treeExport.BuildFullTreeWithCancellation(
                snapshot.RootPath,
                snapshot.Root,
                snapshot.TreeFormat,
                displayRootPath,
                displayRootName,
                includeRootPath: true,
                cancellationToken: cancellationToken)
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

internal sealed record ProjectTextDocumentOutputResult(
	IPreviewTextDocument Document,
	int CandidateFileCount) : IDisposable
{
	public void Dispose() => Document.Dispose();
}

internal enum ProjectTextOutputMode
{
    Tree,
    Content,
    TreeAndContent
}
