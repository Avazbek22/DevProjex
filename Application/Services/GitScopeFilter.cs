namespace DevProjex.Application.Services;

public interface IGitScopePathProvider
{
	Task<GitScopePathResult> ResolveAsync(
		string projectRoot,
		GitFilteringMode mode,
		string? diffRange,
		CancellationToken cancellationToken = default);

	Task<GitScopePathResult> ResolveAsync(
		string projectRoot,
		GitFilteringMode mode,
		string? diffRange,
		IReadOnlyCollection<string> repositoryRoots,
		CancellationToken cancellationToken = default) =>
		ResolveAsync(projectRoot, mode, diffRange, cancellationToken);
}

public sealed record GitScopePathResult(
	bool IsAvailable,
	IReadOnlySet<string> IncludedPaths,
	int DeletedPathCount,
	string? FailureReason = null,
	IReadOnlyList<GitTrackedPathIndex>? PathMatchers = null)
{
	public static GitScopePathResult Unavailable(string? reason = null) =>
		new(false, new HashSet<string>(PathComparer.Default), 0, reason);

	public bool ContainsPath(string path)
	{
		return TryGetOwningMatcher(path, out var owner, out _)
			? owner.Contains(path)
			: IncludedPaths.Contains(path);
	}

	internal string? GetComparisonIdentity(string path)
	{
		if (PathMatchers is null)
			return null;

		return TryGetOwningMatcher(path, out var owner, out var relativeIdentity)
			? owner.RepositoryRootPath + '\0' + relativeIdentity
			: null;
	}

	private bool TryGetOwningMatcher(
		string path,
		out GitTrackedPathIndex owner,
		out string relativeIdentity)
	{
		owner = null!;
		relativeIdentity = string.Empty;
		if (PathMatchers is null)
			return false;

		for (var index = 0; index < PathMatchers.Count; index++)
		{
			var candidate = PathMatchers[index];
			if ((owner is not null && candidate.RepositoryRootPath.Length <= owner.RepositoryRootPath.Length) ||
			    !candidate.TryGetPathIdentity(path, out var candidateIdentity))
			{
				continue;
			}

			owner = candidate;
			relativeIdentity = candidateIdentity;
		}

		return owner is not null;
	}
}

public static class GitScopeFilter
{
	public const string UnavailableDiagnosticCode = "DPX-GIT-STATE-UNAVAILABLE";
	public const string DeletedDiagnosticCode = "DPX-GIT-STATE-DELETED";

	public static IReadOnlyList<string> GetDiscoveredRepositoryRoots(
		ProjectTreeInventorySnapshot? inventory) =>
		inventory is null
			? []
			: inventory.DiscoveredGitRepositoryRoots
			.Concat(inventory.DiscoveredGitTrackedPathIndexes.Select(static index => index.RepositoryRootPath))
			.Distinct(StringComparer.Ordinal)
			.OrderBy(static path => path, PathComparer.Default)
			.ThenBy(static path => path, StringComparer.Ordinal)
			.ToArray();

	public static IReadOnlyList<string> GetDiscoveredRepositoryRoots(
		ProjectTreeInventorySnapshot? inventory,
		string sourceRoot,
		IReadOnlyCollection<string> selectedRootFolders,
		bool rootSelectionIsExplicit,
		IReadOnlyCollection<string>? selectedFullPaths = null)
	{
		var repositoryRoots = GetDiscoveredRepositoryRoots(inventory);
		var pathSelectionIsExplicit = selectedFullPaths is not null;
		if ((!rootSelectionIsExplicit && !pathSelectionIsExplicit) || repositoryRoots.Count == 0)
			return repositoryRoots;

		var selectedRoots = new List<string>(
			(rootSelectionIsExplicit ? selectedRootFolders.Count : 0) +
			(selectedFullPaths?.Count ?? 0));
		if (rootSelectionIsExplicit)
		{
			foreach (var selectedRootFolder in selectedRootFolders)
			{
				if (string.IsNullOrWhiteSpace(selectedRootFolder))
					continue;
				try
				{
					var selectedRoot = PathUtility.Normalize(Path.Combine(sourceRoot, selectedRootFolder));
					if (PathUtility.IsPathInside(selectedRoot, sourceRoot))
						selectedRoots.Add(selectedRoot);
				}
				catch (Exception exception) when (
					exception is ArgumentException or IOException or NotSupportedException)
				{
				}
			}
		}
		if (selectedFullPaths is not null)
		{
			foreach (var selectedFullPath in selectedFullPaths)
			{
				if (PathUtility.IsPathInside(selectedFullPath, sourceRoot))
					selectedRoots.Add(selectedFullPath);
			}
		}

		var selectedRepositoryRoots = new HashSet<string>(StringComparer.Ordinal);
		foreach (var selectedRoot in selectedRoots)
		{
			string? owningRepository = null;
			foreach (var repositoryRoot in repositoryRoots)
			{
				if (PathUtility.IsPathInside(repositoryRoot, selectedRoot))
					selectedRepositoryRoots.Add(repositoryRoot);
				if (!PathUtility.IsPathInside(selectedRoot, repositoryRoot) ||
				    (owningRepository is not null && repositoryRoot.Length <= owningRepository.Length))
				{
					continue;
				}

				owningRepository = repositoryRoot;
			}

			if (owningRepository is not null)
				selectedRepositoryRoots.Add(owningRepository);
		}

		return selectedRepositoryRoots
			.OrderBy(static path => path, PathComparer.Default)
			.ThenBy(static path => path, StringComparer.Ordinal)
			.ToArray();
	}

	public static async Task<ProjectContextPlan> ApplyAsync(
		ProjectContextPlanner planner,
		ProjectContextPlan plan,
		IGitScopePathProvider provider,
		CancellationToken cancellationToken = default) =>
		await ApplyAsync(
			planner,
			plan,
			provider,
			plan.Selection.GitMode ?? GitFilteringMode.None,
			plan.Selection.GitDiffRange,
			cancellationToken).ConfigureAwait(false);

	public static async Task<ProjectContextPlan> ApplyAsync(
		ProjectContextPlanner planner,
		ProjectContextPlan plan,
		IGitScopePathProvider provider,
		GitFilteringMode scopeMode,
		string? diffRange,
		CancellationToken cancellationToken = default) =>
		await ApplyAsync(
			planner,
			plan,
			provider,
			scopeMode,
			diffRange,
			repositoryScopeFullPaths: null,
			cancellationToken).ConfigureAwait(false);

	public static async Task<ProjectContextPlan> ApplyAsync(
		ProjectContextPlanner planner,
		ProjectContextPlan plan,
		IGitScopePathProvider provider,
		GitFilteringMode scopeMode,
		string? diffRange,
		IReadOnlyCollection<string>? repositoryScopeFullPaths,
		CancellationToken cancellationToken = default,
		string? resolvedDiffRange = null)
	{
		ArgumentNullException.ThrowIfNull(planner);
		ArgumentNullException.ThrowIfNull(plan);
		ArgumentNullException.ThrowIfNull(provider);

		if (!GitScopeSelection.IsMomentary(scopeMode))
			return plan;

		var scopedSelection = plan.Selection with
		{
			GitMode = scopeMode,
			GitDiffRange = scopeMode == GitFilteringMode.Diff ? diffRange : null
		};
		var scopedBaseline = plan with { Selection = scopedSelection };

		var scope = repositoryScopeFullPaths is { Count: 0 }
			? new GitScopePathResult(
				true,
				new HashSet<string>(StringComparer.Ordinal),
				0,
				PathMatchers: [])
			: await provider
				.ResolveAsync(
					plan.SourceRoot,
					scopeMode,
					resolvedDiffRange ?? diffRange,
					planner.GetGitScopeRepositoryRoots(plan, repositoryScopeFullPaths),
					cancellationToken)
				.ConfigureAwait(false);
		if (!scope.IsAvailable)
		{
			var empty = await planner
				.ReprojectEmptySelectionAsync(scopedBaseline, cancellationToken)
				.ConfigureAwait(false);
			return empty with
			{
				EffectiveTree = empty.ProjectedTree,
				Diagnostics = AppendDiagnostic(
					empty.Diagnostics,
					CreateUnavailableDiagnostic(plan.SourceRoot, scope))
			};
		}

		var scopedFilePaths = RetainExactFilePaths(
			plan.EffectiveTree,
			scope,
			cancellationToken);
		var scopedTree = ProjectContextPlanner.ResolveSelectionProjection(
			plan.EffectiveTree,
			scopedFilePaths,
			scopedFilePaths.Count == 0,
			knownFullTreeFilePaths: null,
			cancellationToken,
			StringComparer.Ordinal).ProjectedTree;

		var selectedFiles = RetainExactFilePaths(
			plan.ProjectedTree,
			scope,
			cancellationToken);
		var selected = new List<string>(selectedFiles.Count);
		foreach (var path in selectedFiles)
		{
			cancellationToken.ThrowIfCancellationRequested();
			selected.Add(PathUtility.GetPortableRelativePath(plan.SourceRoot, path));
		}

		ProjectContextPlan narrowed;
		if (selected.Count > 0)
		{
			narrowed = await planner
				.ReprojectSelectionAsync(
					scopedBaseline,
					selected,
					StringComparer.Ordinal,
					cancellationToken)
				.ConfigureAwait(false);
		}
		else
		{
			narrowed = await planner
				.ReprojectEmptySelectionAsync(scopedBaseline, cancellationToken)
				.ConfigureAwait(false);
		}

		var diagnostics = narrowed.Diagnostics;
		var presentation = planner.BuildGitScopePresentation(plan, scope, cancellationToken);
		if (scope.DeletedPathCount > 0)
		{
			diagnostics = AppendDiagnostic(
				diagnostics,
				CreateDeletedDiagnostic(plan.SourceRoot, scope.DeletedPathCount));
		}

		return narrowed with
		{
			EffectiveTree = scopedTree,
			AvailableExtensions = presentation.AvailableExtensions,
			SelectedExtensions = plan.SelectedExtensions,
			HasIgnoreOptionCounts = plan.HasIgnoreOptionCounts,
			IgnoreOptionCounts = plan.HasIgnoreOptionCounts
				? presentation.IgnoreOptionCounts
				: IgnoreOptionCounts.Empty,
			IgnoreControllerImpactCounts = plan.HasIgnoreOptionCounts
				? presentation.ControllerImpactCounts
				: IgnoreControllerImpactCounts.Empty,
			Diagnostics = diagnostics
		};
	}

	public static BuildTreeResult ApplyToTree(
		BuildTreeResult tree,
		GitScopePathResult scope,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(tree);
		ArgumentNullException.ThrowIfNull(scope);
		if (!scope.IsAvailable)
			return tree;
		var scopedFilePaths = RetainExactFilePaths(
			tree.Root,
			scope,
			cancellationToken);
		var projection = ProjectContextPlanner.ResolveSelectionProjection(
			tree.Root,
			scopedFilePaths,
			scopedFilePaths.Count == 0,
			knownFullTreeFilePaths: null,
			cancellationToken,
			StringComparer.Ordinal);
		return tree with
		{
			Root = projection.ProjectedTree,
			OrderedFilePaths = projection.IncludedFiles
		};
	}

	private static IReadOnlySet<string> RetainExactFilePaths(
		TreeNodeDescriptor root,
		GitScopePathResult scope,
		CancellationToken cancellationToken)
	{
		if (scope.IncludedPaths.Count == 0)
			return scope.IncludedPaths;

		var files = new HashSet<string>(StringComparer.Ordinal);
		var pending = new Stack<TreeNodeDescriptor>();
		pending.Push(root);
		while (pending.Count > 0)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var node = pending.Pop();
			if (!node.IsDirectory)
			{
				if (scope.ContainsPath(node.FullPath))
					files.Add(node.FullPath);
				continue;
			}

			for (var index = node.Children.Count - 1; index >= 0; index--)
				pending.Push(node.Children[index]);
		}

		return files;
	}

	public static ContextDiagnostic CreateUnavailableDiagnostic(
		string projectRoot,
		GitScopePathResult scope) =>
		new(
			UnavailableDiagnosticCode,
			ContextDiagnosticSeverity.Error,
			scope.FailureReason ?? "The requested Git state is unavailable.",
			projectRoot);

	public static ContextDiagnostic CreateDeletedDiagnostic(string projectRoot, int count) =>
		new(
			DeletedDiagnosticCode,
			ContextDiagnosticSeverity.Warning,
			$"Deleted files excluded from the Git state: {count}.",
			projectRoot,
			count);

	private static IReadOnlyList<ContextDiagnostic> AppendDiagnostic(
		IReadOnlyList<ContextDiagnostic> diagnostics,
		ContextDiagnostic diagnostic)
	{
		var result = new ContextDiagnostic[diagnostics.Count + 1];
		for (var index = 0; index < diagnostics.Count; index++)
			result[index] = diagnostics[index];
		result[^1] = diagnostic;
		return result;
	}
}
