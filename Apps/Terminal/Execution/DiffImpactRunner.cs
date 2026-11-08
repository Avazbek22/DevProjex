using DevProjex.Application.Diff;

namespace DevProjex.Terminal.Execution;

/// <summary>
/// Relates the files a diff changed across the readable project, so the CLI command and the
/// Terminal Workspace name the same dependents and tests.
/// </summary>
internal static class DiffImpactRunner
{
	/// <param name="projectPlan">
	/// The selection without its Git scope and narrowing; it bounds both the dependency index and
	/// every file the result may name.
	/// </param>
	public static async Task<DependencyChangeImpact> FindAsync(
		DependencyFactsEngine engine,
		ProjectContextPlan projectPlan,
		ProjectDiffResult diff,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(engine);
		ArgumentNullException.ThrowIfNull(projectPlan);
		ArgumentNullException.ThrowIfNull(diff);
		var changed = diff.Files
			.Where(static file => file.Status != ProjectDiffFileStatus.Deleted)
			.Select(static file => file.RelativePath)
			.ToArray();
		if (changed.Length == 0)
			return DependencyChangeImpact.None;

		var index = await engine.IndexAsync(
			projectPlan.SourceRoot,
			projectPlan.IncludedFiles,
			progress: null,
			cancellationToken,
			readableProjectFiles: projectPlan.IncludedFiles).ConfigureAwait(false);
		var candidates = projectPlan.IncludedFiles
			.Select(path => PathUtility.GetPortableRelativePath(projectPlan.SourceRoot, path))
			.ToHashSet(StringComparer.Ordinal);
		return DependencyChangeImpact.Compute(index, changed, candidates);
	}
}
