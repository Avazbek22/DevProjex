using DevProjex.Terminal.CommandLine;
using DevProjex.Terminal.Rendering;
using DevProjex.Application.Ranking;
using DevProjex.Application.Secrets;

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
		var plan = await status
			.RunAsync(
				services.Localization["Terminal.Status.AnalyzingProject"],
				() => services.ContextFactory.BuildAsync(
					request.ProjectPath,
					request.Selection,
					cancellationToken: cancellationToken,
					repositorySourceUrl: request.RepositorySourceUrl))
			.ConfigureAwait(false);
		plan = await ProjectFileSizeFilter.ApplyAsync(
				services.ContextPlanner,
				plan,
				request.MaxFileBytes,
				cancellationToken)
			.ConfigureAwait(false);
		if (plan.HasErrors)
		{
			new ContextDiagnosticRenderer(environment, request.Output, services.Localization)
				.Write(plan.Diagnostics);
			return CommandLineExitCodes.PolicyFailure;
		}
		var focusSeeds = ResolveFocusSeeds(plan, request.Focus);
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
		await using var prepared = transformationContext is null
			? null
			: await services.SecretRedactionOutputPreparer
				.PrepareAsync(transformationContext, plan.IncludedFiles, cancellationToken)
				.ConfigureAwait(false);
		if (prepared?.CompressionSnapshot is { } compressionSnapshot)
			plan = CodeCompressionDiagnostic.Append(plan, compressionSnapshot.Availability);
		new ContextDiagnosticRenderer(environment, request.Output, services.Localization)
			.Write(plan.Diagnostics);

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
			var redactionSnapshot = prepared?.Snapshot;
			ProjectContextWriteResult? budgetResult = null;
			if (request.MaximumEstimatedTokens is { } maximumEstimatedTokens)
			{
				budgetResult = prepared is null
					? await services.ContextDocumentService.EvaluateTokenBudgetAsync(
							plan,
							request.View,
							request.Format,
							maximumEstimatedTokens,
							cancellationToken,
							ranking)
						.ConfigureAwait(false)
					: await services.ContextDocumentService.WritePreparedCompleteAsync(
							plan,
							request.View,
							request.Format,
							Stream.Null,
							prepared,
							cancellationToken,
							maximumEstimatedTokens: maximumEstimatedTokens,
							ranking: ranking)
						.ConfigureAwait(false);
			}
			else if (prepared is null)
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
									ranking: ranking)
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
									ranking: ranking)
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
									ranking: ranking)
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
									ranking: ranking)
								.ConfigureAwait(false);
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

		var transformKinds = CodeTransformIdentity.Resolve(
			plan.Selection.CompressCode == true,
			plan.Selection.StripComments == true,
			plan.Selection.StripBlankLines == true);
		var redactionFeatures = SecretRedactionFeatureSelection.Resolve(
			plan.Selection.HideSecrets == true,
			plan.Selection.HidePrivateData == true);
		return ContentTransformationContext.For(
			transformKinds != CodeTransformKinds.None
				? new CodeCompressionContext(
					plan.SourceRoot,
					services.CodeCompressionSession,
					transformKinds)
				: null,
			redactionFeatures != SecretRedactionFeatures.None
				? new SecretRedactionContext(
					plan.SourceRoot,
					services.SecretRedactionSession,
					redactionFeatures)
				: null);
	}
}
