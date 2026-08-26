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
		var plan = await new StatusRenderer(environment, request.Output)
			.RunAsync(
				services.Localization["Terminal.Status.AnalyzingProject"],
				() => services.ContextFactory.BuildAsync(
					request.ProjectPath,
					request.Selection,
					cancellationToken: cancellationToken))
			.ConfigureAwait(false);
		var transformKinds = CodeTransformIdentity.Resolve(
			plan.Selection.CompressCode == true,
			plan.Selection.StripComments == true,
			plan.Selection.StripBlankLines == true);
		var redactionFeatures = SecretRedactionFeatureSelection.Resolve(
			plan.Selection.HideSecrets == true,
			plan.Selection.HidePrivateData == true);
		var transformationContext = ContentTransformationContext.For(
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
		IReadOnlyList<EffectiveRedactionFinding> effectiveFindings = [];
		var effectiveFindingCount = 0;
		if (transformationContext is not null)
		{
			await using var prepared = await services.SecretRedactionOutputPreparer
				.PrepareAsync(
					transformationContext,
					plan.IncludedFiles,
					request.IncludeFindings,
					cancellationToken)
				.ConfigureAwait(false);
			var transformedAnalyzer = services.SecretRedactionOutputPreparer.CreatePreparedAnalyzer(prepared);
			effectiveFindingCount = prepared.Snapshot?.DetectedCount ?? 0;
			if (request.IncludeFindings)
			{
				effectiveFindings = prepared.GetEffectiveFindings();
				if (effectiveFindings.Count != effectiveFindingCount)
				{
					throw new SecretDetectionException(
						"The effective redaction finding count did not match the published snapshot.");
				}
			}
			var transformedMetrics = await ProjectContentMetricsCalculator
				.CalculateAsync(transformedAnalyzer, plan.IncludedFiles, cancellationToken)
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
		if (request.IncludeFindings)
			plan = plan with { Findings = effectiveFindings };
		new ContextDiagnosticRenderer(environment, request.Output, services.Localization)
			.Write(plan.Diagnostics);

		var outputPath = request.OutputPath is not null and not "-"
			? ExactOutputDestinationValidator.ValidateAnalysis(
				plan.SourceRoot,
				request.OutputPath)
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
			var payload = request.Format == AnalysisOutputFormat.Json
				? await BuildJsonAsync(plan, cancellationToken).ConfigureAwait(false)
				: AnalysisTextFormatter.Build(plan, services.Localization);
			var writtenPath = await AtomicOutputWriter
				.WriteTextAsync(
					requestedOutputPath!,
					payload,
					overwrite: false,
					cancellationToken,
					path => ExactOutputDestinationValidator.ValidateAnalysis(
						plan.SourceRoot,
						path))
				.ConfigureAwait(false);
			TerminalTextEscaping.WriteSingleLine(environment.Output, writtenPath);
		}

		return plan.HasErrors ||
		       request.Strict && plan.Diagnostics.Count > 0 ||
		       request.FailOnFindings && effectiveFindingCount > 0
			? CommandLineExitCodes.PolicyFailure
			: CommandLineExitCodes.Success;
	}

	private async Task<string> BuildJsonAsync(
		ProjectContextPlan plan,
		CancellationToken cancellationToken)
	{
		using var writer = new StringWriter();
		var nestedEnvironment = new WriterTerminalEnvironment(environment, writer);
		await new MachineOutputRenderer(nestedEnvironment)
			.WriteAnalysisJsonAsync(plan, writer, cancellationToken)
			.ConfigureAwait(false);
		return writer.ToString().TrimEnd('\r', '\n');
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
			ProjectSelectionTokens.ToToken(plan.Selection.GitMode!.Value)));
		rows.Add(new AnalysisTextRow(
			localization["Terminal.Analysis.Exclusions"],
			string.Join(", ", plan.Selection.Exclusions!.Select(ProjectSelectionTokens.ToToken))));
		rows.Add(new AnalysisTextRow(
			localization["Terminal.Analysis.Roots"],
			JoinEscaped(plan.SelectedRoots)));
		rows.Add(new AnalysisTextRow(
			localization["Terminal.Analysis.Extensions"],
			JoinEscaped(plan.SelectedExtensions)));
		rows.Add(new AnalysisTextRow(
			localization["Terminal.Analysis.Files"],
			plan.IncludedFiles.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)));
		rows.Add(new AnalysisTextRow(
			localization["Terminal.Analysis.Folders"],
			plan.IncludedFolders.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)));
		rows.Add(new AnalysisTextRow(
			localization["Terminal.Analysis.Size"],
			$"{plan.IncludedBytes.ToString(System.Globalization.CultureInfo.InvariantCulture)} B"));
		rows.Add(new AnalysisTextRow(
			localization["Terminal.Analysis.Characters"],
			plan.Analysis.Metrics.Content.Chars.ToString(System.Globalization.CultureInfo.InvariantCulture)));
		rows.Add(new AnalysisTextRow(
			localization["Terminal.Analysis.Tokens"],
			plan.Analysis.Metrics.Content.Tokens.ToString(System.Globalization.CultureInfo.InvariantCulture)));
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
		if (plan.Findings is { } findings)
		{
			rows.Add(new AnalysisTextRow(
				localization["Terminal.Analysis.Findings"],
				findings.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)));
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
			ContextDiagnosticRenderer.ResolveMessage(localization, diagnostic.Code))));
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

internal sealed class WriterTerminalEnvironment(
	ITerminalEnvironment source,
	TextWriter output)
	: ITerminalEnvironment
{
	public TextReader Input => source.Input;
	public TextWriter Output => output;
	public TextWriter Error => source.Error;
	public bool IsInputInteractive => source.IsInputInteractive;
	public bool IsOutputInteractive => false;
	public bool IsErrorInteractive => source.IsErrorInteractive;
	public bool HasAttachedConsole => source.HasAttachedConsole;
	public bool IsTerminalHost => source.IsTerminalHost;
	public bool IsCi => source.IsCi;
	public bool IsTermDumb => source.IsTermDumb;
	public bool IsNoColor => true;
	public bool SupportsUnicode => source.SupportsUnicode;
	public int Width => source.Width;
	public int Height => source.Height;
	public IReadOnlyDictionary<string, string?> Variables => source.Variables;
}
