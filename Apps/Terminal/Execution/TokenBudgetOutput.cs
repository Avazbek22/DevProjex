using DevProjex.Application.Ranking;
using DevProjex.Terminal.Rendering;

namespace DevProjex.Terminal.Execution;

internal static class TokenBudgetOutput
{
	public static void Write(
		TextWriter writer,
		ProjectContextTokenBudgetReport? report,
		LocalizationService localization,
		ImportanceRankingReport? ranking = null)
	{
		if (report is null)
			return;

		writer.WriteLine(localization.Format(
			"Terminal.TokenBudget.Summary",
			report.MaximumEstimatedTokens,
			report.IncludedFileCount,
			report.IncludedEstimatedTokens,
			report.SkippedFileCount,
			report.SkippedEstimatedTokens));
		if (report.LargestSkippedFiles.Count > 0)
		{
			foreach (var file in report.RankedSkippedFiles ?? [])
			{
				if (ranking?.Focus is not null)
				{
					var entry = ranking.Entries.FirstOrDefault(item => item.Priority == file.Priority);
					var hop = entry?.Hop is { } distance ? $"hop {distance}" : "unreachable";
					writer.WriteLine(
						$"[Skipped] {TerminalTextEscaping.EscapeSingleLine(file.Path)} — {hop}, " +
						$"priority {file.Priority.GetValueOrDefault()}, {file.EstimatedTokens} tokens, " +
						$"{file.RemainingEstimatedTokens.GetValueOrDefault()} remaining: does not fit the remaining budget");
				}
				else
				{
					writer.WriteLine(localization.Format(
						"Terminal.TokenBudget.RankedSkipped",
						TerminalTextEscaping.EscapeSingleLine(file.Path),
						file.Priority.GetValueOrDefault(),
						file.EstimatedTokens,
						file.RemainingEstimatedTokens.GetValueOrDefault()));
				}
			}
			writer.WriteLine(localization["Terminal.TokenBudget.SkippedFiles"]);
			foreach (var file in report.LargestSkippedFiles)
			{
				writer.WriteLine("  " + localization.Format(
					"Terminal.TokenBudget.SkippedFile",
					TerminalTextEscaping.EscapeSingleLine(file.Path),
					file.EstimatedTokens));
			}
			if (report.AdditionalSkippedFileCount > 0)
			{
				writer.WriteLine("  " + localization.Format(
					"Terminal.TokenBudget.More",
					report.AdditionalSkippedFileCount));
			}
		}
		writer.WriteLine(report.RankedSkippedFiles is { Count: > 0 } &&
		                 report.LargestSkippedFiles.Any(file => file.EstimatedTokens > report.MaximumEstimatedTokens)
			? localization["Terminal.TokenBudget.OversizedHint"]
			: localization["Terminal.TokenBudget.Hint"]);
	}
}
