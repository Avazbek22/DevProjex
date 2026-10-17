using DevProjex.Infrastructure.AgentJournal;
using DevProjex.Infrastructure.LiveContext;
using DevProjex.Infrastructure.Persistence;
using DevProjex.Infrastructure.TerminalCommands;

namespace DevProjex.Tests.Unit;

[Collection(ProcessEnvironmentCollection.Name)]
public sealed class FlatpakOrdinaryChannelSnapshotTests
{
	[Fact]
	public void ConnectionFragmentsAndShimKeepTheirOriginalBytes()
	{
		using var ordinary = FlatpakEnvironment.UseForTesting(new(false, null));
		var previous = Environment.GetEnvironmentVariable("APPIMAGE_EXTRACT_AND_RUN");
		try
		{
			Environment.SetEnvironmentVariable("APPIMAGE_EXTRACT_AND_RUN", null);
			Assert.Equal("{\"mcpServers\":{\"devprojex\":{\"command\":\"/opt/DevProjex\",\"args\":[\"mcp\",\"--root\",\"/home/person/project\",\"--live\"]}}}",
				McpConnectionFragmentGenerator.Generate(McpConnectionClient.Cursor, McpConnectionMode.Live, "/opt/DevProjex", "/home/person/project"));
			Assert.Equal("[mcp_servers.devprojex]" + Environment.NewLine + "command = \"/opt/DevProjex\"" +
				Environment.NewLine + "args = [\"mcp\", \"--root\", \"/home/person/project\", \"--live\"]",
				McpConnectionFragmentGenerator.Generate(McpConnectionClient.Codex, McpConnectionMode.Live, "/opt/DevProjex", "/home/person/project"));
			Assert.Equal("cd \"/home/person/project\" && claude mcp add --scope local devprojex -- \"/opt/DevProjex\" mcp --root \"/home/person/project\" --live",
				McpConnectionFragmentGenerator.Generate(McpConnectionClient.ClaudeCode, McpConnectionMode.Live, "/opt/DevProjex", "/home/person/project"));
			Assert.Equal("#!/bin/sh\n# DevProjex terminal command wrapper\n# target: /opt/DevProjex\nexport DEVPROJEX_TERMINAL_HOST=1\nexec '/opt/DevProjex' \"$@\"\n",
				TerminalCommandSetupService.BuildWrapperContent("/opt/DevProjex"));
			Assert.Throws<ArgumentException>(() => McpConnectionFragmentGenerator.Generate(McpConnectionClient.Cursor,
				McpConnectionMode.Live, "/opt/DevProjex", "/home/person/line\nnext"));
		}
		finally { Environment.SetEnvironmentVariable("APPIMAGE_EXTRACT_AND_RUN", previous); }
	}

	[Fact]
	public async Task RegistryAndJournalKeepTheirOriginalNamesAndJsonBytes()
	{
		using var ordinary = FlatpakEnvironment.UseForTesting(new(false, null));
		using var directory = new TemporaryDirectory();
		var now = new DateTimeOffset(2026, 10, 17, 0, 0, 0, TimeSpan.Zero);
		var rootJson = JsonSerializer.Serialize(directory.Path);
		var registry = new LiveSessionRegistry(() => directory.Path, new Clock(now), _ => now);
		await using var writer = registry.Start(42, now, [directory.Path]);
		Assert.Equal("42.json", Path.GetFileName(writer.Path));
		Assert.Equal("{\"Pid\":42,\"ProcessStartUtc\":\"2026-10-17T00:00:00+00:00\",\"ClientName\":null,\"ClientVersion\":null,\"Roots\":[" +
			rootJson + "],\"HeartbeatUtc\":\"2026-10-17T00:00:00+00:00\",\"Mode\":1}",
			await File.ReadAllTextAsync(writer.Path, TestContext.Current.CancellationToken));
		using var journal = new AgentJournalStore(() => directory.Path, new Clock(now));
		var id = AgentJournalStore.CreateSessionId(now, 42);
		Assert.Equal("20261017-000000-42", id);
		await journal.StartSession(new(id, now, null, 42, now, "client", "1", AgentJournalMode.Live,
			[new(directory.Path, "Project")], AgentJournalToolSet.Reduced, "5.2", false, AgentJournalTotals.Empty, true),
			TestContext.Current.CancellationToken);
		Assert.Equal("{\"type\":\"session\",\"session\":{\"id\":\"20261017-000000-42\",\"startedUtc\":\"2026-10-17T00:00:00+00:00\",\"endedUtc\":null,\"pid\":42," +
			"\"processStartUtc\":\"2026-10-17T00:00:00+00:00\",\"clientName\":\"client\",\"clientVersion\":\"1\",\"mode\":\"Live\",\"roots\":[{\"configuredPath\":" +
			rootJson + ",\"name\":\"Project\"}],\"toolSet\":\"Reduced\",\"serverVersion\":\"5.2\",\"hidePrivateData\":false," +
			"\"totals\":{\"calls\":0,\"resultCharacters\":0,\"estimatedTokens\":0,\"filesDelivered\":0,\"secretsMasked\":0,\"privateDataMasked\":0,\"errors\":0},\"isLive\":false},\"call\":null,\"end\":null}\n",
			await File.ReadAllTextAsync(Path.Combine(journal.DirectoryPath, id + ".jsonl"), TestContext.Current.CancellationToken));
	}

	[Theory]
	[InlineData("XDG_CONFIG_HOME", ".config", "configuration")]
	[InlineData("XDG_DATA_HOME", ".local/share", "data")]
	[InlineData("XDG_STATE_HOME", ".local/state", "state")]
	[InlineData("XDG_CACHE_HOME", ".cache", "cache")]
	public void OrdinaryDataPathsDoNotAcquireAnApplicationSandbox(string variable, string suffix, string kind)
	{
		using var ordinary = FlatpakEnvironment.UseForTesting(new(false, null));
		using var directory = new TemporaryDirectory();
		var selectedKind = kind switch
		{
			"configuration" => UserDataDirectoryKind.Configuration,
			"data" => UserDataDirectoryKind.Data,
			"state" => UserDataDirectoryKind.State,
			_ => UserDataDirectoryKind.Cache
		};
		var expected = Path.Combine(directory.Path, suffix.Replace('/', Path.DirectorySeparatorChar));
		Assert.Equal(expected, UserDataPathResolver.Resolve(selectedKind, false,
			(_, _) => directory.Path, name => name == variable ? expected : name == "HOME" ? directory.Path : null));
	}

	private sealed class Clock(DateTimeOffset now) : TimeProvider
	{
		public override DateTimeOffset GetUtcNow() => now;
	}
}
