using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;
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
		var document = new
		{
			schemaVersion = 1,
			kind = "devprojex-analysis",
			project = new
			{
				root = PathUtility.NormalizeSeparators(ResolveDocumentRoot(plan)),
				name = plan.SourceIdentity?.DisplayName ??
				       Path.GetFileName(Path.TrimEndingDirectorySeparator(plan.SourceRoot)),
				source = plan.SourceIdentity is
				{
					SourceType: ProjectSourceType.GitClone,
					RepositoryUrl.Length: > 0
				} identity
					? new
					{
						type = "git",
						repositoryUrl = identity.RepositoryUrl,
						branch = identity.Branch,
						commit = identity.CommitHash
					}
					: null
			},
			selection = new
			{
				gitMode = ProjectSelectionTokens.ToToken(plan.Selection.GitMode!.Value),
				exclusions = plan.Selection.Exclusions!.Select(ProjectSelectionTokens.ToToken).ToArray(),
				hideSecrets = plan.Selection.HideSecrets == true,
				hidePrivateData = plan.Selection.HidePrivateData == true,
				compressCode = plan.Selection.CompressCode == true,
				stripComments = plan.Selection.StripComments == true,
				stripBlankLines = plan.Selection.StripBlankLines == true,
				roots = plan.SelectedRoots,
				extensions = plan.SelectedExtensions,
				selectedPaths = plan.Selection.SelectedPaths ?? []
			},
			inventory = new
			{
				files = plan.IncludedFiles.Count,
				folders = plan.IncludedFolders.Count
			},
			metrics = new
			{
				bytes = plan.IncludedBytes,
				tree = plan.Analysis.Metrics.Tree,
				content = plan.Analysis.Metrics.Content
			},
			diagnostics = plan.Diagnostics.Select(static diagnostic => new
			{
				code = diagnostic.Code,
				severity = diagnostic.Severity.ToString().ToLowerInvariant(),
				message = diagnostic.Message,
				path = diagnostic.Path is null ? null : PathUtility.NormalizeSeparators(diagnostic.Path)
			}),
			fingerprint = plan.Fingerprint
		};
		var json = JsonSerializer.Serialize(document, JsonOptions);
		if (plan.Redaction is not null ||
		    plan.Privacy is not null ||
		    plan.Compression is not null ||
		    plan.Findings is not null)
		{
			var root = JsonNode.Parse(json)?.AsObject() ??
			           throw new JsonException("The analysis document could not be materialized.");
			if (plan.Redaction is { } redaction)
			{
				root["redaction"] = new JsonObject
				{
					["matchedCount"] = redaction.MatchedCount,
					["redactedCount"] = redaction.RedactedCount,
					["notice"] = redaction.MatchedCount == 0
						? SecretRedactionLegendText.English.NoFindingsNotice
						: SecretRedactionLegendText.English.Notice
				};
			}
			if (plan.Privacy is { } privacy)
			{
				root["privacy"] = new JsonObject
				{
					["matchedCount"] = privacy.MatchedCount,
					["redactedCount"] = privacy.RedactedCount,
					["notice"] = privacy.MatchedCount == 0
						? SecretRedactionLegendText.PrivacyEnglish.NoFindingsNotice
						: SecretRedactionLegendText.PrivacyEnglish.Notice
				};
			}
			if (plan.Compression is { } compression)
			{
				root["compression"] = new JsonObject
				{
					["compressedFiles"] = compression.CompressedFiles,
					["unchangedFiles"] = compression.UnchangedFiles,
					["sourceCharacters"] = compression.SourceCharacters,
					["transformedCharacters"] = compression.TransformedCharacters,
					["bodyTransformedFiles"] = compression.BodyTransformedFiles,
					["commentTransformedFiles"] = compression.CommentTransformedFiles,
					["blankLineTransformedFiles"] = compression.BlankLineTransformedFiles
				};
			}
			if (plan.Findings is { } findings)
			{
				root["findings"] = new JsonArray(findings.Select(finding =>
					(JsonNode)new JsonObject
					{
						["ruleId"] = finding.RuleId,
						["category"] = finding.Category == RedactionFindingCategory.Secrets
							? "secret"
							: "private-data",
						["relativePath"] = PathUtility.NormalizeSeparators(finding.RelativePath),
						["lineNumber"] = finding.LineNumber
					}).ToArray());
			}
			if (plan.UnscannableFiles is { Count: > 0 } unscannableFiles)
			{
				root["contentInspection"] = new JsonObject
				{
					["unscannableCount"] = unscannableFiles.Count,
					["unscannableFiles"] = new JsonArray(unscannableFiles.Select(file =>
						(JsonNode)new JsonObject
						{
							["path"] = PathUtility.GetPortableRelativePath(plan.SourceRoot, file.Path),
							["reason"] = UnscannableFileOutput.ToReasonToken(file.Classification)
						}).ToArray())
				};
			}
			json = root.ToJsonString(JsonOptions);
		}
		await writer.WriteLineAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
	}

	public TextWriter StandardOutput => environment.Output;

	private static string ResolveDocumentRoot(ProjectContextPlan plan) =>
		plan.SourceIdentity is
		{
			SourceType: ProjectSourceType.GitClone,
			SourceReference.Length: > 0
		} identity
			? identity.SourceReference
			: plan.SourceRoot;
}
