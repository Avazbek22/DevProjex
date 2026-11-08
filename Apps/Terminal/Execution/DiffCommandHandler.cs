using DevProjex.Application.Diff;
using DevProjex.Terminal.CommandLine;
using DevProjex.Terminal.Rendering;

namespace DevProjex.Terminal.Execution;

public sealed class DiffCommandHandler(
	TerminalServices services,
	ITerminalEnvironment environment)
{
	public async Task<int> ExecuteAsync(DiffCommandRequest request, CancellationToken cancellationToken)
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
		// Captured before ProjectFileSizeFilter replaces the plan instance, which would lose the
		// association between the plan and its resolved remote commit range.
		var resolvedDiffRange = services.ContextFactory.TryGetResolvedDiffRange(plan);
		plan = await ProjectFileSizeFilter.ApplyAsync(
			services.ContextPlanner,
			plan,
			request.MaxFileBytes,
			cancellationToken).ConfigureAwait(false);
		new ContextDiagnosticRenderer(environment, request.Output, services.Localization).Write(plan.Diagnostics);
		if (plan.HasErrors)
			return CommandLineExitCodes.PolicyFailure;

		var destination = request.OutputPath is null or "-"
			? null
			: Path.GetFullPath(request.OutputPath);
		if (destination is not null)
			_ = ExactOutputDestinationValidator.ValidateAnalysis(plan.SourceRoot, destination, overwrite: false);
		ProjectDiffResult diff;
		try
		{
			diff = await status.RunAsync(
				services.Localization["Terminal.Status.ComparingVersions"],
				() => DiffQueryRunner.CompareAsync(services, plan, request.ContextLines, cancellationToken, resolvedDiffRange))
				.ConfigureAwait(false);
		}
		catch (ProjectDiffException exception)
		{
			new ErrorRenderer(environment, request.Output, services.Localization).Write(new TerminalError(
				GitScopeFilter.UnavailableDiagnosticCode,
				services.Localization["Terminal.Diff.Unavailable"],
				ExitCode: CommandLineExitCodes.PolicyFailure,
				Exception: exception));
			return CommandLineExitCodes.PolicyFailure;
		}

		var affected = diff.HasChanges
			? await status.RunAsync(
				services.Localization["Terminal.Status.IndexingDependencies"],
				() => FindAffectedAsync(request, diff, cancellationToken)).ConfigureAwait(false)
			: null;
		var payload = request.Format == AnalysisOutputFormat.Json
			? DiffOutputRenderer.FormatJson(diff, affected)
			: DiffOutputRenderer.FormatText(diff, services.Localization, affected);
		if (destination is null)
		{
			await environment.Output.WriteAsync(payload.AsMemory(), cancellationToken).ConfigureAwait(false);
			await environment.Output.FlushAsync(cancellationToken).ConfigureAwait(false);
			return CommandLineExitCodes.Success;
		}

		var writtenPath = await AtomicOutputWriter.WriteTextAsync(
			destination,
			payload,
			overwrite: false,
			cancellationToken,
			path => ExactOutputDestinationValidator.ValidateAnalysis(plan.SourceRoot, path, overwrite: false))
			.ConfigureAwait(false);
		TerminalTextEscaping.WriteSingleLine(environment.Output, writtenPath);
		return CommandLineExitCodes.Success;
	}

	// The affected files add to the diff; a project that cannot be related still prints its diff.
	private async Task<DependencyChangeImpact> FindAffectedAsync(
		DiffCommandRequest request,
		ProjectDiffResult diff,
		CancellationToken cancellationToken)
	{
		if (request.ProjectSelection is null)
			return DependencyChangeImpact.None;
		try
		{
			var projectPlan = await services.ContextFactory.BuildAsync(
				request.ProjectPath,
				request.ProjectSelection,
				includeOutputMetrics: false,
				cancellationToken: cancellationToken,
				repositorySourceUrl: request.RepositorySourceUrl).ConfigureAwait(false);
			projectPlan = await ProjectFileSizeFilter.ApplyAsync(
				services.ContextPlanner,
				projectPlan,
				request.MaxFileBytes,
				cancellationToken).ConfigureAwait(false);
			return projectPlan.HasErrors
				? DependencyChangeImpact.None
				: await DiffImpactRunner.FindAsync(services.DependencyFactsEngine, projectPlan, diff, cancellationToken)
					.ConfigureAwait(false);
		}
		catch (Exception exception) when (exception is not OperationCanceledException)
		{
			return DependencyChangeImpact.None;
		}
	}
}
