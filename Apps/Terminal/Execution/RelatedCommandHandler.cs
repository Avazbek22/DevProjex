using DevProjex.Terminal.CommandLine;
using DevProjex.Terminal.Rendering;

namespace DevProjex.Terminal.Execution;

public sealed class RelatedCommandHandler(
	TerminalServices services,
	ITerminalEnvironment environment)
{
	public async Task<int> ExecuteAsync(RelatedCommandRequest request, CancellationToken cancellationToken)
	{
		var status = new StatusRenderer(environment, request.Output);
		var plan = await status.RunAsync(
			services.Localization["Terminal.Status.AnalyzingProject"],
			() => services.ContextFactory.BuildAsync(
				request.ProjectPath,
				request.Selection,
				includeOutputMetrics: false,
				cancellationToken: cancellationToken,
				repositorySourceUrl: request.RepositorySourceUrl)).ConfigureAwait(false);
		plan = await ProjectFileSizeFilter.ApplyAsync(
			services.ContextPlanner,
			plan,
			request.MaxFileBytes,
			cancellationToken).ConfigureAwait(false);
		new ContextDiagnosticRenderer(environment, request.Output, services.Localization).Write(plan.Diagnostics);
		if (plan.HasErrors) return CommandLineExitCodes.PolicyFailure;

		SelectedPathExistenceValidator.Validate(plan.SourceRoot, [request.SeedPath]);
		var relative = ProjectSelectionPath.NormalizeRelative(request.SeedPath);
		var fullPath = Path.GetFullPath(Path.Combine(plan.SourceRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
		var exact = plan.IncludedFiles.FirstOrDefault(candidate => PathComparer.Default.Equals(candidate, fullPath));
		if (exact is null)
			throw new ProjectContextValidationException(
				"DPX-SELECTION-PATH-MISSING",
				"The related seed is outside the effective selection.",
				request.SeedPath);
		relative = PathUtility.GetPortableRelativePath(plan.SourceRoot, exact);
		var related = await status.RunAsync(
			services.Localization["Terminal.Status.IndexingDependencies"],
			() => services.DependencyFactsEngine.FindRelatedAsync(
				plan.SourceRoot,
				plan.IncludedFiles,
				[relative],
				request.Direction,
				cancellationToken: cancellationToken)).ConfigureAwait(false);
		var seed = related.Seeds.Single();
		if (seed.NoFactsReason is { Length: > 0 })
		{
			environment.Error.WriteLine(
				"warning[DPX-DEPENDENCY-UNSUPPORTED]: " +
				TerminalTextEscaping.EscapeSingleLine(services.Localization["Terminal.Related.NoFacts"]));
		}
		await RelatedOutputRenderer.WriteAsync(
			environment.Output,
			related,
			request.Direction,
			request.Format,
			services.Localization,
			cancellationToken).ConfigureAwait(false);
		return CommandLineExitCodes.Success;
	}
}
