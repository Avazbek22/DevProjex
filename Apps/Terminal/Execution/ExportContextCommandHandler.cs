using DevProjex.Terminal.CommandLine;
using DevProjex.Terminal.Rendering;
using DevProjex.Application.Secrets;

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
		plan = await ProjectFileSizeFilter.ApplyAsync(
				services.ContextPlanner,
				plan,
				request.MaxFileBytes,
				cancellationToken)
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
			SecretRedactionSnapshot? redactionSnapshot = null;
			ProjectContextWriteResult? budgetResult = null;
			if (request.MaximumEstimatedTokens is { } maximumEstimatedTokens)
			{
				budgetResult = await services.ContextDocumentService.EvaluateTokenBudgetAsync(
						plan,
						request.View,
						request.Format,
						maximumEstimatedTokens,
						cancellationToken)
					.ConfigureAwait(false);
			}
			else
			{
				var redactionFeatures = SecretRedactionFeatureSelection.Resolve(
					plan.Selection.HideSecrets == true,
					plan.Selection.HidePrivateData == true);
				if (request.View is ProjectContextView.Content or ProjectContextView.TreeContent &&
				    redactionFeatures != SecretRedactionFeatures.None)
				{
					redactionSnapshot = await services.SecretRedactionOutputPreparer
						.AnalyzeAsync(
							new SecretRedactionContext(
								plan.SourceRoot,
								services.SecretRedactionSession,
								redactionFeatures),
							plan.IncludedFiles,
							cancellationToken)
						.ConfigureAwait(false);
				}
			}
			DryRunRenderer.WritePlan(
				environment,
				services.Localization,
				requestedOutputPath ?? "-",
				plan);
			var unscannableFiles = budgetResult?.UnscannableFiles ??
			                       redactionSnapshot?.UnscannableFiles;
			if (unscannableFiles is not null)
			{
				UnscannableFileOutput.Write(
					environment.Error,
					plan.SourceRoot,
					unscannableFiles,
					services.Localization);
			}
			TokenBudgetOutput.Write(
				environment.Error,
				budgetResult?.TokenBudget,
				services.Localization);
			return CommandLineExitCodes.Success;
		}

		if (request.OutputPath is null or "-")
		{
			var report = await status.RunAsync(
					services.Localization["Terminal.Status.BuildingContext"],
					async () =>
					{
						await using var destination = new Utf8TextWriterStream(
							environment.Output,
							cancellationToken);
						var writeResult = await services.ContextDocumentService.WriteCompleteWithReportAsync(
								plan,
								request.View,
								request.Format,
								destination,
								cancellationToken,
								plain: request.Output.Plain,
								useSourceMappedStructuredPaths: true,
								maximumEstimatedTokens: request.MaximumEstimatedTokens)
							.ConfigureAwait(false);
						await destination.CompleteAsync(cancellationToken).ConfigureAwait(false);
						return writeResult;
					})
				.ConfigureAwait(false);
			await environment.Output.WriteLineAsync().ConfigureAwait(false);
			UnscannableFileOutput.Write(
				environment.Error,
				plan.SourceRoot,
				report.UnscannableFiles,
				services.Localization);
			TokenBudgetOutput.Write(environment.Error, report.TokenBudget, services.Localization);
			return CommandLineExitCodes.Success;
		}

		ProjectContextWriteResult? writeReport = null;
		var writtenPath = await status.RunAsync(
				services.Localization["Terminal.Status.BuildingContext"],
				() => AtomicOutputWriter.WriteAsync(
					requestedOutputPath!,
					request.Force,
					async (destination, token) =>
					{
						writeReport = await services.ContextDocumentService.WriteCompleteWithReportAsync(
							plan,
							request.View,
							request.Format,
							destination,
							token,
							plain: request.Output.Plain,
							useSourceMappedStructuredPaths: true,
							maximumEstimatedTokens: request.MaximumEstimatedTokens).ConfigureAwait(false);
					},
					cancellationToken,
					path => ExactOutputDestinationValidator.ValidateContext(
						plan.SourceRoot,
						path,
						request.Force)))
			.ConfigureAwait(false);
		TerminalTextEscaping.WriteSingleLine(environment.Output, writtenPath);
		if (writeReport is not null)
		{
			UnscannableFileOutput.Write(
				environment.Error,
				plan.SourceRoot,
				writeReport.UnscannableFiles,
				services.Localization);
			TokenBudgetOutput.Write(
				environment.Error,
				writeReport.TokenBudget,
				services.Localization);
		}
		return CommandLineExitCodes.Success;
	}
}
