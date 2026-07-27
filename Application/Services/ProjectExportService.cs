namespace DevProjex.Application.Services;

public sealed class ProjectExportService(
	TreeExportService treeExport,
	SelectedContentExportService contentExport,
	TreeAndContentExportService treeAndContentExport)
{
	public async Task<string> BuildAsync(
		LoadedProjectAnalysisRequest project,
		ProjectTextExportRequest request,
		CancellationToken cancellationToken = default)
	{
		return request.Mode switch
		{
			ProjectTextExportMode.Tree => treeExport.BuildFullTree(project.RootPath, project.Tree.Root, request.Format),
			ProjectTextExportMode.Content => await contentExport
				.BuildAsync(project.Tree.OrderedFilePaths ?? [], cancellationToken)
				.ConfigureAwait(false),
			ProjectTextExportMode.TreeContent => await treeAndContentExport
				.BuildAsync(project.RootPath, project.Tree.Root, new HashSet<string>(PathComparer.Default), request.Format, cancellationToken)
				.ConfigureAwait(false),
			_ => throw new InvalidOperationException($"Unsupported export mode: {request.Mode}.")
		};
	}
}

public enum ProjectTextExportMode
{
	Tree,
	Content,
	TreeContent
}

public sealed record ProjectTextExportRequest(
	ProjectTextExportMode Mode,
	TreeTextFormat Format);
