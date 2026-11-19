namespace DevProjex.Application.Dependencies;

public sealed partial class DependencyFactsEngine
{
	/// <summary>
	/// A narrowed selection cannot see part of the project: a reference into a file it left out
	/// reads as unresolved, external, a namespace, a single candidate where the project has
	/// several, or a module that only stands in for a child module the selection left out. Any
	/// seed with facts is therefore worth relating against the readable project.
	/// </summary>
	public static bool CanRelateOutsideSelection(DependencyRelatedResult selection)
	{
		ArgumentNullException.ThrowIfNull(selection);
		return selection.Seeds.Any(static seed => seed.NoFactsReason is null);
	}

	/// <summary>
	/// Counts the selected-direction edges of the seeds by status: every edge a seed starts, and
	/// every edge whose target is the seed itself.
	/// </summary>
	public static DependencyResolutionCounts CountResolution(
		DependencyIndexSnapshot index,
		IEnumerable<string> seeds,
		DependencyDirection direction)
	{
		ArgumentNullException.ThrowIfNull(index);
		ArgumentNullException.ThrowIfNull(seeds);
		return CountResolution(seeds.Select(seed => (index, seed)), direction, visible: null);
	}

	/// <summary>
	/// Relates the seeds of a narrowed selection against the readable project. The project
	/// manifest must already exclude every file the caller may not read; files outside the
	/// selection are only named.
	/// </summary>
	public Task<DependencyOutsideSelectionResult> FindOutsideSelectionAsync(
		string sourceRoot,
		DependencyRelatedResult selection,
		IReadOnlyList<string> projectManifestFiles,
		DependencyDirection direction,
		IProgress<DependencyIndexProgress>? progress = null,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(selection);
		return FindOutsideSelectionAsync(
			sourceRoot,
			selection,
			projectManifestFiles,
			selection.Seeds.Select(static seed => seed.Seed).ToArray(),
			direction,
			depth: 1,
			progress,
			cancellationToken);
	}

	/// <summary>
	/// Relates <paramref name="seedRelativePaths"/> against the readable project and walks
	/// <paramref name="depth"/> levels of resolved relations as a related traversal does, but
	/// only into files the selection shows.
	/// </summary>
	public async Task<DependencyOutsideSelectionResult> FindOutsideSelectionAsync(
		string sourceRoot,
		DependencyRelatedResult selection,
		IReadOnlyList<string> projectManifestFiles,
		IReadOnlyList<string> seedRelativePaths,
		DependencyDirection direction,
		int depth,
		IProgress<DependencyIndexProgress>? progress = null,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(selection);
		ArgumentNullException.ThrowIfNull(seedRelativePaths);
		if (depth < 1)
			throw new ArgumentOutOfRangeException(nameof(depth));

		// The project manifest is everything the caller may read, so no configuration outside it is.
		var project = await IndexAsync(
				sourceRoot, projectManifestFiles, progress, cancellationToken, readableProjectFiles: projectManifestFiles)
			.ConfigureAwait(false);
		return FindOutsideSelection(selection.Index, project, seedRelativePaths, direction, depth, cancellationToken);
	}

	private static DependencyOutsideSelectionResult FindOutsideSelection(
		DependencyIndexSnapshot selection,
		DependencyIndexSnapshot project,
		IReadOnlyList<string> seedRelativePaths,
		DependencyDirection direction,
		int depth,
		CancellationToken cancellationToken)
	{
		var visible = selection.FileByPath;
		var projectFiles = project.FileByPath;
		var outsideSeeds = new List<SeedOutsideSelection>();
		var answers = new List<(DependencyIndexSnapshot Index, string Seed)>();
		var selectionAnsweredSeeds = 0;
		var seeds = TraverseRelated(
			seedRelativePaths,
			direction,
			depth,
			visible,
			frontier => frontier.Select(Relate).ToArray(),
			cancellationToken);
		return new DependencyOutsideSelectionResult(
			project,
			new DependencyRelatedResult(selection, seeds),
			outsideSeeds,
			CountResolution(answers, direction, visible))
		{
			SelectionAnsweredSeeds = selectionAnsweredSeeds
		};

		SeedRelatedFiles Relate(string seed)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var own = FindRelated(selection, [seed], direction).Seeds[0];
			if (visible.GetValueOrDefault(seed)?.Status != DependencyFileStatus.Supported)
				return own;
			if (!projectFiles.TryGetValue(seed, out var projectFacts) ||
				projectFacts.Status != DependencyFileStatus.Supported ||
				ProjectDroppedFactsOf(own))
			{
				// The project cannot answer for this seed in full, so the selection's own answer,
				// which holds every relation it could see, is the best one.
				answers.Add((selection, seed));
				selectionAnsweredSeeds++;
				return own;
			}

			answers.Add((project, seed));
			var dependencies = direction == DependencyDirection.Dependents
				? []
				: ProjectDependencies(seed, project.EdgesBySource.GetValueOrDefault(seed) ?? [], project.FileByPath);
			var dependents = direction == DependencyDirection.Dependencies
				? []
				: ProjectDependents(seed, project.EdgesByTarget.GetValueOrDefault(seed) ?? [], project.FileByPath);
			var outsideDependencies = OutsideSelection(dependencies);
			var outsideDependents = OutsideSelection(dependents);
			if (outsideDependencies.Length > 0 || outsideDependents.Length > 0)
			{
				outsideSeeds.Add(new SeedOutsideSelection(
					seed,
					outsideDependencies.Take(DependencyOutsideSelectionResult.MaximumFilesPerSection).ToArray(),
					outsideDependencies.Length,
					outsideDependents.Take(DependencyOutsideSelectionResult.MaximumFilesPerSection).ToArray(),
					outsideDependents.Length));
			}
			return new SeedRelatedFiles(
				seed,
				visible[seed].LanguageId,
				InsideSelection(dependencies),
				InsideSelection(dependents),
				null);
		}

		// A file the selection relates to the seed reaches the project's answer only through facts
		// the project kept: the seed's own resolved edges, a dependency's declarations, and a
		// dependent's resolved edges. A limit that dropped any of them would silently lose a
		// relation the selection shows.
		bool ProjectDroppedFactsOf(SeedRelatedFiles own)
		{
			if (direction != DependencyDirection.Dependents && HasLimitEdge(own.Seed))
				return true;
			foreach (var dependency in own.Dependencies)
			{
				if (IsFactBudgetLimited(dependency.Path))
					return true;
			}
			foreach (var dependent in own.Dependents)
			{
				if (IsFactBudgetLimited(dependent.Path) || HasLimitEdge(dependent.Path))
					return true;
			}
			return false;
		}

		bool IsFactBudgetLimited(string path) =>
			projectFiles.GetValueOrDefault(path) is { FactBudgetLimited: true };

		bool HasLimitEdge(string path) =>
			(project.EdgesBySource.GetValueOrDefault(path) ?? []).Any(static edge =>
				edge.Target is null && string.Equals(edge.Reference, LimitReference, StringComparison.Ordinal));

		// An ambiguous row names the candidate it chose, so it is listed only where that file
		// is shown; outside sections hold resolved relations only. A target that is no file of the
		// project, such as a Python namespace package, is never left out by narrowing.
		RelatedFile[] InsideSelection(IReadOnlyList<RelatedFile> files) =>
			files.Where(file => IsShown(visible, projectFiles, file.Path)).ToArray();

		RelatedFile[] OutsideSelection(IReadOnlyList<RelatedFile> files) =>
			files.Where(file => file.Status == ResolutionStatus.Resolved && !IsShown(visible, projectFiles, file.Path))
				.ToArray();
	}

	private static bool IsShown(
		IReadOnlyDictionary<string, FileFacts> visible,
		IReadOnlyDictionary<string, FileFacts> indexFiles,
		string path) =>
		visible.ContainsKey(path) || !indexFiles.ContainsKey(path);

	/// <param name="visible">
	/// The narrowed selection's files, or null when nothing was narrowed and every resolved edge
	/// counts as resolved.
	/// </param>
	private static DependencyResolutionCounts CountResolution(
		IEnumerable<(DependencyIndexSnapshot Index, string Seed)> answers,
		DependencyDirection direction,
		IReadOnlyDictionary<string, FileFacts>? visible)
	{
		// One edge counts once even when both of its ends are seeds; it stays inside the
		// selection when either end it is counted from reaches a file the selection shows.
		// The counts cover exactly the edges the rows are projected from: a resolved reference
		// to the seed's own file is never listed, and a dependent reaches the seed as any of its
		// declaration parts or as an ambiguous candidate, not only as the first target.
		var edges = new Dictionary<DependencyEdge, bool>();
		foreach (var (index, seed) in answers)
		{
			if (direction is DependencyDirection.Dependencies or DependencyDirection.Both)
			{
				foreach (var edge in index.EdgesBySource.GetValueOrDefault(seed) ?? [])
				{
					if (!ReachesOnlyItsSource(edge))
						Add(edge, visible is null || DependencyReachesSelection(edge, seed, visible, index.FileByPath));
				}
			}
			if (direction is DependencyDirection.Dependents or DependencyDirection.Both)
			{
				foreach (var edge in index.EdgesByTarget.GetValueOrDefault(seed) ?? [])
				{
					if (!string.Equals(edge.Source, seed, StringComparison.Ordinal))
						Add(edge, visible is null || visible.ContainsKey(edge.Source));
				}
			}
		}

		var counts = new int[4];
		var outsideSelection = 0;
		foreach (var (edge, insideSelection) in edges)
		{
			if (edge.Status == ResolutionStatus.Resolved && !insideSelection)
				outsideSelection++;
			else
				counts[(int)edge.Status]++;
		}
		return new DependencyResolutionCounts(
			counts[(int)ResolutionStatus.Resolved],
			counts[(int)ResolutionStatus.Ambiguous],
			counts[(int)ResolutionStatus.Unresolved],
			counts[(int)ResolutionStatus.External],
			outsideSelection);

		void Add(DependencyEdge edge, bool insideSelection) =>
			edges[edge] = edges.TryGetValue(edge, out var known) ? known || insideSelection : insideSelection;
	}

	private static bool ReachesOnlyItsSource(DependencyEdge edge)
	{
		if (edge.Status != ResolutionStatus.Resolved)
			return false;
		var reachesAnyFile = false;
		foreach (var target in ResolvedTargetFiles(edge))
		{
			if (!StringComparer.Ordinal.Equals(target, edge.Source))
				return false;
			reachesAnyFile = true;
		}
		return reachesAnyFile;
	}

	// The seed itself is never listed as its own dependency, so it does not keep an edge inside.
	// A target that is no file of the answering index stays inside, where its row is listed.
	private static bool DependencyReachesSelection(
		DependencyEdge edge,
		string seed,
		IReadOnlyDictionary<string, FileFacts> visible,
		IReadOnlyDictionary<string, FileFacts> indexFiles)
	{
		var reachesOtherFile = false;
		foreach (var target in ResolvedTargetFiles(edge))
		{
			if (StringComparer.Ordinal.Equals(target, seed))
				continue;
			if (IsShown(visible, indexFiles, target))
				return true;
			reachesOtherFile = true;
		}
		return !reachesOtherFile;
	}
}
