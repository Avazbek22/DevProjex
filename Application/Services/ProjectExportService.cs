namespace DevProjex.Application.Services;

public sealed class ProjectExportService(
	TreeExportService treeExport,
	SelectedContentExportService contentExport,
	TreeAndContentExportService treeAndContentExport)
{
	public async Task<string> BuildAsync(
		LoadedProjectAnalysisRequest project,
		StartupExportOptions options,
		CancellationToken cancellationToken = default)
	{
		if (!options.Enabled)
			throw new ArgumentException("Export options must be enabled.", nameof(options));

		return options.Mode switch
		{
			StartupExportMode.Tree => treeExport.BuildFullTree(project.RootPath, project.Tree.Root, options.Format),
			StartupExportMode.Content => await contentExport
				.BuildAsync(project.Tree.OrderedFilePaths ?? [], cancellationToken)
				.ConfigureAwait(false),
			StartupExportMode.TreeContent => await treeAndContentExport
				.BuildAsync(project.RootPath, project.Tree.Root, new HashSet<string>(PathComparer.Default), options.Format, cancellationToken)
				.ConfigureAwait(false),
			_ => throw new InvalidOperationException($"Unsupported export mode: {options.Mode}.")
		};
	}
}
