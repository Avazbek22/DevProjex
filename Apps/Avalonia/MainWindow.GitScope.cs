using DevProjex.Avalonia.Coordinators;
using DevProjex.Application.Context;

namespace DevProjex.Avalonia;

public partial class MainWindow
{
	private BuildTreeSnapshotResult BuildTreeWithGitScope(
		TreeRefreshInput input,
		CancellationToken cancellationToken)
	{
		var result = input.TreeInventory is null
			? _buildTree.ExecuteWithInventory(
				new BuildTreeRequest(input.CurrentPath, input.Options),
				cancellationToken)
			: _buildTree.ExecuteWithInventory(
				new BuildTreeRequest(input.CurrentPath, input.Options),
				input.TreeInventory,
				cancellationToken);
		if (!GitScopeSelection.IsMomentary(input.GitMode))
			return result;

		var availableRootFolders = input.AvailableRootFolders ?? input.Options.AllowedRootFolders;
		var rootSelectionIsExplicit = input.AvailableRootFolders is not null &&
		                              !input.Options.AllowedRootFolders.SetEquals(availableRootFolders);
		var scope = input.GitScope ?? GitScopeFilter
			.ResolvePathsAsync(
				_gitScopePathProvider,
				input.CurrentPath,
				input.GitMode,
				diffRange: null,
				GitScopeFilter.GetDiscoveredRepositoryRoots(
					result.Inventory,
					input.CurrentPath,
					input.Options.AllowedRootFolders,
					rootSelectionIsExplicit,
					input.GitRepositoryScopePaths),
				input.GitRepositoryScopePaths,
				cancellationToken)
			.GetAwaiter()
			.GetResult();
		if (!scope.IsAvailable)
		{
			return result with
			{
				Diagnostics = AppendGitScopeDiagnostic(
					result.Diagnostics,
					GitScopeFilter.CreateUnavailableDiagnostic(input.CurrentPath, scope))
			};
		}

		var diagnostics = result.Diagnostics;
		if (scope.DeletedPathCount > 0)
		{
			diagnostics = AppendGitScopeDiagnostic(
				diagnostics,
				GitScopeFilter.CreateDeletedDiagnostic(input.CurrentPath, scope.DeletedPathCount));
		}
		return result with
		{
			Tree = GitScopeFilter.ApplyToTree(result.Tree, scope, cancellationToken),
			Diagnostics = diagnostics,
			GitScopePresentation = input.GitScopePresentation ?? GitScopePresentationProjector.Build(
				input.CurrentPath,
				result.Inventory,
				scope,
				input.Options.AllowedRootFolders,
				availableRootFolders,
				input.EffectiveExtensionPolicy,
				input.Options.IgnoreRules,
				cancellationToken,
				rootSelectionIsExplicit,
				selectedPathFrontier: input.GitRepositoryScopePaths)
		};
	}

	internal static IReadOnlyList<ContextDiagnostic> AppendGitScopeDiagnostic(
		IReadOnlyList<ContextDiagnostic>? existing,
		ContextDiagnostic diagnostic)
	{
		ArgumentNullException.ThrowIfNull(diagnostic);
		if (existing is null || existing.Count == 0)
			return [diagnostic];

		var combined = new ContextDiagnostic[existing.Count + 1];
		for (var index = 0; index < existing.Count; index++)
			combined[index] = existing[index];
		combined[^1] = diagnostic;
		return combined;
	}

	private bool HandleGitScopeDiagnostics(IReadOnlyList<ContextDiagnostic>? diagnostics)
	{
		if (diagnostics is null || diagnostics.Count == 0)
			return false;
		var gitDiagnostics = diagnostics.Where(IsGitScopeDiagnostic).ToArray();
		foreach (var diagnostic in gitDiagnostics)
		{
			var message = diagnostic.Code == GitScopeFilter.DeletedDiagnosticCode
				? _localization.Format("Terminal.Diagnostic.GitStateDeleted", diagnostic.Count ?? 0)
				: _localization["Terminal.Diagnostic.GitStateUnavailable"];
			_toastService.Show(message);
		}
		return gitDiagnostics.Any(static diagnostic =>
			diagnostic.Severity == ContextDiagnosticSeverity.Error);
	}

	internal static bool IsGitScopeDiagnostic(ContextDiagnostic diagnostic) =>
		diagnostic.Code is GitScopeFilter.DeletedDiagnosticCode or
			GitScopeFilter.UnavailableDiagnosticCode;
}
