using DevProjex.Terminal.CommandLine;
using DevProjex.Terminal.Rendering;
using DevProjex.Application.Secrets;

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

		_ = ExactOutputDestinationValidator.ValidateProject(
			plan.SourceRoot,
			request.OutputPath,
			request.Format,
			request.Force);
		var requestedOutput = Path.GetFullPath(request.OutputPath);
		if (request.DryRun)
		{
			var redactSecrets = plan.Selection.HideSecrets == true;
			if (redactSecrets)
			{
				var preflight = await services.SecretRedactionOutputPreparer
					.AnalyzeAsync(
						new SecretRedactionContext(plan.SourceRoot, services.SecretRedactionSession),
						plan.IncludedFiles,
						cancellationToken)
					.ConfigureAwait(false);
				// A dry run has to predict the real run. The copy refuses a file the scanner may not
				// read, so the preflight has to refuse it too rather than report readiness.
				if (preflight.UnscannablePath is { } unscannablePath)
				{
					throw new SecretScanLimitExceededException(
						unscannablePath,
						new FileInfo(unscannablePath).Length,
						SecretRedactionOutputPreparer.MaximumScannableFileBytes);
				}
			}
			DryRunRenderer.WritePlan(
				environment,
				services.Localization,
				requestedOutput);
			if (redactSecrets)
			{
				environment.Error.WriteLine(
					services.Localization["Terminal.DryRun.ProjectCopy.RedactionWarning"]);
			}

			if (plan.Selection.CompressCode == true)
				environment.Error.WriteLine(services.Localization["Compression.CopyNotice"]);
			return CommandLineExitCodes.Success;
		}

		var exportRequest = new ProjectCopyExportRequest(
			ProjectRootPath: plan.SourceRoot,
			ProjectName: plan.SourceIdentity?.DisplayName ??
			             Path.GetFileName(Path.TrimEndingDirectorySeparator(plan.SourceRoot)),
			TreeRoot: plan.ProjectedTree,
			SelectedPaths: new HashSet<string>(PathComparer.Default),
			DestinationPath: requestedOutput,
			Format: request.Format,
			DestinationMode: ProjectCopyDestinationMode.Exact,
			ConflictPolicy: request.Force
				? ProjectCopyConflictPolicy.ReplaceAtomically
				: ProjectCopyConflictPolicy.Fail,
			RedactSecrets: plan.Selection.HideSecrets == true,
			CompressCode: plan.Selection.CompressCode == true,
			NoticeText: ProjectCopyExportService.BuildProjectCopyNoticeText(services.Localization));
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
