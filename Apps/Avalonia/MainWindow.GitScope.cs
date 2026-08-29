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

		var scope = _gitScopePathProvider
			.ResolveAsync(input.CurrentPath, input.GitMode, diffRange: null, cancellationToken)
			.GetAwaiter()
			.GetResult();
		if (!scope.IsAvailable)
		{
			return result with
			{
				Diagnostics = [GitScopeFilter.CreateUnavailableDiagnostic(input.CurrentPath, scope)]
			};
		}

		var diagnostics = scope.DeletedPathCount > 0
			? new[] { GitScopeFilter.CreateDeletedDiagnostic(input.CurrentPath, scope.DeletedPathCount) }
			: [];
		return result with
		{
			Tree = GitScopeFilter.ApplyToTree(result.Tree, scope, cancellationToken),
			Diagnostics = diagnostics
		};
	}

	private bool HandleGitScopeDiagnostics(IReadOnlyList<ContextDiagnostic>? diagnostics)
	{
		if (diagnostics is null || diagnostics.Count == 0)
			return false;
		foreach (var diagnostic in diagnostics)
		{
			var message = diagnostic.Code == GitScopeFilter.DeletedDiagnosticCode
				? _localization.Format("Terminal.Diagnostic.GitStateDeleted", diagnostic.Count ?? 0)
				: _localization["Terminal.Diagnostic.GitStateUnavailable"];
			_toastService.Show(message);
		}
		return diagnostics.Any(static diagnostic =>
			diagnostic.Severity == ContextDiagnosticSeverity.Error);
	}
}
