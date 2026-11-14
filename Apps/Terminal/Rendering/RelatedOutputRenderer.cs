using System.Text.Json;
using System.Text.Json.Serialization;
using DevProjex.Terminal.Execution;

namespace DevProjex.Terminal.Rendering;

internal static class RelatedOutputRenderer
{
	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		WriteIndented = true,
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
	};

	public static async Task WriteAsync(
		TextWriter writer,
		DependencyRelatedResult result,
		DependencyOutsideSelectionResult? outside,
		DependencyDirection direction,
		AnalysisOutputFormat format,
		LocalizationService localization,
		CancellationToken cancellationToken)
	{
		// A narrowed call answers from the readable project; the selection only places each file.
		result = outside?.Selection ?? result;
		var outsideSeeds = outside is { Seeds.Count: > 0 } ? outside.Seeds : null;
		if (format == AnalysisOutputFormat.Json)
		{
			var jsonCoverage = result.Index.Coverage;
			var resolution = CountResolution(result, outside, direction);
			var document = new RelatedDocument(
				SchemaVersion: 1,
				Kind: "devprojex-related-files",
				Direction: direction,
				Seeds: result.Seeds,
				OutsideSelection: outsideSeeds,
				Coverage: new
				{
					jsonCoverage.Files,
					jsonCoverage.Supported,
					jsonCoverage.Unsupported,
					jsonCoverage.ExtractionFailed,
					jsonCoverage.UnsupportedLanguages,
					jsonCoverage.CSharpErrorNodeKinds,
					jsonCoverage.ExtractionFailedFiles,
					jsonCoverage.PartialParseDiagnostics,
					configurationDiagnostics = jsonCoverage.ConfigurationDiagnostics.Select(ProjectConfigurationDiagnostic)
				},
				Resolution: resolution,
				SearchScope: new { files = result.Index.Files.Count });
			await writer.WriteLineAsync(JsonSerializer.Serialize(document, JsonOptions).AsMemory(), cancellationToken)
				.ConfigureAwait(false);
			return;
		}

		var outsideBySeed = outsideSeeds?.ToDictionary(static seed => seed.Seed, StringComparer.Ordinal);
		foreach (var seed in result.Seeds)
		{
			await writer.WriteLineAsync($"{localization["Terminal.Related.Seed"]}: {TerminalTextEscaping.EscapeSingleLine(seed.Seed)}")
				.ConfigureAwait(false);
			if (seed.NoFactsReason is not null)
			{
				await writer.WriteLineAsync(localization["Terminal.Related.NoFacts"]).ConfigureAwait(false);
				continue;
			}
			if (direction is DependencyDirection.Dependencies or DependencyDirection.Both)
				await WriteSection(writer, localization["Terminal.Related.Dependencies"], seed.Dependencies, localization).ConfigureAwait(false);
			if (direction is DependencyDirection.Dependents or DependencyDirection.Both)
				await WriteSection(writer, localization["Terminal.Related.Dependents"], seed.Dependents, localization).ConfigureAwait(false);
			if (seed.Dependencies.Count == 0 && seed.Dependents.Count == 0)
				await writer.WriteLineAsync(localization["Terminal.Related.None"]).ConfigureAwait(false);
			if (outsideBySeed?.GetValueOrDefault(seed.Seed) is not { } outsideSeed)
				continue;
			if (outsideSeed.DependencyCount > 0)
				await WriteSection(
					writer,
					OutsideTitle(localization, "Terminal.Related.OutsideDependencies", outsideSeed.Dependencies.Count, outsideSeed.DependencyCount),
					outsideSeed.Dependencies,
					localization).ConfigureAwait(false);
			if (outsideSeed.DependentCount > 0)
				await WriteSection(
					writer,
					OutsideTitle(localization, "Terminal.Related.OutsideDependents", outsideSeed.Dependents.Count, outsideSeed.DependentCount),
					outsideSeed.Dependents,
					localization).ConfigureAwait(false);
		}
		var resolutionCounts = CountResolution(result, outside, direction);
		await writer.WriteLineAsync(
			$"[Resolution] resolved={resolutionCounts.Resolved} · ambiguous={resolutionCounts.Ambiguous} · " +
			$"unresolved={resolutionCounts.Unresolved} · external={resolutionCounts.External}" +
			(resolutionCounts.OutsideSelection > 0 ? $" · outside-selection={resolutionCounts.OutsideSelection}" : string.Empty))
			.ConfigureAwait(false);
		if (outside is { SelectionAnsweredSeeds: > 0 and var selectionAnsweredSeeds })
		{
			await writer.WriteLineAsync(
				$"[Dependency facts limited] project index partial · selection-answer-seeds={selectionAnsweredSeeds}; " +
				"a fact or work limit dropped facts these seeds rely on, so they keep the selection's own answer and list no files outside it.")
				.ConfigureAwait(false);
		}
		var coverage = result.Index.Coverage;
		await writer.WriteLineAsync(localization.Format(
			"Terminal.Related.Coverage",
			coverage.Files,
			coverage.Supported,
			coverage.Unsupported,
			coverage.ExtractionFailed)).ConfigureAwait(false);
		foreach (var diagnostic in coverage.ConfigurationDiagnostics.Take(8))
		{
			var projected = ProjectConfigurationDiagnostic(diagnostic);
			await writer.WriteLineAsync(
				$"[Dependency configuration] affected-scopes={projected.AffectedScopes} · " +
				$"problem={projected.Problem} · path={TerminalTextEscaping.EscapeSingleLine(projected.Path)}")
				.ConfigureAwait(false);
		}
		foreach (var path in coverage.ExtractionFailedFiles.Take(8))
			await writer.WriteLineAsync($"[Dependency extraction failed] path={TerminalTextEscaping.EscapeSingleLine(path)}")
				.ConfigureAwait(false);
		foreach (var diagnostic in coverage.PartialParseDiagnostics.Take(8))
		{
			var ranges = string.Join(',', diagnostic.Ranges.Select(static range =>
				range.StartLine == range.EndLine ? range.StartLine.ToString() : $"{range.StartLine}-{range.EndLine}"));
			if (diagnostic.RangesTruncated) ranges += ",...";
			await writer.WriteLineAsync(
				$"[Dependency partial parse] path={TerminalTextEscaping.EscapeSingleLine(diagnostic.Path)} · " +
				$"dropped={diagnostic.DroppedConstructs} · lines={ranges}").ConfigureAwait(false);
		}
	}

	private static string OutsideTitle(LocalizationService localization, string key, int listed, int total) =>
		listed == total
			? localization[key]
			: $"{localization[key]} ({localization.Format("Terminal.Related.OutsideListed", listed, total)})";

	private static ResolutionCounts CountResolution(
		DependencyRelatedResult result,
		DependencyOutsideSelectionResult? outside,
		DependencyDirection direction)
	{
		var counts = outside?.Resolution ?? DependencyFactsEngine.CountResolution(
			result.Index,
			result.Seeds.Select(static seed => seed.Seed),
			direction);
		return new ResolutionCounts(
			counts.Resolved,
			counts.Ambiguous,
			counts.Unresolved,
			counts.External,
			counts.OutsideSelection);
	}

	private static async Task WriteSection(
		TextWriter writer,
		string title,
		IReadOnlyList<RelatedFile> files,
		LocalizationService localization)
	{
		await writer.WriteLineAsync(title + ":").ConfigureAwait(false);
		foreach (var file in files)
		{
			var crossScope = file.CrossScope
				? " — " + localization["Terminal.Related.CrossScope"]
				: string.Empty;
			var candidates = file.Status == ResolutionStatus.Ambiguous && file.Candidates.Count > 0
				? " — " + localization.Format(
					"Terminal.Related.Candidates",
					string.Join(", ", file.Candidates.Select(TerminalTextEscaping.EscapeSingleLine)))
				: string.Empty;
			var reasons = RelatedReasonCompaction.Compact(file.Reasons)
				.Select(TerminalTextEscaping.EscapeSingleLine);
			var line = $"{TerminalTextEscaping.EscapeSingleLine(file.Path)} — " +
			           $"{string.Join(" · ", reasons)} — " +
			           $"{file.Status.ToString().ToLowerInvariant()} — " +
			           localization.Format("Terminal.Related.Tokens", file.EstimatedTokens) +
			           crossScope + candidates;
			await writer.WriteLineAsync(line).ConfigureAwait(false);
		}
	}

	private static ConfigurationDiagnosticOutput ProjectConfigurationDiagnostic(
		DependencyConfigurationDiagnostic diagnostic) => new(
			diagnostic.Path,
			diagnostic.State.ToString().ToLowerInvariant(),
			diagnostic.ScopeIds.Count);

	private sealed record ConfigurationDiagnosticOutput(string Path, string Problem, int AffectedScopes);

	// The outside-selection members are additive: a selection that narrows nothing, or finds
	// nothing outside itself, keeps the original document byte for byte.
	private sealed record ResolutionCounts(
		int Resolved,
		int Ambiguous,
		int Unresolved,
		int External,
		[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int OutsideSelection);

	private sealed record RelatedDocument(
		int SchemaVersion,
		string Kind,
		DependencyDirection Direction,
		IReadOnlyList<SeedRelatedFiles> Seeds,
		[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		IReadOnlyList<SeedOutsideSelection>? OutsideSelection,
		object Coverage,
		ResolutionCounts Resolution,
		object SearchScope);
}
