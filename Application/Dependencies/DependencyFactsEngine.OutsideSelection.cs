namespace DevProjex.Application.Dependencies;

public sealed partial class DependencyFactsEngine
{
	/// <summary>
	/// A narrowed selection cannot tell a reference into a file it left out from one that leads
	/// nowhere. Only then is the project index worth building: when dependents are requested, or
	/// when a seed has a reference the selection could not resolve.
	/// </summary>
	public static bool CanRelateOutsideSelection(DependencyRelatedResult selection, DependencyDirection direction)
	{
		ArgumentNullException.ThrowIfNull(selection);
		var supportedSeeds = selection.Seeds.Where(static seed => seed.NoFactsReason is null).ToArray();
		if (supportedSeeds.Length == 0)
			return false;
		if (direction is DependencyDirection.Dependents or DependencyDirection.Both)
			return true;
		return supportedSeeds.Any(seed =>
			(selection.Index.EdgesBySource.GetValueOrDefault(seed.Seed) ?? [])
			.Any(static edge => edge.Status == ResolutionStatus.Unresolved));
	}

	/// <summary>
	/// Relates the seeds of a narrowed selection to project files the selection left out. The
	/// project manifest must already exclude every file the caller may not read; the result only
	/// names those files. Relations the selection answered itself stay as it answered them.
	/// </summary>
	public async Task<DependencyOutsideSelectionResult> FindOutsideSelectionAsync(
		string sourceRoot,
		DependencyRelatedResult selection,
		IReadOnlyList<string> projectManifestFiles,
		DependencyDirection direction,
		IProgress<DependencyIndexProgress>? progress = null,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(selection);
		var project = await IndexAsync(sourceRoot, projectManifestFiles, progress, cancellationToken)
			.ConfigureAwait(false);
		return FindOutsideSelection(selection, project, direction, cancellationToken);
	}

	private static DependencyOutsideSelectionResult FindOutsideSelection(
		DependencyRelatedResult selection,
		DependencyIndexSnapshot project,
		DependencyDirection direction,
		CancellationToken cancellationToken)
	{
		var visible = selection.Index.FileByPath;
		var seeds = new List<SeedOutsideSelection>(selection.Seeds.Count);
		var usedEdges = new HashSet<DependencyEdge>(ReferenceEqualityComparer.Instance);
		var explainedUnresolved = 0;
		foreach (var seed in selection.Seeds)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (seed.NoFactsReason is not null || !project.FileByPath.ContainsKey(seed.Seed))
				continue;

			IReadOnlyList<RelatedFile> dependencies = [];
			if (direction is DependencyDirection.Dependencies or DependencyDirection.Both)
			{
				var selectionEdges = selection.Index.EdgesBySource.GetValueOrDefault(seed.Seed) ?? [];
				var answered = selectionEdges
					.Where(static edge => edge.Status != ResolutionStatus.Unresolved)
					.Select(static edge => (edge.Layer, edge.Reference))
					.ToHashSet();
				var outsideEdges = (project.EdgesBySource.GetValueOrDefault(seed.Seed) ?? [])
					.Where(edge => edge.Status == ResolutionStatus.Resolved &&
								   !answered.Contains((edge.Layer, edge.Reference)) &&
								   ResolvedTargetFiles(edge).Any(target =>
									   target != seed.Seed && !visible.ContainsKey(target)))
					.ToArray();
				var outsideKeys = outsideEdges.Select(static edge => (edge.Layer, edge.Reference)).ToHashSet();
				explainedUnresolved += selectionEdges.Count(edge =>
					edge.Status == ResolutionStatus.Unresolved && outsideKeys.Contains((edge.Layer, edge.Reference)));
				usedEdges.UnionWith(outsideEdges);
				dependencies = ProjectDependencies(seed.Seed, outsideEdges, project.FileByPath)
					.Where(file => !visible.ContainsKey(file.Path))
					.ToArray();
			}

			IReadOnlyList<RelatedFile> dependents = [];
			if (direction is DependencyDirection.Dependents or DependencyDirection.Both)
			{
				var outsideEdges = (project.EdgesByTarget.GetValueOrDefault(seed.Seed) ?? [])
					.Where(edge => edge.Status == ResolutionStatus.Resolved && !visible.ContainsKey(edge.Source))
					.ToArray();
				usedEdges.UnionWith(outsideEdges);
				dependents = ProjectDependents(seed.Seed, outsideEdges, project.FileByPath);
			}

			if (dependencies.Count == 0 && dependents.Count == 0)
				continue;
			seeds.Add(new SeedOutsideSelection(
				seed.Seed,
				dependencies.Take(DependencyOutsideSelectionResult.MaximumFilesPerSection).ToArray(),
				dependencies.Count,
				dependents.Take(DependencyOutsideSelectionResult.MaximumFilesPerSection).ToArray(),
				dependents.Count));
		}
		return new DependencyOutsideSelectionResult(project, seeds, usedEdges.Count, explainedUnresolved);
	}
}
