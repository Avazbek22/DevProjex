namespace DevProjex.Application.Services;

public interface IGitScopePathProvider
{
	Task<GitScopePathResult> ResolveAsync(
		string projectRoot,
		GitFilteringMode mode,
		string? diffRange,
		CancellationToken cancellationToken = default);
}

public sealed record GitScopePathResult(
	bool IsAvailable,
	IReadOnlySet<string> IncludedPaths,
	int DeletedPathCount,
	string? FailureReason = null)
{
	public static GitScopePathResult Unavailable(string? reason = null) =>
		new(false, new HashSet<string>(PathComparer.Default), 0, reason);
}

public static class GitScopeFilter
{
	public const string UnavailableDiagnosticCode = "DPX-GIT-STATE-UNAVAILABLE";
	public const string DeletedDiagnosticCode = "DPX-GIT-STATE-DELETED";

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
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(planner);
		ArgumentNullException.ThrowIfNull(plan);
		ArgumentNullException.ThrowIfNull(provider);

		if (!GitScopeSelection.IsMomentary(scopeMode))
			return plan;

		var scope = await provider
			.ResolveAsync(plan.SourceRoot, scopeMode, diffRange, cancellationToken)
			.ConfigureAwait(false);
		if (!scope.IsAvailable)
		{
			var empty = await planner
				.ReprojectEmptySelectionAsync(plan, cancellationToken)
				.ConfigureAwait(false);
			return empty with
			{
				Selection = plan.Selection,
				Diagnostics = AppendDiagnostic(
					empty.Diagnostics,
					CreateUnavailableDiagnostic(plan.SourceRoot, scope))
			};
		}

		var scopeProjection = ProjectContextPlanner.ResolveSelectionProjection(
			plan.EffectiveTree,
			scope.IncludedPaths,
			scope.IncludedPaths.Count == 0,
			knownFullTreeFilePaths: null,
			cancellationToken);
		var scopedBaseline = plan with
		{
			EffectiveTree = scopeProjection.ProjectedTree,
			ProjectedTree = scopeProjection.ProjectedTree,
			IncludedFiles = scopeProjection.IncludedFiles,
			IncludedFolders = scopeProjection.IncludedFolders
		};

		var selected = new List<string>(Math.Min(plan.IncludedFiles.Count, scope.IncludedPaths.Count));
		foreach (var path in plan.IncludedFiles)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (scope.IncludedPaths.Contains(path))
				selected.Add(PathUtility.GetPortableRelativePath(plan.SourceRoot, path));
		}

		ProjectContextPlan narrowed;
		if (selected.Count > 0)
		{
			narrowed = await planner
				.ReprojectSelectionAsync(scopedBaseline, selected, cancellationToken)
				.ConfigureAwait(false);
		}
		else
		{
			narrowed = await planner
				.ReprojectEmptySelectionAsync(scopedBaseline, cancellationToken)
				.ConfigureAwait(false);
		}

		var diagnostics = narrowed.Diagnostics;
		if (scope.DeletedPathCount > 0)
		{
			diagnostics = AppendDiagnostic(
				diagnostics,
				CreateDeletedDiagnostic(plan.SourceRoot, scope.DeletedPathCount));
		}

		return narrowed with
		{
			Selection = plan.Selection,
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
		var projection = ProjectContextPlanner.ResolveSelectionProjection(
			tree.Root,
			scope.IncludedPaths,
			scope.IncludedPaths.Count == 0,
			knownFullTreeFilePaths: null,
			cancellationToken);
		return tree with
		{
			Root = projection.ProjectedTree,
			OrderedFilePaths = projection.IncludedFiles
		};
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
			$"{count} deleted files from the Git state are not included.",
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
