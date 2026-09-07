using DevProjex.Application.Ranking;
using DevProjex.Infrastructure.Git;

namespace DevProjex.Tests.Terminal;

public sealed class ImportanceRankingProgressTests
{
	[Fact]
	public async Task RankAsyncReportsThreeOrderedBoundedStages()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		var files = new[]
		{
			workspace.WriteFile("project/A.cs", "class A { B Value = new(); }"),
			workspace.WriteFile("project/B.cs", "class B { }"),
			workspace.WriteFile("project/C.cs", "class C { B Value = new(); }")
		};
		using var services = new TerminalServiceFactory(
				() => workspace.CreateDirectory("data"))
			.Create(AppLanguage.En);
		var events = new List<ImportanceRankingProgress>();
		var progress = new SynchronousProgress<ImportanceRankingProgress>(events.Add);

		await new ImportanceRankingService(
				services.DependencyFactsEngine,
				new ProjectGitHistoryReader())
			.RankAsync(project, files, progress, TestContext.Current.CancellationToken);

		Assert.NotEmpty(events);
		Assert.True(events.Count <= 150, $"Progress emitted {events.Count} events.");
		Assert.Equal(
			[
				ImportanceRankingStage.IndexingFacts,
				ImportanceRankingStage.ReadingHistory,
				ImportanceRankingStage.ComputingPriorities
			],
			events.Select(static value => value.Stage).Distinct().ToArray());
		Assert.All(events, value => Assert.InRange(value.Completed, 0, value.Total));
		Assert.Equal(events[^1].Total, events[^1].Completed);
	}

	private sealed class SynchronousProgress<T>(Action<T> callback) : IProgress<T>
	{
		public void Report(T value) => callback(value);
	}
}
