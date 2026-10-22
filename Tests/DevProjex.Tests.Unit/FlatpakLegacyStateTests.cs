using DevProjex.Infrastructure.AgentJournal;
using DevProjex.Infrastructure.LiveContext;

namespace DevProjex.Tests.Unit;

public sealed class FlatpakLegacyStateTests
{
	private static readonly DateTimeOffset Epoch = new(2026, 10, 18, 0, 0, 0, TimeSpan.Zero);

	[Fact]
	public void LegacyPidRegistrationWithoutSandboxMetadataStillReadsOutsideFlatpak()
	{
		using var ordinary = FlatpakEnvironment.UseForTesting(new(false, null));
		using var directory = new TemporaryDirectory();
		var registry = new LiveSessionRegistry(() => directory.Path, new Clock(), _ => Epoch);
		var path = Path.Combine(registry.DirectoryPath, "42.json");
		var json = "{\"Pid\":42,\"ProcessStartUtc\":\"2026-10-18T00:00:00+00:00\",\"ClientName\":null,\"ClientVersion\":null,\"Roots\":[" + JsonSerializer.Serialize(directory.Path) + "],\"HeartbeatUtc\":\"2026-10-18T00:00:00+00:00\",\"Mode\":1}";
		File.WriteAllText(path, json);
		var record = Assert.Single(registry.ReadActive());
		Assert.Equal(42, record.Pid);
		Assert.Null(record.SandboxSessionId);
		Assert.Equal(json, File.ReadAllText(path));
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task LegacyJournalHeaderRemainsReadableWithoutRewritingItsBytes(bool flatpak)
	{
		using var directory = new TemporaryDirectory();
		var id = "20261018-000000-42";
		using (FlatpakEnvironment.UseForTesting(new(false, null)))
		using (var writer = new AgentJournalStore(() => directory.Path, new Clock(), () => []))
			await writer.StartSession(new(id, Epoch, null, 42, Epoch, "legacy", "1", AgentJournalMode.Live,
				[new(directory.Path, "project")], AgentJournalToolSet.Full, "5.2", false, AgentJournalTotals.Empty, false), TestContext.Current.CancellationToken);
		var path = Path.Combine(directory.Path, "DevProjex", "agent-journal", id + ".jsonl");
		var bytes = File.ReadAllBytes(path);
		Assert.DoesNotContain("sandboxSessionId", Encoding.UTF8.GetString(bytes));
		using var environment = FlatpakEnvironment.UseForTesting(new(flatpak, flatpak ? "test.app" : null));
		using var reader = new AgentJournalStore(() => directory.Path, new Clock(), () => []);
		var session = Assert.Single(await reader.ListSessionsAsync(cancellationToken: TestContext.Current.CancellationToken));
		Assert.Equal(id, session.Id);
		Assert.Null(session.SandboxSessionId);
		Assert.False(session.IsLive);
		Assert.Equal(bytes, File.ReadAllBytes(path));
	}

	private sealed class Clock : TimeProvider
	{
		public override DateTimeOffset GetUtcNow() => Epoch;
	}
}
