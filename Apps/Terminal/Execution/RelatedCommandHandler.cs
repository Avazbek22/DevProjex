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

		var destination = request.OutputPath is null or "-"
			? null
			: Path.GetFullPath(request.OutputPath);
		if (destination is not null)
			_ = ExactOutputDestinationValidator.ValidateAnalysis(plan.SourceRoot, destination, overwrite: false);
		var relative = RelatedQueryRunner.ResolveSeed(plan, request.SeedPath);
		DependencyRelatedResult related;
		DependencyOutsideSelectionResult? outside;
		try
		{
			related = await status.RunAsync(
			services.Localization["Terminal.Status.IndexingDependencies"],
				() => RelatedQueryRunner.FindAsync(
					services.DependencyFactsEngine,
					plan,
					relative,
				request.Direction,
					request.Depth,
					cancellationToken)).ConfigureAwait(false);
			// The project's relations can lead a deeper walk to more selected files, under the same limit.
			outside = await status.RunAsync(
				services.Localization["Terminal.Status.IndexingDependencies"],
				() => FindOutsideSelectionAsync(request, plan, related, relative, cancellationToken)).ConfigureAwait(false);
		}
		catch (DependencyTraversalLimitException exception)
		{
			new ErrorRenderer(environment, request.Output, services.Localization).Write(new TerminalError(
				DependencyTraversalLimitException.ErrorCode,
				services.Localization.Format("Terminal.Related.TraversalLimit", exception.MaximumSeeds),
				ExitCode: CommandLineExitCodes.PolicyFailure,
				Exception: exception));
			return CommandLineExitCodes.PolicyFailure;
		}
		if (related.Seeds.Any(static seed => seed.NoFactsReason is { Length: > 0 }))
		{
			environment.Error.WriteLine(
				"warning[DPX-DEPENDENCY-UNSUPPORTED]: " +
				TerminalTextEscaping.EscapeSingleLine(services.Localization["Terminal.Related.NoFacts"]));
		}
		if (destination is null)
		{
			await RelatedOutputRenderer.WriteAsync(
				environment.Output,
				related,
				outside,
				request.Direction,
				request.Format,
				services.Localization,
				cancellationToken).ConfigureAwait(false);
			return CommandLineExitCodes.Success;
		}

		await using var payload = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
		await RelatedOutputRenderer.WriteAsync(
			payload,
			related,
			outside,
			request.Direction,
			request.Format,
			services.Localization,
			cancellationToken).ConfigureAwait(false);
		var writtenPath = await AtomicOutputWriter.WriteTextAsync(
			destination,
			payload.ToString(),
			overwrite: false,
			cancellationToken,
			path => ExactOutputDestinationValidator.ValidateAnalysis(plan.SourceRoot, path, overwrite: false))
			.ConfigureAwait(false);
		TerminalTextEscaping.WriteSingleLine(environment.Output, writtenPath);
		return CommandLineExitCodes.Success;
	}

	// A narrowed selection cannot see part of the project, so the same project without that
	// narrowing answers for the seed's relations; the selection only places each related file.
	private async Task<DependencyOutsideSelectionResult?> FindOutsideSelectionAsync(
		RelatedCommandRequest request,
		ProjectContextPlan plan,
		DependencyRelatedResult related,
		string seed,
		CancellationToken cancellationToken)
	{
		if (request.ProjectSelection is null ||
			!DependencyFactsEngine.CanRelateOutsideSelection(related))
			return null;

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
		var selected = plan.IncludedFiles.ToHashSet(PathComparer.Default);
		if (projectPlan.HasErrors || projectPlan.IncludedFiles.All(selected.Contains))
			return null;

		return await services.DependencyFactsEngine.FindOutsideSelectionAsync(
			plan.SourceRoot,
			related,
			projectPlan.IncludedFiles,
			[seed],
			request.Direction,
			request.Depth,
			cancellationToken: cancellationToken).ConfigureAwait(false);
	}
}
