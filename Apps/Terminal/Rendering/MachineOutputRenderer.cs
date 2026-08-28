using System.Text.Json;
using System.Text.Json.Serialization;
using DevProjex.Application.Secrets;
using DevProjex.Terminal.CommandLine;
using DevProjex.Terminal.Execution;

namespace DevProjex.Terminal.Rendering;

public sealed class MachineOutputRenderer(ITerminalEnvironment environment)
{
	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		WriteIndented = true,
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
	};

	public async Task WriteAnalysisJsonAsync(
		ProjectContextPlan plan,
		TextWriter writer,
		CancellationToken cancellationToken)
	{
		await using (var stream = new Utf8TextWriterStream(writer, cancellationToken))
		{
			await WriteAnalysisJsonContentAsync(plan, stream, cancellationToken)
				.ConfigureAwait(false);
			await stream.CompleteAsync(cancellationToken).ConfigureAwait(false);
		}
		await writer.WriteLineAsync(ReadOnlyMemory<char>.Empty, cancellationToken)
			.ConfigureAwait(false);
	}

	internal Task WriteAnalysisJsonContentAsync(
		ProjectContextPlan plan,
		Stream destination,
		CancellationToken cancellationToken) =>
		JsonSerializer.SerializeAsync(
			destination,
			CreateAnalysisDocument(plan),
			JsonOptions,
			cancellationToken);

	public TextWriter StandardOutput => environment.Output;

	private static string ResolveDocumentRoot(ProjectContextPlan plan) =>
		plan.SourceIdentity is
		{
			SourceType: ProjectSourceType.GitClone,
			SourceReference.Length: > 0
		} identity
			? identity.SourceReference
			: plan.SourceRoot;

	private static AnalysisDocument CreateAnalysisDocument(ProjectContextPlan plan)
	{
		var identity = plan.SourceIdentity;
		var hasExtendedContent = plan.Redaction is not null ||
		                         plan.Privacy is not null ||
		                         plan.Compression is not null ||
		                         plan.FindingCount is not null ||
		                         plan.Findings is not null;
		return new AnalysisDocument(
			SchemaVersion: 1,
			Kind: "devprojex-analysis",
			Project: new AnalysisProjectDocument(
				MachinePathPresentation.Normalize(ResolveDocumentRoot(plan)),
				identity?.DisplayName ??
				Path.GetFileName(Path.TrimEndingDirectorySeparator(plan.SourceRoot)),
				identity is
				{
					SourceType: ProjectSourceType.GitClone,
					RepositoryUrl.Length: > 0
				}
					? new AnalysisGitSourceDocument(
						"git",
						identity.RepositoryUrl,
						identity.Branch,
						identity.CommitHash)
					: null),
			Selection: new AnalysisSelectionDocument(
				ProjectSelectionTokens.ToToken(plan.Selection.GitMode!.Value),
				plan.Selection.Exclusions!.Select(ProjectSelectionTokens.ToToken).ToArray(),
				plan.Selection.HideSecrets == true,
				plan.Selection.HidePrivateData == true,
				plan.Selection.CompressCode == true,
				plan.Selection.StripComments == true,
				plan.Selection.StripBlankLines == true,
				plan.SelectedRoots,
				plan.SelectedExtensions,
				plan.Selection.SelectedPaths ?? []),
			Inventory: new AnalysisInventoryDocument(
				plan.IncludedFiles.Count,
				plan.IncludedFolders.Count),
			Metrics: new AnalysisMetricsDocument(
				plan.IncludedBytes,
				plan.Analysis.Metrics.Tree,
				plan.Analysis.Metrics.Content),
			Diagnostics: plan.Diagnostics.Select(static diagnostic =>
				new AnalysisDiagnosticDocument(
					diagnostic.Code,
					diagnostic.Severity.ToString().ToLowerInvariant(),
					diagnostic.Message,
					diagnostic.Path is null
						? null
						: MachinePathPresentation.Normalize(diagnostic.Path))),
			Fingerprint: plan.Fingerprint,
			Redaction: plan.Redaction is { } redaction
				? new AnalysisRedactionDocument(
					redaction.MatchedCount,
					redaction.RedactedCount,
					redaction.MatchedCount == 0
						? SecretRedactionLegendText.English.NoFindingsNotice
						: SecretRedactionLegendText.English.Notice)
				: null,
			Privacy: plan.Privacy is { } privacy
				? new AnalysisRedactionDocument(
					privacy.MatchedCount,
					privacy.RedactedCount,
					privacy.MatchedCount == 0
						? SecretRedactionLegendText.PrivacyEnglish.NoFindingsNotice
						: SecretRedactionLegendText.PrivacyEnglish.Notice)
				: null,
			Compression: plan.Compression is { } compression
				? new AnalysisCompressionDocument(
					compression.CompressedFiles,
					compression.UnchangedFiles,
					compression.SourceCharacters,
					compression.TransformedCharacters,
					compression.BodyTransformedFiles,
					compression.CommentTransformedFiles,
					compression.BlankLineTransformedFiles)
				: null,
			FindingCount: plan.FindingCount,
			Findings: plan.Findings?.Select(static finding =>
				new AnalysisFindingDocument(
					finding.RuleId,
					finding.Category == RedactionFindingCategory.Secrets
						? "secret"
						: "private-data",
					PathUtility.NormalizeSeparators(finding.RelativePath),
					finding.LineNumber)),
			ContentInspection: hasExtendedContent &&
			                   plan.UnscannableFiles is { Count: > 0 } unscannableFiles
				? new AnalysisContentInspectionDocument(
					unscannableFiles.Count,
					unscannableFiles.Select(file =>
						new AnalysisUnscannableFileDocument(
							PathUtility.GetPortableRelativePath(plan.SourceRoot, file.Path),
							UnscannableFileOutput.ToReasonToken(file.Classification))))
				: null);
	}

	private sealed record AnalysisDocument(
		int SchemaVersion,
		string Kind,
		AnalysisProjectDocument Project,
		AnalysisSelectionDocument Selection,
		AnalysisInventoryDocument Inventory,
		AnalysisMetricsDocument Metrics,
		IEnumerable<AnalysisDiagnosticDocument> Diagnostics,
		string Fingerprint,
		[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		AnalysisRedactionDocument? Redaction,
		[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		AnalysisRedactionDocument? Privacy,
		[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		AnalysisCompressionDocument? Compression,
		[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		int? FindingCount,
		[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		IEnumerable<AnalysisFindingDocument>? Findings,
		[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		AnalysisContentInspectionDocument? ContentInspection);

	private readonly record struct AnalysisProjectDocument(
		string Root,
		string Name,
		AnalysisGitSourceDocument? Source);

	private readonly record struct AnalysisGitSourceDocument(
		string Type,
		string RepositoryUrl,
		string? Branch,
		string? Commit);

	private readonly record struct AnalysisSelectionDocument(
		string GitMode,
		IReadOnlyList<string> Exclusions,
		bool HideSecrets,
		bool HidePrivateData,
		bool CompressCode,
		bool StripComments,
		bool StripBlankLines,
		IReadOnlyList<string> Roots,
		IReadOnlyList<string> Extensions,
		IReadOnlyCollection<string> SelectedPaths);

	private readonly record struct AnalysisInventoryDocument(int Files, int Folders);

	private readonly record struct AnalysisMetricsDocument(
		long Bytes,
		ProjectOutputMetricsReport Tree,
		ProjectOutputMetricsReport Content);

	private readonly record struct AnalysisDiagnosticDocument(
		string Code,
		string Severity,
		string Message,
		string? Path);

	private readonly record struct AnalysisRedactionDocument(
		int MatchedCount,
		int RedactedCount,
		string Notice);

	private readonly record struct AnalysisCompressionDocument(
		int CompressedFiles,
		int UnchangedFiles,
		long SourceCharacters,
		long TransformedCharacters,
		int BodyTransformedFiles,
		int CommentTransformedFiles,
		int BlankLineTransformedFiles);

	private readonly record struct AnalysisFindingDocument(
		string RuleId,
		string Category,
		string RelativePath,
		int LineNumber);

	private sealed record AnalysisContentInspectionDocument(
		int UnscannableCount,
		IEnumerable<AnalysisUnscannableFileDocument> UnscannableFiles);

	private readonly record struct AnalysisUnscannableFileDocument(string Path, string Reason);
}
