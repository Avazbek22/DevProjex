using DevProjex.Terminal.CommandLine;
using DevProjex.Terminal.Rendering;
using DevProjex.Application.Ranking;
using DevProjex.Application.Secrets;
using DevProjex.Application.Diagnostics;

namespace DevProjex.Terminal.Execution;

public sealed class ExportContextCommandHandler(
	TerminalServices services,
	ITerminalEnvironment environment,
	IImportanceRankingService? rankingService = null)
{
	public async Task<int> ExecuteAsync(
		ExportContextCommandRequest request,
		CancellationToken cancellationToken)
	{
		var status = new StatusRenderer(environment, request.Output);
		ProjectContextPlan plan;
		using (ContentPipelineDiagnostics.MeasureStage(ContentPipelineStage.Selection))
		{
			plan = await status
				.RunAsync(
					services.Localization["Terminal.Status.AnalyzingProject"],
					() => services.ContextFactory.BuildAsync(
						request.ProjectPath,
						request.Selection,
						cancellationToken: cancellationToken,
						repositorySourceUrl: request.RepositorySourceUrl))
				.ConfigureAwait(false);
		}
		plan = await ProjectFileSizeFilter.ApplyAsync(
				services.ContextPlanner,
				plan,
				request.MaxFileBytes,
				cancellationToken)
			.ConfigureAwait(false);
		var diagnosticRenderer = new ContextDiagnosticRenderer(
			environment,
			request.Output,
			services.Localization);
		if (plan.HasErrors)
		{
			diagnosticRenderer.Write(plan.Diagnostics);
			return CommandLineExitCodes.PolicyFailure;
		}
		var outputPath = request.OutputPath is not null and not "-"
			? ExactOutputDestinationValidator.ValidateContext(
				plan.SourceRoot,
				request.OutputPath,
				request.Force)
			: null;
		var requestedOutputPath = outputPath is not null
			? Path.GetFullPath(request.OutputPath!)
			: null;
		IReadOnlyList<FocusRankingSeedRequest>? focusSeeds;
		try
		{
			focusSeeds = ResolveFocusSeeds(plan, request.Focus);
		}
		catch (ProjectContextValidationException exception) when (request.Focus is not null)
		{
			diagnosticRenderer.Write([
				new ContextDiagnostic(
					exception.Code,
					ContextDiagnosticSeverity.Error,
					exception.Message,
					exception.ContextPath)
			]);
			return CommandLineExitCodes.PolicyFailure;
		}
		var ranking = request.Rank is null
			? null
			: focusSeeds is null
				? await (rankingService ?? new ImportanceRankingService(
					services.DependencyFactsEngine,
					new ProjectGitHistoryReader()))
					.RankAsync(plan.SourceRoot, plan.IncludedFiles, cancellationToken)
					.ConfigureAwait(false)
				: await (rankingService ?? new ImportanceRankingService(
						services.DependencyFactsEngine,
						new ProjectGitHistoryReader()))
					.RankAsync(
						plan.SourceRoot,
						plan.IncludedFiles,
						new FocusRankingRequest(focusSeeds),
						cancellationToken: cancellationToken)
					.ConfigureAwait(false);
		var transformationContext = CreateTransformationContext(plan, request.View);
		await using var measured = transformationContext is null ||
		                               (!request.DryRun && request.MaximumEstimatedTokens is null)
			? null
			: await services.SecretRedactionOutputPreparer
				.MeasureAsync(
					transformationContext,
					plan.IncludedFiles,
					captureEffectiveFindings: false,
					cancellationToken: cancellationToken)
				.ConfigureAwait(false);
		ProjectContextWriteResult? admissionResult = null;
		if (!request.DryRun && request.MaximumEstimatedTokens is { } admissionBudget && measured is not null)
		{
			var admission = await new ProjectContextTokenAdmissionService(services.ContextDocumentService)
				.AdmitMeasuredAsync(
					plan,
					request.View,
					request.Format,
					admissionBudget,
					measured,
					ranking,
					cancellationToken)
				.ConfigureAwait(false);
			plan = admission.Plan;
			admissionResult = admission.WriteResult;
		}
		await using var materialized = transformationContext is null || request.DryRun
			? null
			: await services.SecretRedactionOutputPreparer
				.PrepareAsync(
					transformationContext,
					plan.IncludedFiles,
					captureEffectiveFindings: false,
					captureTransformedMetrics: request.Format is
						ProjectContextDocumentFormat.Json or ProjectContextDocumentFormat.Xml,
					cancellationToken)
				.ConfigureAwait(false);
		var prepared = request.DryRun ? measured : materialized;
		if (prepared?.CompressionSnapshot is { } compressionSnapshot)
			plan = CodeCompressionDiagnostic.Append(plan, compressionSnapshot.Availability);
		diagnosticRenderer.Write(plan.Diagnostics);

		if (request.DryRun)
		{
			var redactionSnapshot = prepared?.Snapshot;
			ProjectContextWriteResult? budgetResult = null;
			if (request.MaximumEstimatedTokens is { } maximumEstimatedTokens)
			{
				// The measured branch goes through the same admission service a real export and the
				// MCP tools use, so there is one greedy pass in the product. Its narrowed plan is
				// deliberately discarded here: a dry run reports the complete plan it forecast.
				budgetResult = prepared is null
					? await services.ContextDocumentService.EvaluateTokenBudgetAsync(
							plan,
							request.View,
							request.Format,
							maximumEstimatedTokens,
							cancellationToken,
							ranking)
						.ConfigureAwait(false)
					: (await new ProjectContextTokenAdmissionService(services.ContextDocumentService)
						.AdmitMeasuredAsync(
							plan,
							request.View,
							request.Format,
							maximumEstimatedTokens,
							prepared,
							ranking,
							cancellationToken)
						.ConfigureAwait(false)).WriteResult;
			}
			if (prepared is not null)
				plan = ProjectContextDocumentService.ApplyMeasuredContentMetrics(plan, prepared);
			DryRunRenderer.WritePlan(
				environment,
				services.Localization,
				requestedOutputPath ?? "-",
				plan,
				cancellationToken);
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
			RankingOutput.Write(
				environment.Error,
				ranking,
				budgetResult?.TokenBudget,
				services.Localization);
			TokenBudgetOutput.Write(
				environment.Error,
				budgetResult?.TokenBudget,
				services.Localization,
				ranking);
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
						var writeResult = prepared is null
							? await services.ContextDocumentService.WriteCompleteWithReportAsync(
									plan,
									request.View,
									request.Format,
									destination,
									cancellationToken,
									plain: request.Output.Plain,
									useSourceMappedStructuredPaths: true,
									maximumEstimatedTokens: request.MaximumEstimatedTokens,
									ranking: ranking,
									precomputedTokenBudget: admissionResult?.TokenBudget)
								.ConfigureAwait(false)
							: await services.ContextDocumentService.WritePreparedCompleteAsync(
									plan,
									request.View,
									request.Format,
									destination,
									prepared,
									cancellationToken,
									plain: request.Output.Plain,
									useSourceMappedStructuredPaths: true,
									maximumEstimatedTokens: request.MaximumEstimatedTokens,
									ranking: ranking,
									precomputedTokenBudget: admissionResult?.TokenBudget,
									preserveContentMetrics: admissionResult is not null)
								.ConfigureAwait(false);
						if (admissionResult is not null)
							writeResult = writeResult with { UnscannableFiles = admissionResult.UnscannableFiles };
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
			RankingOutput.Write(environment.Error, ranking, report.TokenBudget, services.Localization);
			TokenBudgetOutput.Write(environment.Error, report.TokenBudget, services.Localization, ranking);
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
						writeReport = prepared is null
							? await services.ContextDocumentService.WriteCompleteWithReportAsync(
									plan,
									request.View,
									request.Format,
									destination,
									token,
									plain: request.Output.Plain,
									useSourceMappedStructuredPaths: true,
									maximumEstimatedTokens: request.MaximumEstimatedTokens,
									ranking: ranking,
									precomputedTokenBudget: admissionResult?.TokenBudget)
								.ConfigureAwait(false)
							: await services.ContextDocumentService.WritePreparedCompleteAsync(
									plan,
									request.View,
									request.Format,
									destination,
									prepared,
									token,
									plain: request.Output.Plain,
									useSourceMappedStructuredPaths: true,
									maximumEstimatedTokens: request.MaximumEstimatedTokens,
									ranking: ranking,
									precomputedTokenBudget: admissionResult?.TokenBudget,
									preserveContentMetrics: admissionResult is not null)
								.ConfigureAwait(false);
						if (admissionResult is not null && writeReport is not null)
							writeReport = writeReport with { UnscannableFiles = admissionResult.UnscannableFiles };
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
			RankingOutput.Write(
				environment.Error,
				ranking,
				writeReport.TokenBudget,
				services.Localization);
			TokenBudgetOutput.Write(
				environment.Error,
				writeReport.TokenBudget,
				services.Localization,
				ranking);
		}
		return CommandLineExitCodes.Success;
	}

	private static IReadOnlyList<FocusRankingSeedRequest>? ResolveFocusSeeds(
		ProjectContextPlan plan,
		IReadOnlyList<string>? requested)
	{
		if (requested is null)
			return null;
		var resolved = new List<FocusRankingSeedRequest>(requested.Count);
		foreach (var value in requested)
		{
			var validationPath = value;
			if (Path.IsPathFullyQualified(value))
			{
				var fullPath = Path.GetFullPath(value);
				if (!PathUtility.IsPathInside(fullPath, plan.SourceRoot))
				{
					throw new ProjectContextValidationException(
						"DPX-SELECTION-PATH-MISSING",
						"The focus seed is outside the project root.",
						value);
				}
				validationPath = PathUtility.GetPortableRelativePath(plan.SourceRoot, fullPath);
			}
			SelectedPathExistenceValidator.Validate(plan.SourceRoot, [validationPath]);
			var relative = ProjectSelectionPath.NormalizeRelative(validationPath);
			var full = Path.GetFullPath(Path.Combine(
				plan.SourceRoot,
				relative.Replace('/', Path.DirectorySeparatorChar)));
			var exact = plan.IncludedFiles.FirstOrDefault(candidate => PathComparer.Default.Equals(candidate, full));
			if (exact is null)
			{
				throw new ProjectContextValidationException(
					"DPX-SELECTION-PATH-MISSING",
					"The focus seed is outside the effective selection.",
					value);
			}
			resolved.Add(new FocusRankingSeedRequest(value, exact));
		}
		return resolved;
	}

	private ContentTransformationContext? CreateTransformationContext(
		ProjectContextPlan plan,
		ProjectContextView view)
	{
		if (view is not (ProjectContextView.Content or ProjectContextView.TreeContent))
			return null;

		var transformKinds = ContentDetailSelection.ResolveContextKinds(plan.Selection);
		var redactionFeatures = SecretRedactionFeatureSelection.Resolve(
			plan.Selection.HideSecrets == true,
			plan.Selection.HidePrivateData == true);
		return ContentTransformationContext.For(
			transformKinds != CodeTransformKinds.None
				? new CodeCompressionContext(
					plan.SourceRoot,
					services.CodeCompressionSession,
					transformKinds)
				{
					Policy = ContentDetailSelection.Resolve(plan.Selection)
				}
				: null,
			redactionFeatures != SecretRedactionFeatures.None
				? new SecretRedactionContext(
					plan.SourceRoot,
					services.SecretRedactionSession,
					redactionFeatures)
				: null);
	}
}
