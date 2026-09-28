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
}
