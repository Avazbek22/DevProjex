using DevProjex.Terminal.CommandLine;
using DevProjex.Terminal.Rendering;
using DevProjex.Application.Compression;
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
		    request.OutputPath != "-" &&
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

		var writesToStandardOutput = request.Format == ProjectCopyExportFormat.Zip &&
		                             request.OutputPath == "-";
		if (request.OutputPath == "-" && !writesToStandardOutput)
		{
			throw new ProjectContextValidationException(
				"DPX-CLI-FOLDER-STDOUT-NOT-SUPPORTED",
				"Folder export cannot write to stdout.");
		}
		if (!writesToStandardOutput)
		{
			_ = ExactOutputDestinationValidator.ValidateProject(
				plan.SourceRoot,
				request.OutputPath,
				request.Format,
				request.Force);
		}
		var requestedOutput = writesToStandardOutput ? "-" : Path.GetFullPath(request.OutputPath);
		if (request.DryRun)
		{
			var redactionFeatures = SecretRedactionFeatureSelection.Resolve(
				plan.Selection.HideSecrets == true,
				plan.Selection.HidePrivateData == true);
			var redactContent = redactionFeatures != SecretRedactionFeatures.None;
			IReadOnlyList<UnscannableFile> unscannableFiles = [];
			if (redactContent)
			{
				var preflight = await services.SecretRedactionOutputPreparer
					.AnalyzeAsync(
						new SecretRedactionContext(
							plan.SourceRoot,
							services.SecretRedactionSession,
							redactionFeatures),
						plan.IncludedFiles,
						cancellationToken)
					.ConfigureAwait(false);
				unscannableFiles = preflight.UnscannableFiles;
			}
			DryRunRenderer.WritePlan(
				environment,
				services.Localization,
				requestedOutput,
				plan);
			if (redactContent)
			{
				environment.Error.WriteLine(
					services.Localization["Terminal.DryRun.ProjectCopy.RedactionWarning"]);
				if (unscannableFiles.Count > 0)
				{
					environment.Error.WriteLine(
						services.Localization["ProjectCopy.Notice.UnscannableExcluded"]);
				}
				UnscannableFileOutput.Write(
					environment.Error,
					plan.SourceRoot,
					unscannableFiles,
					services.Localization);
			}

			if (CodeTransformIdentity.Resolve(
				    plan.Selection.CompressCode == true,
				    plan.Selection.StripComments == true,
				    plan.Selection.StripBlankLines == true) != CodeTransformKinds.None)
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
			RedactPrivateData: plan.Selection.HidePrivateData == true,
			CompressCode: plan.Selection.CompressCode == true,
			StripComments: plan.Selection.StripComments == true,
			StripBlankLines: plan.Selection.StripBlankLines == true,
			NoticeText: ProjectCopyExportService.BuildProjectCopyNoticeText(services.Localization));
		if (writesToStandardOutput)
		{
			var rawOutput = environment.RawOutput ?? throw new ProjectContextValidationException(
				"DPX-CLI-BINARY-STDOUT-UNAVAILABLE",
				"Binary stdout is unavailable in this host.");
			await environment.Output.FlushAsync(cancellationToken).ConfigureAwait(false);
			var streamedResult = await new ProgressRenderer(environment, request.Output, services.Localization)
				.RunProjectExportAsync(progress =>
					services.ProjectCopyExportService.ExportZipToStreamAsync(
						exportRequest,
						rawOutput,
						progress,
						cancellationToken))
				.ConfigureAwait(false);
			UnscannableFileOutput.Write(
				environment.Error,
				plan.SourceRoot,
				streamedResult.UnscannableFiles ?? [],
				services.Localization);
			return CommandLineExitCodes.Success;
		}
		var result = await new ProgressRenderer(environment, request.Output, services.Localization)
			.RunProjectExportAsync(progress =>
				services.ProjectCopyExportService.ExportAsync(
					exportRequest,
					progress,
					cancellationToken))
			.ConfigureAwait(false);
		TerminalTextEscaping.WriteSingleLine(
			environment.Output,
			Path.GetFullPath(result.DestinationPath));
		if (result.UnscannableFiles is { Count: > 0 })
		{
			environment.Error.WriteLine(
				services.Localization["ProjectCopy.Notice.UnscannableExcluded"]);
		}
		UnscannableFileOutput.Write(
			environment.Error,
			plan.SourceRoot,
			result.UnscannableFiles ?? [],
			services.Localization);
		return CommandLineExitCodes.Success;
	}

}
