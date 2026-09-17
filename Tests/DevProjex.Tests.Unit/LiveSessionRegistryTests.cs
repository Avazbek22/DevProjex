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
