using DevProjex.Application.Ranking;

namespace DevProjex.Tests.Terminal;

public sealed class RankingOutputContractTests
{
	[Fact]
	public void WriteFocusReportKeepsStatusAndPathExplanationsDistinct()
	{
		using var workspace = new TemporaryDirectory();
		using var services = new TerminalServiceFactory(
				() => workspace.CreateDirectory("focus-data"))
			.Create(AppLanguage.En);
		var seed = new ImportanceRankingEntry(
			"Seed.cs", "Seed.cs", 1, 0.2, 1, 1, 2, 1,
			ImportanceFileRole.Source, true, true)
		{
			Hop = 0,
			BaseImportancePriority = 5,
			IsFocusSeed = true,
			MainContribution = ImportanceRankingSignal.Graph
		};
		var neighbor = new ImportanceRankingEntry(
			"Neighbor.cs", "Neighbor.cs", 2, 0.8, 3, 2, 4, 1,
			ImportanceFileRole.Source, true, true)
		{
			Hop = 1,
			BaseImportancePriority = 1,
			Via = new FocusRankingVia("Seed.cs", FocusRankingRelation.DependencyOf),
			MainContribution = ImportanceRankingSignal.Graph
		};
		var report = new ImportanceRankingReport(
			"focus-v1", [seed, neighbor], [seed, neighbor], 8, 6, 1, 0.75,
			200, 20, ProjectGitHistoryUnavailableReason.None, false,
			ImportanceRankingService.GraphVariant)
		{
			Focus = new FocusRankingSummary(
				"focus-v1", "importance-v1",
				[
					new FocusRankingSeed("Seed.cs", "Seed.cs", FocusSeedState.Resolved),
					new FocusRankingSeed("MissingFacts.cs", "MissingFacts.cs", FocusSeedState.Unsupported)
				],
				new SortedDictionary<int, int> { [0] = 2, [1] = 3 },
				2, 9, 1)
		};
		var budget = new ProjectContextTokenBudgetReport(
			10, 1, 1, 8, 4,
			[new ProjectContextTokenBudgetSkippedFile("Neighbor.cs", 4, 2, 2, 1, 1, neighbor.Via)],
			0,
			[new ProjectContextTokenBudgetSkippedFile("Neighbor.cs", 4, 2, 2, 1, 1, neighbor.Via)]);
		using var rankingOutput = new StringWriter();
		using var budgetOutput = new StringWriter();

		RankingOutput.Write(rankingOutput, report, budget, services.Localization);
		TokenBudgetOutput.Write(budgetOutput, budget, services.Localization, report);

		var rankingText = rankingOutput.ToString();
		Assert.Contains("[Ranking] focus-v1 · 2 seeds · hops 0:2 1:3 8+:2 · max hop 9 · unreachable 1", rankingText, StringComparison.Ordinal);
		Assert.Contains("focus degraded: 1 of 2 seeds has no resolved links (no supported facts)", rankingText, StringComparison.Ordinal);
		Assert.Contains("[Ranking top] Seed.cs — seed", rankingText, StringComparison.Ordinal);
		Assert.Contains("hop 1 · dependency of Seed.cs", rankingText, StringComparison.Ordinal);
		Assert.Contains("priority 2 (importance 1)", rankingText, StringComparison.Ordinal);
		Assert.Contains("[Skipped] Neighbor.cs — hop 1, priority 2", budgetOutput.ToString(), StringComparison.Ordinal);
	}

	[Fact]
	public void WriteExplainsCoverageShallowHistoryAndConfidenceLimitedContribution()
	{
		using var workspace = new TemporaryDirectory();
		using var services = new TerminalServiceFactory(
				() => workspace.CreateDirectory("data"))
			.Create(AppLanguage.En);
		var entry = new ImportanceRankingEntry(
			"Coordinator.cs",
			"Coordinator.cs",
			3,
			0.04,
			0,
			4,
			1,
			1,
			ImportanceFileRole.Source,
			false,
			true)
		{
			Confidence = 0.05,
			MainContribution = ImportanceRankingSignal.Role,
			ConfidenceLimited = true,
			IsCoordinator = true
		};
		var report = new ImportanceRankingReport(
			ImportanceRankingService.AlgorithmId,
			[entry],
			[entry],
			100,
			60,
			20,
			0.6,
			200,
			1,
			ProjectGitHistoryUnavailableReason.None,
			false,
			ImportanceRankingService.GraphVariant)
		{
			ResolvedInternalReferences = 2,
			InternalReferenceCandidates = 20,
			ResolvedInternalReferenceCoverage = 0.1,
			FilesWithResolvedEdges = 3,
			GitHistoryIsShallow = true,
			GitHistoryIsComplete = false,
			HasMissingSignals = true,
			MissingSignalPolicy = ImportanceMissingSignalPolicy.ConfidenceLimited
		};
		using var output = new StringWriter();

		RankingOutput.Write(output, report, tokenBudget: null, services.Localization);

		var text = output.ToString();
		Assert.Contains("facts 60%", text, StringComparison.Ordinal);
		Assert.Contains("resolved internal references 2/20 (10%)", text, StringComparison.Ordinal);
		Assert.Contains("files with resolved edges 3", text, StringComparison.Ordinal);
		Assert.Contains("read 1/200 commits; shallow history", text, StringComparison.Ordinal);
		Assert.Contains("coordinator", text, StringComparison.Ordinal);
		Assert.Contains("priority 3; graph unavailable; git available; main contribution: role; confidence limited", text, StringComparison.Ordinal);
	}
}
