using DevProjex.Terminal.Execution;

namespace DevProjex.Terminal.CommandLine;

internal static class DesktopOpenGitReadinessValidator
{
	public static async Task<IReadOnlyList<ContextDiagnostic>> ValidateAsync(
		TerminalServices services,
		string projectPath,
		ProjectSelectionSpec selection,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(services);
		ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
		ArgumentNullException.ThrowIfNull(selection);
		if (selection.GitMode is not (GitFilteringMode.TrackedFilesOnly or
			GitFilteringMode.Staged or GitFilteringMode.Changes))
		{
			return [];
		}

		var plan = await services.ContextFactory
			.BuildAsync(
				projectPath,
				selection,
				includeOutputMetrics: false,
				cancellationToken: cancellationToken)
			.ConfigureAwait(false);
		return plan.Diagnostics
			.Where(diagnostic => selection.GitMode == GitFilteringMode.TrackedFilesOnly
				? diagnostic.Code is ProjectContextGitReadiness.UnavailableDiagnosticCode or
					ProjectContextGitReadiness.PartialDiagnosticCode
				: diagnostic.Code is GitScopeFilter.UnavailableDiagnosticCode or
					GitScopeFilter.UnsafeFilterDiagnosticCode)
			.ToArray();
	}
}
