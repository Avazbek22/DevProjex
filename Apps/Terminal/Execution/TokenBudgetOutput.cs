using DevProjex.Terminal.Rendering;

namespace DevProjex.Terminal.Execution;

internal static class TokenBudgetOutput
{
	public static void Write(
		TextWriter writer,
		ProjectContextTokenBudgetReport? report,
		LocalizationService localization)
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
		writer.WriteLine(localization["Terminal.TokenBudget.Hint"]);
	}
}
