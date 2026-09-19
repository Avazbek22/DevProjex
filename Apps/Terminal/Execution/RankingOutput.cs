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
		if (report.Focus is { } focus)
			WriteFocusSummary(writer, report, focus, percentage);
		else
		{
			writer.WriteLine(localization.Format(
				"Terminal.Ranking.Summary",
				report.Algorithm,
				percentage,
				report.CandidateCount,
				report.GitWindow,
				historySuffix,
				$"{report.GraphVariant} · facts"));
		}
		var referenceResolution = report.InternalReferenceCandidates == 0
			? "unavailable"
			: $"{report.ResolvedInternalReferences.ToString(CultureInfo.InvariantCulture)}/" +
			  $"{report.InternalReferenceCandidates.ToString(CultureInfo.InvariantCulture)} " +
			  $"({Math.Round(report.ResolvedInternalReferenceCoverage * 100, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture)}%)";
		writer.WriteLine(
			$"[Ranking coverage] facts {percentage.ToString(CultureInfo.InvariantCulture)}% · " +
			$"internal reference resolution {referenceResolution} · " +
			$"unique resolved file pairs {report.UniqueResolvedFilePairs.ToString(CultureInfo.InvariantCulture)} · " +
			$"files with resolved edges {report.FilesWithResolvedEdges.ToString(CultureInfo.InvariantCulture)}");
		if (report.GitHistoryIsShallow && !report.GitHistoryIsComplete)
		{
			writer.WriteLine(
				$"[Ranking git] read {report.GitCommitCount.ToString(CultureInfo.InvariantCulture)}/" +
				$"{report.GitWindow.ToString(CultureInfo.InvariantCulture)} commits; shallow history");
		}
		foreach (var entry in report.TopEntries.Take(10))
		{
			if (report.Focus is not null)
			{
				WriteFocusEntry(writer, report, entry, localization);
				continue;
			}
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

	private static void WriteFocusSummary(
		TextWriter writer,
		ImportanceRankingReport report,
		FocusRankingSummary focus,
		double percentage)
	{
		var output = new StringBuilder(384);
		output.Append("[Ranking] ")
			.Append(focus.Algorithm)
			.Append(" · ")
			.Append(focus.Seeds.Count.ToString(CultureInfo.InvariantCulture))
			.Append(focus.Seeds.Count == 1 ? " seed · hops" : " seeds · hops");
		foreach (var hop in focus.Hops.OrderBy(static pair => pair.Key))
		{
			output.Append(' ')
				.Append(hop.Key.ToString(CultureInfo.InvariantCulture))
				.Append(':')
				.Append(hop.Value.ToString(CultureInfo.InvariantCulture));
		}
		if (focus.HopsBeyond > 0)
		{
			output.Append(" 8+:")
				.Append(focus.HopsBeyond.ToString(CultureInfo.InvariantCulture))
				.Append(" · max hop ")
				.Append(focus.MaxHop.ToString(CultureInfo.InvariantCulture));
		}
		output.Append(" · unreachable ")
			.Append(focus.Unreachable.ToString(CultureInfo.InvariantCulture))
			.Append(" · within hop ")
			.Append(focus.WithinHop)
			.Append(" · graph ")
			.Append(report.GraphVariant)
			.Append(" · facts ")
			.Append(percentage.ToString(CultureInfo.InvariantCulture))
			.Append("% of ")
			.Append(report.CandidateCount.ToString(CultureInfo.InvariantCulture))
			.Append(" sources · git window ")
			.Append(report.GitWindow.ToString(CultureInfo.InvariantCulture))
			.Append(" commits");
		var degraded = focus.Seeds.Where(static seed => seed.State != FocusSeedState.Resolved).ToArray();
		if (degraded.Length > 0)
		{
			var reasons = degraded.Select(static seed => SeedStateToken(seed.State))
				.Distinct(StringComparer.Ordinal);
			output.Append(" · focus degraded: ")
				.Append(degraded.Length.ToString(CultureInfo.InvariantCulture))
				.Append(" of ")
				.Append(focus.Seeds.Count.ToString(CultureInfo.InvariantCulture))
				.Append(focus.Seeds.Count == 1 ? " seed" : " seeds")
				.Append(degraded.Length == 1 ? " has no resolved links (" : " have no resolved links (")
				.Append(string.Join(", ", reasons))
				.Append(')');
		}
		writer.WriteLine(output.ToString());
	}

	private static void WriteFocusEntry(
		TextWriter writer,
		ImportanceRankingReport report,
		ImportanceRankingEntry entry,
		LocalizationService localization)
	{
		var path = TerminalTextEscaping.EscapeSingleLine(entry.Path);
		if (entry.IsFocusSeed)
		{
			writer.WriteLine($"[Ranking top] {path} — seed");
			return;
		}
		var output = new StringBuilder(256)
			.Append("[Ranking top] ")
			.Append(path)
			.Append(" — ");
		if (entry.Hop is { } hop)
		{
			output.Append("hop ")
				.Append(hop.ToString(CultureInfo.InvariantCulture));
			if (entry.Via is { } via)
			{
				output.Append(" · ")
					.Append(RelationText(via.Relation))
					.Append(' ')
					.Append(TerminalTextEscaping.EscapeSingleLine(via.Path));
			}
			output.Append(" · ");
		}
		else
		{
			output.Append("unreachable · ");
		}
		var commits = entry.Commits?.ToString(CultureInfo.InvariantCulture) ??
		              $"{localization["Terminal.Ranking.Unavailable"]}: {entry.GitUnavailableReason}";
		output.Append("dependents ")
			.Append(entry.Dependents.ToString(CultureInfo.InvariantCulture))
			.Append(" · dependencies ")
			.Append(entry.Dependencies.ToString(CultureInfo.InvariantCulture))
			.Append(" · commits ")
			.Append(commits)
			.Append('/')
			.Append(report.GitWindow.ToString(CultureInfo.InvariantCulture))
			.Append(RoleSuffix(entry, localization))
			.Append(" · priority ")
			.Append(entry.Priority.ToString(CultureInfo.InvariantCulture))
			.Append(" (importance ")
			.Append(entry.BaseImportancePriority.GetValueOrDefault().ToString(CultureInfo.InvariantCulture))
			.Append(')')
			.Append("; graph ")
			.Append(entry.HasGraphFacts ? "available" : "unavailable")
			.Append("; git ")
			.Append(entry.HasGitHistory ? "available" : "unavailable")
			.Append("; main contribution: ")
			.Append(SignalToken(entry.MainContribution));
		if (entry.ConfidenceLimited)
			output.Append("; confidence limited");
		writer.WriteLine(output.ToString());
	}

	private static string SeedStateToken(FocusSeedState state) => state switch
	{
		FocusSeedState.Resolved => "resolved links",
		FocusSeedState.NoResolvedNeighbors => "facts but no resolved neighbors",
		FocusSeedState.ExtractionFailed => "fact extraction failed",
		FocusSeedState.Unsupported => "no supported facts",
		_ => throw new ArgumentOutOfRangeException(nameof(state), state, null)
	};

	private static string RelationText(FocusRankingRelation relation) => relation switch
	{
		FocusRankingRelation.DependentOf => "dependent of",
		FocusRankingRelation.DependencyOf => "dependency of",
		FocusRankingRelation.LinkedWith => "linked with",
		_ => throw new ArgumentOutOfRangeException(nameof(relation), relation, null)
	};

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
