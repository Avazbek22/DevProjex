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
				() => services.ContextPlanner.BuildAsync(
					new ProjectContextRequest(request.ProjectPath, request.Selection),
					cancellationToken))
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

		if (request.DryRun && outputPath is not null)
		{
			environment.Output.WriteLine(outputPath);
			return CommandLineExitCodes.Success;
		}

		var payload = await status
			.RunAsync(
				services.Localization["Terminal.Status.BuildingContext"],
				() => services.ContextDocumentService.BuildAsync(
					plan,
					request.View,
					request.Format,
					cancellationToken))
			.ConfigureAwait(false);
		if (request.OutputPath is null or "-")
		{
			await environment.Output.WriteAsync(payload.AsMemory(), cancellationToken).ConfigureAwait(false);
			if (!payload.EndsWith('\n'))
				await environment.Output.WriteLineAsync().ConfigureAwait(false);
			return CommandLineExitCodes.Success;
		}

		var writtenPath = await AtomicOutputWriter
			.WriteTextAsync(
				outputPath!,
				payload,
				request.Force,
				cancellationToken,
				path => ExactOutputDestinationValidator.ValidateContext(
					plan.SourceRoot,
					path,
					request.Force))
			.ConfigureAwait(false);
		environment.Output.WriteLine(writtenPath);
		return CommandLineExitCodes.Success;
	}
}
