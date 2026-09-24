using DevProjex.Infrastructure.AgentJournal;
using DevProjex.Kernel.Models;

namespace DevProjex.Tests.Integration;

public sealed class AgentJournalRetentionIntegrationTests
{
	[Fact]
	public async Task RetentionUsesTheInjectedClockAtTheNextJournalRead()
	{
		using var workspace = new TemporaryDirectory();
		var started = new DateTimeOffset(2026, 9, 20, 8, 0, 0, TimeSpan.Zero);
		var clock = new ManualTimeProvider(started);
		using var store = new AgentJournalStore(
			() => workspace.Path,
			clock,
			activeSessionProvider: static () => []);
		var session = new AgentJournalSession(
			AgentJournalStore.CreateSessionId(started, 42),
			started,
			null,
			42,
			started.AddSeconds(-1),
			"retention-test",
			"1.0",
			AgentJournalMode.Standard,
			[new AgentJournalRoot(workspace.Path, "project")],
			AgentJournalToolSet.Full,
			"5.2",
			false,
			AgentJournalTotals.Empty,
			false);
		await store.StartSession(session, TestContext.Current.CancellationToken);
		var path = Path.Combine(store.DirectoryPath, session.Id + ".jsonl");
		File.SetLastWriteTimeUtc(path, started.UtcDateTime);

		clock.Advance(TimeSpan.FromDays(31));
		var sessions = await store.ListSessionsAsync(
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.Empty(sessions);
		Assert.False(File.Exists(path));
	}

	private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
	{
		private DateTimeOffset _utcNow = utcNow;

		public override DateTimeOffset GetUtcNow() => _utcNow;

		public void Advance(TimeSpan duration) => _utcNow += duration;
	}
}
