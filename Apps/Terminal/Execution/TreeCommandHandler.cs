using DevProjex.Terminal.CommandLine;
using DevProjex.Terminal.Rendering;

namespace DevProjex.Terminal.Execution;

public sealed class TreeCommandHandler(
	TerminalServices services,
	ITerminalEnvironment environment)
{
	public async Task<int> ExecuteAsync(
		TreeCommandRequest request,
		CancellationToken cancellationToken)
	{
		var plan = await new StatusRenderer(environment, request.Output)
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

		var payload = BuildPayload(plan, request.Format, request.Output.Plain);
		if (request.OutputPath is null or "-")
		{
			await environment.Output.WriteAsync(payload.AsMemory(), cancellationToken)
				.ConfigureAwait(false);
			return CommandLineExitCodes.Success;
		}

		var requestedPath = Path.GetFullPath(request.OutputPath);
		_ = ExactOutputDestinationValidator.ValidateAnalysis(plan.SourceRoot, requestedPath);
		var writtenPath = await AtomicOutputWriter.WriteTextAsync(
				requestedPath,
				payload,
				overwrite: false,
				cancellationToken,
				path => ExactOutputDestinationValidator.ValidateAnalysis(plan.SourceRoot, path))
			.ConfigureAwait(false);
		environment.Output.WriteLine(writtenPath);
		return CommandLineExitCodes.Success;
	}

	private string BuildPayload(
		ProjectContextPlan plan,
		TreeTextFormat format,
		bool plain)
	{
		var displayRootPath = plan.SourceIdentity is
		{
			SourceType: ProjectSourceType.GitClone,
			SourceReference.Length: > 0
		} identity
			? identity.SourceReference
			: plan.SourceRoot;
		var displayRootName = plan.SourceIdentity?.DisplayName;
		return plain && format == TreeTextFormat.Ascii
			? services.TreeExportService.BuildFullTreePlain(
				plan.SourceRoot,
				plan.ProjectedTree,
				displayRootPath,
				displayRootName)
			: services.TreeExportService.BuildFullTree(
				plan.SourceRoot,
				plan.ProjectedTree,
				format,
				displayRootPath,
				displayRootName);
	}
}
