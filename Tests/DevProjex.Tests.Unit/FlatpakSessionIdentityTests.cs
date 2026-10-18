using DevProjex.Infrastructure.LiveContext;
using DevProjex.Infrastructure.Persistence;
using DevProjex.Infrastructure.AgentJournal;

namespace DevProjex.Tests.Unit;

public sealed class FlatpakSessionIdentityTests
{
	private static readonly DateTimeOffset Epoch = new(2026, 10, 18, 0, 0, 0, TimeSpan.Zero);
	[Fact]
	public async Task JournalSessionsWithTheSamePidAndSecondRemainIndependent()
	{
		using var sandbox = FlatpakEnvironment.UseForTesting(new(true, "io.github.Avazbek22.DevProjex"));
		using var directory = new TemporaryDirectory();
		var now = Epoch;
		var first = CreateJournalSession(directory.Path, now, "00000000000000000000000000000001");
		var second = CreateJournalSession(directory.Path, now, "00000000000000000000000000000002");
		var active = new List<LiveSessionRecord>
		{
			new(2, now, null, null, [directory.Path], now) { SandboxSessionId = first.SandboxSessionId },
			new(2, now, null, null, [directory.Path], now) { SandboxSessionId = second.SandboxSessionId }
		};
		using var store = new AgentJournalStore(() => directory.Path, new MutableClock(now), activeSessionProvider: () => active);
		await store.StartSession(first, TestContext.Current.CancellationToken);
		await store.StartSession(second, TestContext.Current.CancellationToken);
		var sessions = await store.ListSessionsAsync(cancellationToken: TestContext.Current.CancellationToken);
		Assert.Equal(2, sessions.Count);
		Assert.All(sessions, session => Assert.True(session.IsLive));
		active.RemoveAt(0);
		sessions = await store.ListSessionsAsync(cancellationToken: TestContext.Current.CancellationToken);
		Assert.False(sessions.Single(session => session.Id == first.Id).IsLive);
		Assert.True(sessions.Single(session => session.Id == second.Id).IsLive);
	}

	[Fact]
	public async Task OrdinaryJournalRetainsItsIdAndSerializedShape()
	{
		using var ordinary = FlatpakEnvironment.UseForTesting(new(false, null));
		using var directory = new TemporaryDirectory();
		var now = new DateTimeOffset(2026, 10, 17, 1, 2, 3, TimeSpan.Zero);
		var session = CreateJournalSession(directory.Path, now, null);
		Assert.Equal("20261017-010203-2", session.Id);
		using var store = new AgentJournalStore(() => directory.Path, new MutableClock(now));
		await store.StartSession(session, TestContext.Current.CancellationToken);
		Assert.DoesNotContain("sandboxSessionId", await File.ReadAllTextAsync(
			Path.Combine(store.DirectoryPath, session.Id + ".jsonl"), TestContext.Current.CancellationToken));
	}

	private static AgentJournalSession CreateJournalSession(string root, DateTimeOffset now, string? sandboxId) =>
		new(AgentJournalStore.CreateSessionId(now, 2, sandboxId), now, null, 2, now, "Client", "1",
			AgentJournalMode.Live, [new(root, "Project")], AgentJournalToolSet.Full, "5.2", false,
			AgentJournalTotals.Empty, true)
		{ SandboxSessionId = sandboxId };

	[Fact]
	public async Task SeparateNamespacesWithTheSamePidAndStartTimeCannotOverwriteEachOther()
	{
		using var sandbox = FlatpakEnvironment.UseForTesting(new(true, "io.github.Avazbek22.DevProjex"));
		using var directory = new TemporaryDirectory();
		var clock = new MutableClock(Epoch);
		var registry = new LiveSessionRegistry(() => directory.Path, clock,
			_ => throw new InvalidOperationException("Another PID namespace cannot be inspected."));
		using var first = registry.Start(2, clock.GetUtcNow(), [directory.Path], AgentJournalMode.Live);
		using var second = registry.Start(2, clock.GetUtcNow(), [directory.Path], AgentJournalMode.Live);
		Assert.NotEqual(first.Path, second.Path);
		Assert.Equal(2, registry.ReadActive().Count);
		first.Dispose();
		Assert.Single(registry.ReadActive());
		Assert.True(File.Exists(second.Path));
		var abandonedRecord = File.ReadAllText(second.Path);
		await second.DisposeAsync();
		File.WriteAllText(second.Path, abandonedRecord);
		clock.Advance(LiveSessionRegistry.StaleHeartbeatAge + TimeSpan.FromSeconds(1));
		Assert.Empty(registry.ReadActive());
	}

	[Fact]
	public void OrdinaryRegistrationStillUsesPidNameAndOmitsSandboxMetadata()
	{
		using var ordinary = FlatpakEnvironment.UseForTesting(new(false, null));
		using var directory = new TemporaryDirectory();
		var now = Epoch;
		var registry = new LiveSessionRegistry(() => directory.Path, new MutableClock(now), processStartProvider: _ => now);
		using var writer = registry.Start(123, now, [directory.Path], AgentJournalMode.Live);
		Assert.Equal("123.json", Path.GetFileName(writer.Path));
		Assert.DoesNotContain("SandboxSessionId", File.ReadAllText(writer.Path));
		Assert.Single(registry.ReadActive());
	}

	[Fact]
	public void MissingXdgStateUsesThePrivateApplicationDirectoryOnlyInFlatpak()
	{
		using var sandbox = FlatpakEnvironment.UseForTesting(new(true, "io.github.Avazbek22.DevProjex"));
		using var directory = new TemporaryDirectory();
		var root = UserDataPathResolver.Resolve(UserDataDirectoryKind.State, false,
			(_, _) => directory.Path, name => name == "HOME" ? directory.Path : null);
		Assert.Equal(Path.Combine(directory.Path, ".var", "app", "io.github.Avazbek22.DevProjex", "state"), root);
	}

	private sealed class MutableClock(DateTimeOffset now) : TimeProvider
	{
		private DateTimeOffset _now = now;
		public override DateTimeOffset GetUtcNow() => _now;
		public void Advance(TimeSpan elapsed) => _now += elapsed;
	}
}
