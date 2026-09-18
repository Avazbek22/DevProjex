using DevProjex.Infrastructure.LiveContext;

namespace DevProjex.Tests.Unit;

public sealed class LiveSessionRegistryTests
{
	[Fact]
	public void DirectoryPath_IsDirectlyUnderTheStateRoot()
	{
		using var workspace = new TemporaryDirectory();
		var registry = new LiveSessionRegistry(() => workspace.Path);

		Assert.Equal(
			Path.Combine(workspace.Path, "live-sessions"),
			registry.DirectoryPath);
	}

	[Fact]
	public async Task WriterPublishesClientAndRemovesRecordOnDispose()
	{
		using var temporary = new TemporaryDirectory();
		var started = new DateTimeOffset(2026, 9, 18, 1, 2, 3, TimeSpan.Zero);
		var clock = new MutableTimeProvider(started.AddMinutes(1));
		var registry = new LiveSessionRegistry(
			() => temporary.Path,
			clock,
			pid => pid == 42 ? started : null);
		var writer = registry.Start(42, started, [temporary.Path]);

		writer.UpdateClient("sample-client", "2.4.1");

		var record = Assert.Single(registry.ReadActive(temporary.Path));
		Assert.Equal("sample-client", record.ClientName);
		Assert.Equal("2.4.1", record.ClientVersion);
		Assert.Equal(clock.GetUtcNow(), record.HeartbeatUtc);
		Assert.True(File.Exists(writer.Path));

		await writer.DisposeAsync();
		Assert.False(File.Exists(writer.Path));
	}

	[Fact]
	public async Task ReaderRemovesRecordWhenPidStartDoesNotMatch()
	{
		using var temporary = new TemporaryDirectory();
		var started = new DateTimeOffset(2026, 9, 18, 1, 2, 3, TimeSpan.Zero);
		var registry = new LiveSessionRegistry(
			() => temporary.Path,
			new MutableTimeProvider(started.AddSeconds(1)),
			_ => started.AddMinutes(-1));
		await using var writer = registry.Start(42, started, [temporary.Path]);

		Assert.Empty(registry.ReadActive());
		Assert.False(File.Exists(writer.Path));
	}

	[Fact]
	public async Task ReaderRemovesRecordWhenHeartbeatIsStale()
	{
		using var temporary = new TemporaryDirectory();
		var started = new DateTimeOffset(2026, 9, 18, 1, 2, 3, TimeSpan.Zero);
		var clock = new MutableTimeProvider(started);
		var registry = new LiveSessionRegistry(
			() => temporary.Path,
			clock,
			_ => started);
		await using var writer = registry.Start(42, started, [temporary.Path]);

		clock.Advance(LiveSessionRegistry.StaleHeartbeatAge + TimeSpan.FromTicks(1));

		Assert.Empty(registry.ReadActive());
		Assert.False(File.Exists(writer.Path));
	}

	[Fact]
	public async Task ReaderRemovesRecordWhenProcessIdentityCannotBeRead()
	{
		using var temporary = new TemporaryDirectory();
		var started = new DateTimeOffset(2026, 9, 18, 1, 2, 3, TimeSpan.Zero);
		var registry = new LiveSessionRegistry(
			() => temporary.Path,
			new MutableTimeProvider(started.AddSeconds(1)),
			_ => throw new System.ComponentModel.Win32Exception());
		await using var writer = registry.Start(42, started, [temporary.Path]);

		Assert.Empty(registry.ReadActive());
		Assert.False(File.Exists(writer.Path));
	}

	[Fact]
	public async Task ReaderKeepsRecordWhenItIsTemporarilyLocked()
	{
		using var temporary = new TemporaryDirectory();
		var started = new DateTimeOffset(2026, 9, 18, 1, 2, 3, TimeSpan.Zero);
		var registry = new LiveSessionRegistry(
			() => temporary.Path,
			new MutableTimeProvider(started.AddSeconds(1)),
			_ => started);
		await using var writer = registry.Start(42, started, [temporary.Path]);

		using (new FileStream(writer.Path, FileMode.Open, FileAccess.Read, FileShare.None))
		{
			Assert.Empty(registry.ReadActive());
			Assert.True(File.Exists(writer.Path));
		}

		Assert.Single(registry.ReadActive());
	}

	[Fact]
	public void ReaderRemovesMalformedRecord()
	{
		using var temporary = new TemporaryDirectory();
		var registry = new LiveSessionRegistry(() => temporary.Path);
		Directory.CreateDirectory(registry.DirectoryPath);
		var path = Path.Combine(registry.DirectoryPath, "42.json");
		File.WriteAllBytes(path, [0x7B, 0x22, 0xFF, 0x22, 0x3A, 0x31, 0x7D]);

		Assert.Empty(registry.ReadActive());
		Assert.False(File.Exists(path));
	}

	[Fact]
	public void ReaderRemovesOversizedRecordWithoutDeserializingIt()
	{
		using var temporary = new TemporaryDirectory();
		var registry = new LiveSessionRegistry(() => temporary.Path);
		Directory.CreateDirectory(registry.DirectoryPath);
		var path = Path.Combine(registry.DirectoryPath, "42.json");
		File.WriteAllBytes(path, new byte[(64 * 1024) + 1]);

		Assert.Empty(registry.ReadActive());
		Assert.False(File.Exists(path));
	}

	[Fact]
	public async Task WriterRetriesAfterSessionDirectoryBecomesWritable()
	{
		using var temporary = new TemporaryDirectory();
		var unavailableRoot = temporary.CreateFile("blocked", "not a directory");
		var stateRoot = unavailableRoot;
		var started = new DateTimeOffset(2026, 9, 18, 1, 2, 3, TimeSpan.Zero);
		var registry = new LiveSessionRegistry(
			() => stateRoot,
			new MutableTimeProvider(started.AddSeconds(1)),
			_ => started);
		await using var writer = registry.Start(42, started, [temporary.Path]);

		writer.UpdateClient("sample-client", "2.4.1");
		Assert.Empty(registry.ReadActive());

		stateRoot = temporary.CreateFolder("available");
		writer.WriteHeartbeat();

		var record = Assert.Single(registry.ReadActive(temporary.Path));
		Assert.Equal("sample-client", record.ClientName);
		Assert.Equal("2.4.1", record.ClientVersion);
	}

	[Fact]
	public async Task ReaderKeepsHeartbeatAtTheStaleBoundary()
	{
		using var temporary = new TemporaryDirectory();
		var started = new DateTimeOffset(2026, 9, 18, 1, 2, 3, TimeSpan.Zero);
		var clock = new MutableTimeProvider(started);
		var registry = new LiveSessionRegistry(
			() => temporary.Path,
			clock,
			_ => started);
		await using var writer = registry.Start(42, started, [temporary.Path]);

		clock.Advance(LiveSessionRegistry.StaleHeartbeatAge);

		Assert.Single(registry.ReadActive());
	}

	[Theory]
	[InlineData("claude-code", "Claude Code")]
	[InlineData("codex", "Codex")]
	[InlineData("sample-client", "sample-client")]
	public void ClientNameUsesKnownDisplayForm(string source, string expected)
	{
		Assert.Equal(expected, LiveSessionRegistry.FormatClientName(source));
	}

	private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
	{
		private DateTimeOffset value = now;

		public override DateTimeOffset GetUtcNow() => value;

		public void Advance(TimeSpan elapsed) => value += elapsed;
	}
}
