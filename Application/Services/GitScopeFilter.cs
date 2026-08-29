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

		var scopedSelection = plan.Selection with
		{
			GitMode = scopeMode,
			GitDiffRange = scopeMode == GitFilteringMode.Diff ? diffRange : null
		};
		var scopedBaseline = plan with { Selection = scopedSelection };

		var scope = await provider
			.ResolveAsync(plan.SourceRoot, scopeMode, diffRange, cancellationToken)
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
			scope.IncludedPaths,
			cancellationToken);
		var scopedTree = ProjectContextPlanner.ResolveSelectionProjection(
			plan.EffectiveTree,
			scopedFilePaths,
			scopedFilePaths.Count == 0,
			knownFullTreeFilePaths: null,
			cancellationToken).ProjectedTree;

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
			HasIgnoreOptionCounts = true,
			IgnoreOptionCounts = presentation.IgnoreOptionCounts,
			IgnoreControllerImpactCounts = presentation.ControllerImpactCounts,
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
			scope.IncludedPaths,
			cancellationToken);
		var projection = ProjectContextPlanner.ResolveSelectionProjection(
			tree.Root,
			scopedFilePaths,
			scopedFilePaths.Count == 0,
			knownFullTreeFilePaths: null,
			cancellationToken);
		return tree with
		{
			Root = projection.ProjectedTree,
			OrderedFilePaths = projection.IncludedFiles
		};
	}

	private static IReadOnlySet<string> RetainExactFilePaths(
		TreeNodeDescriptor root,
		IReadOnlySet<string> scopedPaths,
		CancellationToken cancellationToken)
	{
		if (scopedPaths.Count == 0)
			return scopedPaths;

		var files = new HashSet<string>(PathComparer.Default);
		var pending = new Stack<TreeNodeDescriptor>();
		pending.Push(root);
		while (pending.Count > 0)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var node = pending.Pop();
			if (!node.IsDirectory)
			{
				if (scopedPaths.Contains(node.FullPath))
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
