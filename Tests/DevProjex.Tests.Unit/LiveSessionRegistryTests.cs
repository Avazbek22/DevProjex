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
	public void DirectoryPath_RestrictsAnExistingUnixSessionDirectory()
	{
		if (OperatingSystem.IsWindows())
		{
			Assert.Skip("Unix file modes do not apply on Windows.");
			return;
		}

		using var workspace = new TemporaryDirectory();
		var directory = Directory.CreateDirectory(Path.Combine(workspace.Path, "live-sessions")).FullName;
		File.SetUnixFileMode(
			directory,
			UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
			UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
			UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
		var registry = new LiveSessionRegistry(() => workspace.Path);

		Assert.Equal(directory, registry.DirectoryPath);
		Assert.Equal(
			UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
			File.GetUnixFileMode(directory));
	}

	[Fact]
	public async Task WriterKeepsUnixSessionRecordsPrivateAcrossAtomicHeartbeats()
	{
		if (OperatingSystem.IsWindows())
		{
			Assert.Skip("Unix file modes do not apply on Windows.");
			return;
		}

		using var workspace = new TemporaryDirectory();
		var started = new DateTimeOffset(2026, 9, 18, 1, 2, 3, TimeSpan.Zero);
		var registry = new LiveSessionRegistry(
			() => workspace.Path,
			new MutableTimeProvider(started),
			_ => started);
		await using var writer = registry.Start(42, started, [workspace.Path]);
		const UnixFileMode privateFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
		Assert.Equal(
			UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
			File.GetUnixFileMode(registry.DirectoryPath));
		Assert.Equal(privateFileMode, File.GetUnixFileMode(writer.Path));

		writer.UpdateClient("sample-client", "2.4.1");

		Assert.Equal(privateFileMode, File.GetUnixFileMode(writer.Path));
		Assert.Empty(Directory.EnumerateFiles(registry.DirectoryPath, "*.tmp"));
	}

	[Fact]
	public void ReaderRepairsAnExistingUnixSessionRecordBeforeReturningIt()
	{
		if (OperatingSystem.IsWindows())
		{
			Assert.Skip("Unix file modes do not apply on Windows.");
			return;
		}

		using var workspace = new TemporaryDirectory();
		var started = new DateTimeOffset(2026, 9, 18, 1, 2, 3, TimeSpan.Zero);
		var registry = new LiveSessionRegistry(
			() => workspace.Path,
			new MutableTimeProvider(started),
			_ => started);
		var path = registry.GetPath(42);
		registry.Write(new LiveSessionRecord(42, started, "client", "1", [workspace.Path], started));
		File.SetUnixFileMode(
			path,
			UnixFileMode.UserRead | UnixFileMode.UserWrite |
			UnixFileMode.GroupRead | UnixFileMode.OtherRead);

		Assert.Single(registry.ReadActive(workspace.Path));
		Assert.Equal(
			UnixFileMode.UserRead | UnixFileMode.UserWrite,
			File.GetUnixFileMode(path));
	}

	[Fact]
	public void ReaderDoesNotFollowASymbolicLinkSessionRecord()
	{
		if (OperatingSystem.IsWindows())
		{
			Assert.Skip("Symbolic-link creation may require elevated Windows privileges.");
			return;
		}

		using var workspace = new TemporaryDirectory();
		using var outside = new TemporaryDirectory();
		var registry = new LiveSessionRegistry(() => workspace.Path);
		var protectedPath = Path.Combine(outside.Path, "record.json");
		File.WriteAllText(protectedPath, "keep");
		var link = registry.GetPath(42);
		File.CreateSymbolicLink(link, protectedPath);

		Assert.Empty(registry.ReadActive());
		Assert.Equal("keep", File.ReadAllText(protectedPath));
		Assert.False(File.Exists(link));
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
	public void ReaderRejectsARecordWhosePidDoesNotMatchItsFileName()
	{
		using var temporary = new TemporaryDirectory();
		var started = new DateTimeOffset(2026, 9, 18, 1, 2, 3, TimeSpan.Zero);
		var registry = new LiveSessionRegistry(
			() => temporary.Path,
			new MutableTimeProvider(started.AddSeconds(1)),
			pid => pid == 43 ? started : null);
		registry.Write(new LiveSessionRecord(43, started, "client", "1", [temporary.Path], started));
		var mismatchedPath = Path.Combine(registry.DirectoryPath, "42.json");
		File.Move(registry.GetPath(43), mismatchedPath);

		Assert.Empty(registry.ReadActive());
		Assert.False(File.Exists(mismatchedPath));
	}

	[Fact]
	public void ReaderRejectsHeartbeatBeyondTheFutureClockSkew()
	{
		using var temporary = new TemporaryDirectory();
		var started = new DateTimeOffset(2026, 9, 18, 1, 2, 3, TimeSpan.Zero);
		var now = started.AddMinutes(1);
		var registry = new LiveSessionRegistry(
			() => temporary.Path,
			new MutableTimeProvider(now),
			_ => started);
		registry.Write(new LiveSessionRecord(
			42,
			started,
			"client",
			"1",
			[temporary.Path],
			now + LiveSessionRegistry.HeartbeatInterval + TimeSpan.FromTicks(1)));

		Assert.Empty(registry.ReadActive());
		Assert.False(File.Exists(registry.GetPath(42)));
	}

	[Fact]
	public void ReaderRejectsHeartbeatBeforeProcessStart()
	{
		using var temporary = new TemporaryDirectory();
		var started = new DateTimeOffset(2026, 9, 18, 1, 2, 3, TimeSpan.Zero);
		var registry = new LiveSessionRegistry(
			() => temporary.Path,
			new MutableTimeProvider(started.AddSeconds(1)),
			_ => started);
		registry.Write(new LiveSessionRecord(
			42,
			started,
			"client",
			"1",
			[temporary.Path],
			started - TimeSpan.FromTicks(1)));

		Assert.Empty(registry.ReadActive());
		Assert.False(File.Exists(registry.GetPath(42)));
	}

	[Fact]
	public void ReaderBoundsTheNumberOfRegistryEntries()
	{
		using var temporary = new TemporaryDirectory();
		var started = new DateTimeOffset(2026, 9, 18, 1, 2, 3, TimeSpan.Zero);
		var registry = new LiveSessionRegistry(
			() => temporary.Path,
			new MutableTimeProvider(started.AddSeconds(1)),
			_ => started);
		for (var index = 1; index <= 1_025; index++)
		{
			registry.Write(new LiveSessionRecord(
				index,
				started,
				"client",
				"1",
				[temporary.Path],
				started));
		}

		var records = registry.ReadActive();

		Assert.Equal(1_024, records.Count);
	}

	[Fact]
	public void RegistryRejectsASymbolicLinkServiceDirectory()
	{
		using var temporary = new TemporaryDirectory();
		using var outside = new TemporaryDirectory();
		var link = Path.Combine(temporary.Path, "live-sessions");
		try
		{
			Directory.CreateSymbolicLink(link, outside.Path);
		}
		catch (Exception linkException) when (linkException is IOException or UnauthorizedAccessException)
		{
			Assert.Skip("Creating directory symbolic links is unavailable in this environment.");
			return;
		}
		var registry = new LiveSessionRegistry(() => temporary.Path);

		Assert.Empty(registry.ReadActive());
		Assert.Empty(Directory.EnumerateFileSystemEntries(outside.Path));
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
