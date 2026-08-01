using DevProjex.Terminal.CommandLine;
using DevProjex.Terminal.Rendering;

namespace DevProjex.Terminal.Execution;

public sealed class ExportContextCommandHandler(
	TerminalServices services,
	ITerminalEnvironment environment)
{
	public async Task<int> ExecuteAsync(
		ExportContextCommandRequest request,
		CancellationToken cancellationToken)
	{
		var status = new StatusRenderer(environment, request.Output);
		var plan = await status
			.RunAsync(
				services.Localization["Terminal.Status.AnalyzingProject"],
				() => services.ContextFactory.BuildAsync(
					request.ProjectPath,
					request.Selection,
					cancellationToken: cancellationToken))
			.ConfigureAwait(false);
		new ContextDiagnosticRenderer(environment, request.Output, services.Localization)
			.Write(plan.Diagnostics);
		if (plan.HasErrors)
			return CommandLineExitCodes.PolicyFailure;

		var outputPath = request.OutputPath is not null and not "-"
			? ExactOutputDestinationValidator.ValidateContext(
				plan.SourceRoot,
				request.OutputPath,
				request.Force)
			: null;
		var requestedOutputPath = outputPath is not null
			? Path.GetFullPath(request.OutputPath!)
			: null;

		if (request.DryRun)
		{
			DryRunRenderer.WritePlan(
				environment,
				services.Localization,
				requestedOutputPath ?? "-");
			return CommandLineExitCodes.Success;
		}

		if (request.OutputPath is null or "-")
		{
			await status.RunAsync(
					services.Localization["Terminal.Status.BuildingContext"],
					async () =>
					{
						await using var destination = new Utf8TextWriterStream(
							environment.Output,
							cancellationToken);
						await services.ContextDocumentService.WriteCompleteAsync(
								plan,
								request.View,
								request.Format,
								destination,
								cancellationToken,
								plain: request.Output.Plain)
							.ConfigureAwait(false);
						await destination.CompleteAsync(cancellationToken).ConfigureAwait(false);
						return true;
					})
				.ConfigureAwait(false);
			await environment.Output.WriteLineAsync().ConfigureAwait(false);
			return CommandLineExitCodes.Success;
		}

		var writtenPath = await status.RunAsync(
				services.Localization["Terminal.Status.BuildingContext"],
				() => AtomicOutputWriter.WriteAsync(
					requestedOutputPath!,
					request.Force,
					(destination, token) =>
						services.ContextDocumentService.WriteCompleteAsync(
							plan,
							request.View,
							request.Format,
							destination,
							token,
							plain: request.Output.Plain),
					cancellationToken,
					path => ExactOutputDestinationValidator.ValidateContext(
						plan.SourceRoot,
						path,
						request.Force)))
			.ConfigureAwait(false);
		environment.Output.WriteLine(writtenPath);
		return CommandLineExitCodes.Success;
	}
}
