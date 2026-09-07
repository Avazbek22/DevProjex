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
		var historySuffix = report.HasMissingSignals
			? $" · missing signals: {PolicyToken(report.MissingSignalPolicy)}"
			: string.Empty;
		writer.WriteLine(localization.Format(
			"Terminal.Ranking.Summary",
			report.Algorithm,
			percentage,
			report.CandidateCount,
			report.GitWindow,
			historySuffix,
			$"{report.GraphVariant} · facts"));
		writer.WriteLine(
			$"[Ranking coverage] facts {percentage.ToString(CultureInfo.InvariantCulture)}% · " +
			$"resolved internal references {report.ResolvedInternalReferences.ToString(CultureInfo.InvariantCulture)}/" +
			$"{report.InternalReferenceCandidates.ToString(CultureInfo.InvariantCulture)} " +
			$"({Math.Round(report.ResolvedInternalReferenceCoverage * 100, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture)}%) · " +
			$"files with resolved edges {report.FilesWithResolvedEdges.ToString(CultureInfo.InvariantCulture)}");
		if (report.GitHistoryIsShallow && !report.GitHistoryIsComplete)
		{
			writer.WriteLine(
				$"[Ranking git] read {report.GitCommitCount.ToString(CultureInfo.InvariantCulture)}/" +
				$"{report.GitWindow.ToString(CultureInfo.InvariantCulture)} commits; shallow history");
		}
		foreach (var entry in report.TopEntries.Take(10))
		{
			var commits = entry.Commits?.ToString(CultureInfo.InvariantCulture) ??
			              $"{localization["Terminal.Ranking.Unavailable"]}: {entry.GitUnavailableReason}";
			writer.WriteLine(localization.Format(
				"Terminal.Ranking.Top",
				TerminalTextEscaping.EscapeSingleLine(entry.Path),
				entry.Dependents,
				entry.Dependencies,
				commits,
				report.GitWindow,
				RoleSuffix(entry, localization) + ContributionSuffix(entry)));
		}
	}

	private static string RoleSuffix(ImportanceRankingEntry entry, LocalizationService localization)
	{
		if (entry.IsCoordinator)
			return " · coordinator";
		return entry.Role switch
		{
			ImportanceFileRole.TestSource => localization["Terminal.Ranking.TestSourceSuffix"],
			ImportanceFileRole.Manifest => localization["Terminal.Ranking.ManifestSuffix"],
			ImportanceFileRole.EntryPoint => localization["Terminal.Ranking.EntryPointSuffix"],
			_ => string.Empty
		};
	}

	private static string ContributionSuffix(ImportanceRankingEntry entry) =>
		$" · priority {entry.Priority.ToString(CultureInfo.InvariantCulture)}; " +
		$"graph {(entry.HasGraphFacts ? "available" : "unavailable")}; " +
		$"git {(entry.HasGitHistory ? "available" : "unavailable")}; " +
		$"main contribution: {SignalToken(entry.MainContribution)}" +
		(entry.ConfidenceLimited ? "; confidence limited" : string.Empty);

	private static string SignalToken(ImportanceRankingSignal signal) => signal switch
	{
		ImportanceRankingSignal.Graph => "graph",
		ImportanceRankingSignal.Git => "git",
		ImportanceRankingSignal.Role => "role",
		_ => "none"
	};

	private static string PolicyToken(ImportanceMissingSignalPolicy policy) => policy switch
	{
		ImportanceMissingSignalPolicy.Redistribute => "redistributed",
		ImportanceMissingSignalPolicy.NeutralFill => "neutral fill",
		ImportanceMissingSignalPolicy.ConfidenceLimited => "confidence limited",
		_ => throw new ArgumentOutOfRangeException(nameof(policy), policy, null)
	};
}
