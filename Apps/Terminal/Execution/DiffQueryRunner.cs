using DevProjex.Application.Diff;
using DevProjex.Application.Secrets;

namespace DevProjex.Terminal.Execution;

/// <summary>
/// The comparison the CLI command and the Terminal Workspace share: the plan's own Git scope,
/// the protection its selection enables, and the same read every other content command uses.
/// </summary>
internal static class DiffQueryRunner
{
	public const int DefaultContextLines = 3;
	public const int MaximumContextLines = 20;

	public static Task<ProjectDiffResult> CompareAsync(
		TerminalServices services,
		ProjectContextPlan plan,
		int contextLines,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(services);
		ArgumentNullException.ThrowIfNull(plan);
		var mode = plan.Selection.GitMode ?? GitFilteringMode.None;
		var features = SecretRedactionFeatureSelection.Resolve(
			plan.Selection.HideSecrets == true,
			plan.Selection.HidePrivateData == true);
		var redaction = features == SecretRedactionFeatures.None
			? null
			: new SecretRedactionContext(plan.SourceRoot, services.SecretRedactionSession, features);
		var workingFiles = new FileContentAnalyzer();
		return new ProjectDiffService(new GitFileVersionReader()).CompareAsync(
			new ProjectDiffRequest(
				plan,
				mode,
				mode == GitFilteringMode.Diff ? plan.Selection.GitDiffRange : null,
				contextLines,
				redaction,
				(path, token) => workingFiles.ReadClassifiedAsync(
					path,
					SecretRedactionOutputPreparer.MaximumScannableFileBytes,
					token)),
			cancellationToken);
	}

	/// <summary>Narrows an already built plan to a momentary Git scope.</summary>
	public static Task<ProjectContextPlan> ApplyScopeAsync(
		ProjectContextPlanner planner,
		ProjectContextPlan plan,
		GitFilteringMode mode,
		string? diffRange,
		CancellationToken cancellationToken) =>
		GitScopeFilter.ApplyAsync(planner, plan, new GitScopePathProvider(), mode, diffRange, cancellationToken);
}
