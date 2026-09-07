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
			var document = new
			{
				schemaVersion = 1,
				kind = "devprojex-related-files",
				direction,
				seeds = result.Seeds,
				coverage = result.Index.Coverage,
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
		var coverage = result.Index.Coverage;
		await writer.WriteLineAsync(localization.Format(
			"Terminal.Related.Coverage",
			coverage.Files,
			coverage.Supported,
			coverage.Unsupported,
			coverage.ExtractionFailed)).ConfigureAwait(false);
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
}
