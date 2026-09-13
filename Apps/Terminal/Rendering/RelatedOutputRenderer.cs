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
		DependencyDirection direction,
		AnalysisOutputFormat format,
		LocalizationService localization,
		CancellationToken cancellationToken)
	{
		if (format == AnalysisOutputFormat.Json)
		{
			var jsonCoverage = result.Index.Coverage;
			var resolution = CountResolution(result, direction);
			var document = new
			{
				schemaVersion = 1,
				kind = "devprojex-related-files",
				direction,
				seeds = result.Seeds,
				coverage = new
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
				resolution,
				searchScope = new { files = result.Index.Files.Count }
			};
			await writer.WriteLineAsync(JsonSerializer.Serialize(document, JsonOptions).AsMemory(), cancellationToken)
				.ConfigureAwait(false);
			return;
		}

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
		}
		var resolutionCounts = CountResolution(result, direction);
		await writer.WriteLineAsync(
			$"[Resolution] resolved={resolutionCounts.Resolved} · ambiguous={resolutionCounts.Ambiguous} · " +
			$"unresolved={resolutionCounts.Unresolved} · external={resolutionCounts.External}").ConfigureAwait(false);
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

	private static ResolutionCounts CountResolution(DependencyRelatedResult result, DependencyDirection direction)
	{
		var edges = new HashSet<DependencyEdge>();
		foreach (var seed in result.Seeds)
		{
			if (direction is DependencyDirection.Dependencies or DependencyDirection.Both)
				foreach (var edge in result.Index.EdgesBySource.GetValueOrDefault(seed.Seed) ?? [])
					edges.Add(edge);
			if (direction is DependencyDirection.Dependents or DependencyDirection.Both)
				foreach (var edge in result.Index.EdgesByTarget.GetValueOrDefault(seed.Seed) ?? [])
					if (string.Equals(edge.Target, seed.Seed, StringComparison.Ordinal))
						edges.Add(edge);
		}
		return new ResolutionCounts(
			edges.Count(static edge => edge.Status == ResolutionStatus.Resolved),
			edges.Count(static edge => edge.Status == ResolutionStatus.Ambiguous),
			edges.Count(static edge => edge.Status == ResolutionStatus.Unresolved),
			edges.Count(static edge => edge.Status == ResolutionStatus.External));
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
			var candidates = file.Candidates.Count > 1
				? " — " + localization.Format(
					"Terminal.Related.Candidates",
					string.Join(", ", file.Candidates.Select(TerminalTextEscaping.EscapeSingleLine)))
				: string.Empty;
			var line = $"{TerminalTextEscaping.EscapeSingleLine(file.Path)} — " +
			           $"{string.Join(" · ", file.Reasons.Select(TerminalTextEscaping.EscapeSingleLine))} — " +
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
	private sealed record ResolutionCounts(int Resolved, int Ambiguous, int Unresolved, int External);
}
