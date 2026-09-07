using DevProjex.Application.Ranking;
using DevProjex.Terminal.Rendering;
using System.Globalization;

namespace DevProjex.Terminal.Execution;

internal static class RankingOutput
{
	public static void Write(
		TextWriter writer,
		ImportanceRankingReport? report,
		ProjectContextTokenBudgetReport? tokenBudget,
		LocalizationService localization)
	{
		if (report is null)
			return;
		var percentage = Math.Round(report.GraphCoverage * 100, MidpointRounding.AwayFromZero);
		var historySuffix = report.GitUnavailableReason == ProjectGitHistoryUnavailableReason.None
			? string.Empty
			: localization.Format(
				"Terminal.Ranking.GitUnavailableSuffix",
				report.GitUnavailableReason.ToString());
		writer.WriteLine(localization.Format(
			"Terminal.Ranking.Summary",
			report.Algorithm,
			percentage,
			report.CandidateCount,
			report.GitWindow,
			historySuffix));
		foreach (var entry in report.TopEntries.Take(10))
		{
			var commits = entry.Commits?.ToString(CultureInfo.InvariantCulture) ??
			              localization["Terminal.Ranking.Unavailable"];
			writer.WriteLine(localization.Format(
				"Terminal.Ranking.Top",
				TerminalTextEscaping.EscapeSingleLine(entry.Path),
				entry.Dependents,
				entry.Dependencies,
				commits,
				report.GitWindow,
				RoleSuffix(entry.Role, localization)));
		}
	}

	private static string RoleSuffix(ImportanceFileRole role, LocalizationService localization) => role switch
	{
		ImportanceFileRole.TestSource => localization["Terminal.Ranking.TestSourceSuffix"],
		ImportanceFileRole.Manifest => localization["Terminal.Ranking.ManifestSuffix"],
		ImportanceFileRole.EntryPoint => localization["Terminal.Ranking.EntryPointSuffix"],
		_ => string.Empty
	};
}
