using DevProjex.Application.Ranking;

namespace DevProjex.Tests.Terminal;

public sealed class RankingOutputContractTests
{
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
