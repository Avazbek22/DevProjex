using DevProjex.Terminal.CommandLine;
using DevProjex.Terminal.Rendering;
using DevProjex.Application.Secrets;

namespace DevProjex.Terminal.Execution;

public sealed class AnalyzeCommandHandler(
	TerminalServices services,
	ITerminalEnvironment environment)
{
	public async Task<int> ExecuteAsync(
		AnalyzeCommandRequest request,
		CancellationToken cancellationToken)
	{
		var includeSourceContentMetrics = !HasContentTransformations(request.Selection);
		var topFileRanking = request.TopFiles is { } topFileCount
			? new TopFileRanking(topFileCount)
			: null;
		Action<ContentFileMetrics>? topFileObserver = topFileRanking is null
			? null
			: metrics => topFileRanking.Add(
				metrics.Path,
				CodeCompressionSnapshot.EstimateTokens(metrics.CharCount));
		var plan = await new StatusRenderer(environment, request.Output)
			.RunAsync(
				services.Localization["Terminal.Status.AnalyzingProject"],
				() => services.ContextFactory.BuildAsync(
					request.ProjectPath,
					request.Selection,
					includeOutputMetrics: true,
					cancellationToken: cancellationToken,
					includeContentOutputMetrics: includeSourceContentMetrics && topFileRanking is null,
					repositorySourceUrl: request.RepositorySourceUrl))
			.ConfigureAwait(false);
		plan = await ProjectFileSizeFilter.ApplyAsync(
				services.ContextPlanner,
				plan,
				request.MaxFileBytes,
				cancellationToken)
			.ConfigureAwait(false);
		var transformationContext = CreateTransformationContext(plan);
		if (!includeSourceContentMetrics && transformationContext is null)
		{
			// Resolved CLI selections normally make this branch unreachable. Keeping the fallback
			// prevents a future profile-normalization change from publishing empty content metrics.
			plan = await services.ContextFactory.BuildAsync(
					request.ProjectPath,
					request.Selection,
					includeOutputMetrics: true,
					cancellationToken: cancellationToken,
					includeContentOutputMetrics: topFileRanking is null,
					repositorySourceUrl: request.RepositorySourceUrl)
				.ConfigureAwait(false);
			transformationContext = CreateTransformationContext(plan);
		}
		var findingsRequested = request.IncludeFindings || request.FailOnFindings;
		IReadOnlyList<EffectiveRedactionFinding> effectiveFindings = [];
		var effectiveFindingCount = 0;
		var findingsCapturedByOutput = false;
		if (transformationContext is not null)
		{
			await using var prepared = await services.SecretRedactionOutputPreparer
				.PrepareAsync(
					transformationContext,
					plan.IncludedFiles,
					request.IncludeFindings && plan.Selection.HideSecrets == true,
					cancellationToken)
				.ConfigureAwait(false);
			var transformedAnalyzer = services.SecretRedactionOutputPreparer.CreatePreparedAnalyzer(prepared);
			if (findingsRequested && plan.Selection.HideSecrets == true)
			{
				effectiveFindingCount = prepared.Snapshot?.DetectedCount ?? 0;
				if (request.IncludeFindings)
				{
					effectiveFindings = prepared.GetEffectiveFindings();
					EnsureFindingCountMatches(effectiveFindings, effectiveFindingCount);
				}
				findingsCapturedByOutput = true;
			}
			var transformedMetrics = await ProjectContentMetricsCalculator
				.CalculateAsync(
					transformedAnalyzer,
					plan.IncludedFiles,
					topFileObserver,
					progress: null,
					cancellationToken)
				.ConfigureAwait(false);
			plan = plan with
			{
				Analysis = plan.Analysis with
				{
					Metrics = plan.Analysis.Metrics with
					{
						Content = new ProjectOutputMetricsReport(
							transformedMetrics.Lines,
							transformedMetrics.Chars,
							transformedMetrics.Tokens)
					}
				},
				Redaction = prepared.Snapshot is { } redaction
					  && plan.Selection.HideSecrets == true
					? new SecretRedactionSummary(redaction.SecretDetectedCount, redaction.SecretRedactedCount)
					: null,
				Privacy = prepared.Snapshot is { } privacy && plan.Selection.HidePrivateData == true
					? new PrivateDataRedactionSummary(
						privacy.PrivateDataDetectedCount,
						privacy.PrivateDataRedactedCount)
					: null,
				Compression = prepared.CompressionSnapshot is { } compression
					? new CodeCompressionSummary(
						compression.CompressedFiles,
						compression.UnchangedFiles,
						compression.SourceCharacters,
						compression.TransformedCharacters,
						compression.BodyTransformedFiles,
						compression.CommentTransformedFiles,
						compression.BlankLineTransformedFiles)
					: null,
				UnscannableFiles = prepared.UnscannableFiles
			};
		}
		else if (topFileRanking is not null)
		{
			var sourceMetrics = await services.AnalysisService
				.CalculateContentMetricsAsync(
					plan.IncludedFiles,
					topFileObserver,
					cancellationToken)
				.ConfigureAwait(false);
			plan = plan with
			{
				Analysis = plan.Analysis with
				{
					Metrics = plan.Analysis.Metrics with
					{
						Content = new ProjectOutputMetricsReport(
							sourceMetrics.Lines,
							sourceMetrics.Chars,
							sourceMetrics.Tokens)
					}
				}
			};
		}

		if (topFileRanking is not null)
		{
			plan = plan with
			{
				TopFiles = topFileRanking.Project(item => new TopFileMetric(
					PathUtility.GetPortableRelativePath(plan.SourceRoot, item.Path),
					item.Tokens))
			};
		}

		if (findingsRequested && !findingsCapturedByOutput)
		{
			var detectionContext = CreateTransformationContext(plan, forceSecretDetection: true) ??
			                       throw new InvalidOperationException(
				                       "Finding detection did not create a transformation context.");
			SecretRedactionSnapshot detectionSnapshot;
			IReadOnlyList<UnscannableFile> detectionUnscannableFiles;
			if (request.IncludeFindings)
			{
				await using var prepared = await services.SecretRedactionOutputPreparer
					.InspectAsync(
						detectionContext,
						plan.IncludedFiles,
						captureEffectiveFindings: true,
						cancellationToken)
					.ConfigureAwait(false);
				detectionSnapshot = prepared.Snapshot ??
				                    throw new SecretDetectionException(
					                    "Finding detection completed without a snapshot.");
				effectiveFindings = prepared.GetEffectiveFindings();
				detectionUnscannableFiles = prepared.UnscannableFiles;
			}
			else
			{
				detectionSnapshot = await services.SecretRedactionOutputPreparer
					.DiscoverAsync(detectionContext, plan.IncludedFiles, cancellationToken)
					.ConfigureAwait(false);
				detectionUnscannableFiles = detectionSnapshot.UnscannableFiles;
			}

			effectiveFindingCount = detectionSnapshot.DetectedCount;
			if (request.IncludeFindings)
				EnsureFindingCountMatches(effectiveFindings, effectiveFindingCount);
			plan = plan with
			{
				UnscannableFiles = MergeUnscannableFiles(
					plan.UnscannableFiles,
					detectionUnscannableFiles)
			};
		}

		if (findingsRequested)
		{
			plan = plan with
			{
				FindingCount = effectiveFindingCount,
				Findings = request.IncludeFindings ? effectiveFindings : null
			};
		}
		new ContextDiagnosticRenderer(environment, request.Output, services.Localization)
			.Write(plan.Diagnostics);

		var outputPath = request.OutputPath is not null and not "-"
			? ExactOutputDestinationValidator.ValidateAnalysis(
				plan.SourceRoot,
				request.OutputPath,
				request.Force)
			: null;
		var requestedOutputPath = outputPath is not null
			? Path.GetFullPath(request.OutputPath!)
			: null;

		if (request.OutputPath is null or "-")
		{
			if (request.Format == AnalysisOutputFormat.Json)
			{
				await new MachineOutputRenderer(environment)
					.WriteAnalysisJsonAsync(plan, environment.Output, cancellationToken)
					.ConfigureAwait(false);
			}
			else
			{
				if (request.Output.Plain ||
				    !environment.IsOutputInteractive ||
				    environment.IsTermDumb)
				{
					await environment.Output.WriteAsync(
							AnalysisTextFormatter.Build(plan, services.Localization).AsMemory(),
							cancellationToken)
						.ConfigureAwait(false);
				}
				else
				{
					new HumanOutputRenderer(environment, request.Output, services.Localization)
						.WriteAnalysis(plan);
				}
			}
		}
		else
		{
			string ValidateDestination(string path) =>
				ExactOutputDestinationValidator.ValidateAnalysis(
					plan.SourceRoot,
					path,
					request.Force);
			string writtenPath;
			if (request.Format == AnalysisOutputFormat.Json)
			{
				var renderer = new MachineOutputRenderer(environment);
				writtenPath = await AtomicOutputWriter.WriteAsync(
						requestedOutputPath!,
						overwrite: request.Force,
						(destination, token) => renderer.WriteAnalysisJsonContentAsync(
							plan,
							destination,
							token),
						cancellationToken,
						ValidateDestination)
					.ConfigureAwait(false);
			}
			else
			{
				writtenPath = await AtomicOutputWriter.WriteTextAsync(
						requestedOutputPath!,
						AnalysisTextFormatter.Build(plan, services.Localization),
						overwrite: request.Force,
						cancellationToken,
						ValidateDestination)
					.ConfigureAwait(false);
			}
			TerminalTextEscaping.WriteSingleLine(environment.Output, writtenPath);
		}

		return plan.HasErrors ||
		       request.Strict && plan.Diagnostics.Count > 0 ||
		       request.FailOnFindings && effectiveFindingCount > 0
			? CommandLineExitCodes.PolicyFailure
			: CommandLineExitCodes.Success;
	}

	internal static bool HasContentTransformations(ProjectSelectionSpec selection) =>
		CodeTransformIdentity.Resolve(
			selection.CompressCode == true,
			selection.StripComments == true,
			selection.StripBlankLines == true) != CodeTransformKinds.None ||
		SecretRedactionFeatureSelection.Resolve(
			selection.HideSecrets == true,
			selection.HidePrivateData == true) != SecretRedactionFeatures.None;

	private ContentTransformationContext? CreateTransformationContext(
		ProjectContextPlan plan,
		bool forceSecretDetection = false)
	{
		var transformKinds = CodeTransformIdentity.Resolve(
			plan.Selection.CompressCode == true,
			plan.Selection.StripComments == true,
			plan.Selection.StripBlankLines == true);
		var redactionFeatures = SecretRedactionFeatureSelection.Resolve(
			forceSecretDetection || plan.Selection.HideSecrets == true,
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

	private static void EnsureFindingCountMatches(
		IReadOnlyList<EffectiveRedactionFinding> findings,
		int findingCount)
	{
		if (findings.Count != findingCount)
		{
			throw new SecretDetectionException(
				"The effective redaction finding count did not match the published snapshot.");
		}
	}

	private static IReadOnlyList<UnscannableFile> MergeUnscannableFiles(
		IReadOnlyList<UnscannableFile>? outputFiles,
		IReadOnlyList<UnscannableFile> detectionFiles)
	{
		if (outputFiles is not { Count: > 0 })
			return detectionFiles;
		if (detectionFiles.Count == 0)
			return outputFiles;

		return outputFiles
			.Concat(detectionFiles)
			.DistinctBy(static file => file.Path, PathComparer.Default)
			.OrderBy(static file => file.Path, PathComparer.Default)
			.ToArray();
	}
}

internal static class AnalysisTextFormatter
{
	public static string Build(ProjectContextPlan plan, LocalizationService localization)
	{
		var rows = BuildRows(plan, localization);
		var output = new StringBuilder();
		foreach (var row in rows)
			output.Append(row.Label).Append(": ").AppendLine(row.Value);

		if (plan.Findings is { } findings)
		{
			output.AppendLine();
			foreach (var line in BuildFindingTable(findings, localization))
				output.AppendLine(line);
		}

		return output.ToString();
	}

	public static IReadOnlyList<AnalysisTextRow> BuildRows(
		ProjectContextPlan plan,
		LocalizationService localization)
	{
		var rows = new List<AnalysisTextRow>
		{
			new(
				localization["Terminal.Analysis.Project"],
				TerminalTextEscaping.EscapeSingleLine(
					plan.SourceIdentity?.DisplayName ?? plan.SourceRoot))
		};
		if (plan.SourceIdentity?.RepositoryUrl is { Length: > 0 } repositoryUrl)
		{
			rows.Add(new AnalysisTextRow(
				localization["Terminal.Analysis.Source"],
				TerminalTextEscaping.EscapeSingleLine(repositoryUrl)));
		}
		rows.Add(new AnalysisTextRow(
			localization["Terminal.Analysis.Profile"],
			TerminalTextEscaping.EscapeSingleLine(FormatProfile(plan.Selection.ProfileSource))));
		rows.Add(new AnalysisTextRow(
			localization["Terminal.Analysis.GitMode"],
			ProjectSelectionTokens.ToToken(plan.Selection)));
		if (plan.Selection.Exclusions is { Count: > 0 } exclusions)
		{
			rows.Add(new AnalysisTextRow(
				localization["Terminal.Analysis.Exclusions"],
				string.Join(", ", exclusions.Select(ProjectSelectionTokens.ToToken))));
		}
		rows.Add(new AnalysisTextRow(
			localization["Terminal.Analysis.Roots"],
			plan.SelectedRoots.Count == 0
				? localization["Terminal.Profile.All"]
				: JoinEscaped(plan.SelectedRoots)));
		rows.Add(new AnalysisTextRow(
			localization["Terminal.Analysis.Extensions"],
			plan.SelectedExtensions.Count == 0
				? localization["Terminal.Profile.All"]
				: JoinEscaped(plan.SelectedExtensions)));
		rows.Add(new AnalysisTextRow(
			localization["Terminal.Analysis.Files"],
			plan.IncludedFiles.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)));
		rows.Add(new AnalysisTextRow(
			localization["Terminal.Analysis.Folders"],
			plan.IncludedFolders.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)));
		rows.Add(new AnalysisTextRow(
			localization["Terminal.Analysis.Size"],
			CacheCommandHandler.FormatByteSize(plan.IncludedBytes)));
		if (plan.FileSizeFilter is { } sizeFilter)
		{
			rows.Add(new AnalysisTextRow(
				localization["Terminal.Analysis.SizeFilter"],
				localization.Format(
					"Terminal.Analysis.SizeFilterValue",
					CacheCommandHandler.FormatByteSize(sizeFilter.MaximumFileBytes),
					sizeFilter.ExcludedFiles,
					CacheCommandHandler.FormatByteSize(sizeFilter.ExcludedBytes))));
		}
		rows.Add(new AnalysisTextRow(
			localization["Terminal.Analysis.Characters"],
			plan.Analysis.Metrics.Content.Chars.ToString(System.Globalization.CultureInfo.InvariantCulture)));
		rows.Add(new AnalysisTextRow(
			localization["Terminal.Analysis.Tokens"],
			plan.Analysis.Metrics.Content.Tokens.ToString(System.Globalization.CultureInfo.InvariantCulture)));
		if (plan.TopFiles is { } topFiles)
		{
			var value = topFiles.Count == 0
				? localization["Terminal.Analysis.TopFilesNone"]
				: string.Join(
					Environment.NewLine,
					topFiles.Select((file, index) => localization.Format(
						"Terminal.Analysis.TopFileValue",
						index + 1,
						TerminalTextEscaping.EscapeSingleLine(file.Path),
						file.Tokens.ToString(System.Globalization.CultureInfo.InvariantCulture))));
			rows.Add(new AnalysisTextRow(localization["Terminal.Analysis.TopFiles"], value));
		}
		if (plan.Redaction is { } redaction)
		{
			rows.Add(new AnalysisTextRow(
				localization["Terminal.Analysis.SecretRuleMatches"],
				redaction.MatchedCount.ToString(System.Globalization.CultureInfo.InvariantCulture)));
			rows.Add(new AnalysisTextRow(
				localization["Terminal.Analysis.RedactedValues"],
				redaction.RedactedCount.ToString(System.Globalization.CultureInfo.InvariantCulture)));
			if (redaction.MatchedCount == 0)
			{
				rows.Add(new AnalysisTextRow(
					localization["Terminal.Analysis.SecretScanNotice"],
					localization["SecretRedaction.NoFindingsNotice"]));
			}
		}
		if (plan.Privacy is { } privacy)
		{
			rows.Add(new AnalysisTextRow(
				localization["Terminal.Analysis.PrivateDataRuleMatches"],
				privacy.MatchedCount.ToString(System.Globalization.CultureInfo.InvariantCulture)));
			rows.Add(new AnalysisTextRow(
				localization["Terminal.Analysis.PrivateDataRedactedValues"],
				privacy.RedactedCount.ToString(System.Globalization.CultureInfo.InvariantCulture)));
			if (privacy.MatchedCount == 0)
			{
				rows.Add(new AnalysisTextRow(
					localization["Terminal.Analysis.PrivateDataScanNotice"],
					localization["PrivateDataRedaction.NoFindingsNotice"]));
			}
		}
		if (plan.FindingCount is { } findingCount)
		{
			rows.Add(new AnalysisTextRow(
				localization["Terminal.Analysis.Findings"],
				findingCount.ToString(System.Globalization.CultureInfo.InvariantCulture)));
		}
		if (plan.UnscannableFiles is { Count: > 0 } unscannableFiles)
		{
			rows.Add(new AnalysisTextRow(
				localization.Format("Content.Redaction.UnscannableFiles", unscannableFiles.Count),
				UnscannableFileOutput.FormatSummary(plan.SourceRoot, unscannableFiles, localization)));
		}
		if (plan.Compression is { } compression)
		{
			var value = compression.BodyTransformedFiles == 0
				? localization["Settings.Compression.Status.NothingToCompress"]
				: localization.Format(
					"Settings.Compression.Status.Applied",
					compression.BodyTransformedFiles,
					compression.CompressedFiles + compression.UnchangedFiles,
					CodeCompressionSnapshot.EstimateTokens(compression.SourceCharacters),
					CodeCompressionSnapshot.EstimateTokens(compression.TransformedCharacters));
			if (plan.Selection.CompressCode == true)
				rows.Add(new AnalysisTextRow(localization["Settings.Ignore.CompressCode"], value));
			if (plan.Selection.StripComments == true)
			{
				var commentValue = compression.CommentTransformedFiles == 0
					? localization["Settings.Comments.Status.NothingToStrip"]
					: localization.Format(
						"Settings.Comments.Status.Applied",
						compression.CommentTransformedFiles,
						compression.CompressedFiles + compression.UnchangedFiles);
				rows.Add(new AnalysisTextRow(localization["Settings.Ignore.StripComments"], commentValue));
			}
			if (plan.Selection.StripBlankLines == true)
			{
				var blankLineValue = compression.BlankLineTransformedFiles == 0
					? localization["Settings.BlankLines.Status.NothingToStrip"]
					: localization.Format(
						"Settings.BlankLines.Status.Applied",
						compression.BlankLineTransformedFiles,
						compression.CompressedFiles + compression.UnchangedFiles);
				rows.Add(new AnalysisTextRow(
					localization["Settings.Ignore.StripBlankLines"],
					blankLineValue));
			}
		}
		rows.Add(new AnalysisTextRow(
			localization["Terminal.Analysis.Fingerprint"],
			plan.Fingerprint));
		rows.AddRange(plan.Diagnostics.Select(diagnostic => new AnalysisTextRow(
			diagnostic.Code,
			ContextDiagnosticRenderer.ResolveMessage(localization, diagnostic))));
		return rows;
	}

	private static string JoinEscaped(IEnumerable<string> values) =>
		string.Join(", ", values.Select(TerminalTextEscaping.EscapeSingleLine));

	internal static IReadOnlyList<string> BuildFindingTable(
		IReadOnlyList<EffectiveRedactionFinding> findings,
		LocalizationService localization)
	{
		var rows = new List<string[]>(findings.Count + 1)
		{
			new[]
			{
				localization["Terminal.Analysis.FindingCategory"],
				localization["Terminal.Analysis.FindingRule"],
				localization["Terminal.Analysis.FindingLocation"]
			}
		};
		rows.AddRange(findings.Select(CreateFindingColumns));
		return TerminalColumnLayout.Format(rows);
	}

	internal static string FormatFinding(EffectiveRedactionFinding finding) =>
		TerminalColumnLayout.Format([CreateFindingColumns(finding)])[0];

	internal static string[] CreateFindingColumns(EffectiveRedactionFinding finding) =>
	[
		ToCategoryToken(finding.Category),
		TerminalTextEscaping.EscapeSingleLine(finding.RuleId),
		$"{TerminalTextEscaping.EscapeSingleLine(PathUtility.NormalizeSeparators(finding.RelativePath))}:" +
		finding.LineNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)
	];

	private static string FormatProfile(ProjectProfileReference? profile) =>
		profile?.Kind switch
		{
			null => "standard",
			ProjectProfileSourceKind.Standard => "standard",
			ProjectProfileSourceKind.Local => "local",
			ProjectProfileSourceKind.Portable => profile.Path ?? "portable",
			_ => throw new ArgumentOutOfRangeException(nameof(profile), profile, null)
		};

	private static string ToCategoryToken(RedactionFindingCategory category) =>
		category switch
		{
			RedactionFindingCategory.Secrets => "secret",
			RedactionFindingCategory.PrivateData => "private-data",
			_ => throw new ArgumentOutOfRangeException(nameof(category), category, null)
		};
}

internal sealed record AnalysisTextRow(string Label, string Value);
