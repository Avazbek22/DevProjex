using DevProjex.Terminal.CommandLine;
using DevProjex.Terminal.Rendering;

namespace DevProjex.Terminal.Execution;

public sealed class ExportProjectCommandHandler(
	TerminalServices services,
	ITerminalEnvironment environment)
{
	public async Task<int> ExecuteAsync(
		ExportProjectCommandRequest request,
		CancellationToken cancellationToken)
	{
		if (request.Format == ProjectCopyExportFormat.Folder && request.Force)
		{
			throw new ProjectContextValidationException(
				"DPX-CLI-FORCE-NOT-SUPPORTED",
				"--force is supported only for ZIP project export.");
		}

		if (request.Format == ProjectCopyExportFormat.Zip &&
		    !request.OutputPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
		{
			throw new ProjectContextValidationException(
				"DPX-CLI-ZIP-EXTENSION-REQUIRED",
				"ZIP output must use the .zip extension.");
		}

		var plan = await new StatusRenderer(environment, request.Output)
			.RunAsync(
				services.Localization["Terminal.Status.PreparingProjectExport"],
				() => services.ContextFactory.BuildAsync(
					request.ProjectPath,
					request.Selection,
					cancellationToken: cancellationToken))
			.ConfigureAwait(false);
		new ContextDiagnosticRenderer(environment, request.Output, services.Localization)
			.Write(plan.Diagnostics);
		if (plan.HasErrors)
			return CommandLineExitCodes.PolicyFailure;

		var exactOutput = ExactOutputDestinationValidator.ValidateProject(
			plan.SourceRoot,
			request.OutputPath,
			request.Format,
			request.Force);
		if (request.DryRun)
		{
			DryRunRenderer.WritePlan(
				environment,
				services.Localization,
				exactOutput);
			return CommandLineExitCodes.Success;
		}

		var exportRequest = new ProjectCopyExportRequest(
			ProjectRootPath: plan.SourceRoot,
			ProjectName: plan.SourceIdentity?.DisplayName ??
			             Path.GetFileName(Path.TrimEndingDirectorySeparator(plan.SourceRoot)),
			TreeRoot: plan.ProjectedTree,
			SelectedPaths: new HashSet<string>(PathComparer.Default),
			DestinationPath: exactOutput,
			Format: request.Format,
			DestinationMode: ProjectCopyDestinationMode.Exact,
			ConflictPolicy: request.Force
				? ProjectCopyConflictPolicy.ReplaceAtomically
				: ProjectCopyConflictPolicy.Fail);
		var result = await new ProgressRenderer(environment, request.Output, services.Localization)
			.RunProjectExportAsync(progress =>
				services.ProjectCopyExportService.ExportAsync(
					exportRequest,
					progress,
					cancellationToken))
			.ConfigureAwait(false);
		environment.Output.WriteLine(Path.GetFullPath(result.DestinationPath));
		return CommandLineExitCodes.Success;
	}

}
