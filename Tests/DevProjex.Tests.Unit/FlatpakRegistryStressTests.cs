using DevProjex.Infrastructure.AgentJournal;
using DevProjex.Infrastructure.LiveContext;

namespace DevProjex.Tests.Unit;

public sealed class FlatpakRegistryStressTests
{
	private static IDisposable Sandbox() => FlatpakEnvironment.UseForTesting(new(true, "io.github.Avazbek22.DevProjex"));

	[Fact]
	public async Task FortyEightConcurrentSessionsKeepIndependentLiveRecordsAndJournals()
	{
		using var sandbox = Sandbox();
		using var directory = new TemporaryDirectory();
		var clock = new Clock(DateTimeOffset.UtcNow);
		var registry = new LiveSessionRegistry(() => directory.Path, clock,
			_ => throw new InvalidOperationException("PID namespaces are private."));
		var writers = await Task.WhenAll(Enumerable.Range(0, 48).Select(_ => Task.Run(
			() => registry.Start(2, clock.GetUtcNow(), [directory.Path]), TestContext.Current.CancellationToken)));
		try
		{
			Assert.Equal(48, registry.ReadActive().Count);
			using var journal = new AgentJournalStore(() => directory.Path, clock, () => registry.ReadActive());
			await Task.WhenAll(registry.ReadActive().Select(record => journal.StartSession(new(
				AgentJournalStore.CreateSessionId(clock.GetUtcNow(), record.Pid, record.SandboxSessionId),
				clock.GetUtcNow(), null, record.Pid, record.ProcessStartUtc, "client", "1", AgentJournalMode.Live,
				[new(directory.Path, "Project")], AgentJournalToolSet.Full, "5.2", false, AgentJournalTotals.Empty, true)
			{ SandboxSessionId = record.SandboxSessionId }, TestContext.Current.CancellationToken).AsTask()));
			Assert.Equal(48, (await journal.ListSessionsAsync(cancellationToken: TestContext.Current.CancellationToken)).Count);
			await Task.WhenAll(writers.Select(writer => Task.Run(() =>
			{
				for (var iteration = 0; iteration < 2; iteration++)
				{
					writer.WriteHeartbeat();
					registry.ReadActive();
				}
			}, TestContext.Current.CancellationToken)));
			await Task.WhenAll(writers.Select(static writer => writer.DisposeAsync().AsTask()));
			Assert.Empty(registry.ReadActive());
			Assert.All(await journal.ListSessionsAsync(cancellationToken: TestContext.Current.CancellationToken),
				static session => Assert.False(session.IsLive));
		}
		finally
		{
			await Task.WhenAll(writers.Select(static writer => writer.DisposeAsync().AsTask()));
		}
		Assert.Empty(registry.ReadActive());
	}

	[Theory]
	[InlineData("")]
	[InlineData("{")]
	[InlineData("{}")]
	[InlineData("null")]
	[InlineData("[]")]
	public void BrokenRecordsAreRemovedWithoutTouchingDirectories(string content)
	{
		using var sandbox = Sandbox();
		using var directory = new TemporaryDirectory();
		var registry = new LiveSessionRegistry(() => directory.Path);
		var path = Path.Combine(registry.DirectoryPath, Guid.NewGuid().ToString("N") + ".json");
		File.WriteAllText(path, content);
		var subdirectory = Path.Combine(registry.DirectoryPath, Guid.NewGuid().ToString("N") + ".json");
		Directory.CreateDirectory(subdirectory);
		Assert.Empty(registry.ReadActive());
		Assert.False(File.Exists(path));
		Assert.True(Directory.Exists(subdirectory));
	}

	[Theory]
	[InlineData("foreign")]
	[InlineData("wrong-guid")]
	[InlineData("pid-name")]
	public void ARecordCannotImpersonateAnotherRegistration(string variant)
	{
		using var sandbox = Sandbox();
		using var directory = new TemporaryDirectory();
		var registry = new LiveSessionRegistry(() => directory.Path);
		using var writer = registry.Start(2, DateTimeOffset.UtcNow.AddSeconds(-1), [directory.Path]);
		var record = Assert.Single(registry.ReadActive());
		var filename = variant == "pid-name" ? "2.json" : Guid.NewGuid().ToString("N") + ".json";
		var path = Path.Combine(registry.DirectoryPath, filename);
		File.Copy(writer.Path, path);
		if (variant == "foreign")
			File.WriteAllText(path, JsonSerializer.Serialize(record with { SandboxSessionId = null }));
		Assert.Equal(record.SandboxSessionId, Assert.Single(registry.ReadActive()).SandboxSessionId);
		Assert.False(File.Exists(path));
		Assert.True(File.Exists(writer.Path));
	}

	[Fact]
	public async Task ConcurrentWriteDeleteAndReadDoNotPublishAPartialRecord()
	{
		using var sandbox = Sandbox();
		using var directory = new TemporaryDirectory();
		var now = DateTimeOffset.UtcNow;
		var registry = new LiveSessionRegistry(() => directory.Path);
		var record = new LiveSessionRecord(2, now, null, null, [directory.Path], now)
		{ SandboxSessionId = Guid.NewGuid().ToString("N") };
		await Task.WhenAll(Enumerable.Range(0, 3).Select(worker => Task.Run(() =>
		{
			for (var iteration = 0; iteration < 80; iteration++)
			{
				if (worker == 0) registry.TryWrite(record);
				else if (worker == 1) registry.Delete(record);
				else Assert.All(registry.ReadActive(), item =>
				{
					Assert.Equal(record.SandboxSessionId, item.SandboxSessionId);
					Assert.Equal(record.Roots, item.Roots);
				});
			}
		}, TestContext.Current.CancellationToken)));
		Assert.True(registry.TryWrite(record));
		Assert.Single(registry.ReadActive());
		registry.Delete(record);
		Assert.Empty(registry.ReadActive());
		Assert.Empty(Directory.GetFiles(registry.DirectoryPath, "*.tmp"));
	}

	[Fact]
	public void AbandonedRegistrationExpiresAtTheHeartbeatBoundary()
	{
		using var sandbox = Sandbox();
		using var directory = new TemporaryDirectory();
		var now = DateTimeOffset.UtcNow;
		var clock = new Clock(now);
		var registry = new LiveSessionRegistry(() => directory.Path, clock);
		var record = new LiveSessionRecord(2, now, null, null, [directory.Path], now)
		{ SandboxSessionId = Guid.NewGuid().ToString("N") };
		registry.Write(record);
		clock.Advance(LiveSessionRegistry.StaleHeartbeatAge);
		Assert.Single(registry.ReadActive());
		clock.Advance(TimeSpan.FromTicks(1));
		Assert.Empty(registry.ReadActive());
		Assert.False(File.Exists(registry.GetPath(record)));
	}

	[Fact]
	public void SymbolicLinkRecordNeverReadsOrChangesItsTarget()
	{
		if (OperatingSystem.IsWindows())
			Assert.Skip("Unix symbolic-link permissions are checked on Linux.");
		using var sandbox = Sandbox();
		using var directory = new TemporaryDirectory();
		var registry = new LiveSessionRegistry(() => directory.Path);
		var outside = directory.CreateFile("outside.txt", "keep");
		var link = Path.Combine(registry.DirectoryPath, Guid.NewGuid().ToString("N") + ".json");
		File.CreateSymbolicLink(link, outside);
		Assert.Empty(registry.ReadActive());
		Assert.Equal("keep", File.ReadAllText(outside));
		Assert.False(File.Exists(link));
	}

	private sealed class Clock(DateTimeOffset now) : TimeProvider
	{
		private DateTimeOffset _now = now;
		public override DateTimeOffset GetUtcNow() => _now;
		public void Advance(TimeSpan elapsed) => _now += elapsed;
	}
}
