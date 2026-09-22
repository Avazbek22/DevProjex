using System.Diagnostics;
using DevProjex.Infrastructure.AgentJournal;
using DevProjex.Infrastructure.LiveContext;

namespace DevProjex.Tests.Terminal;

public sealed class AgentJournalCommandProcessTests
{
	[Theory]
	[InlineData("text", "get_file")]
	[InlineData("json", "\"schema\": \"devprojex-agent-journal\"")]
	[InlineData("markdown", "# DevProjex agent journal")]
	public async Task RealCliPrintsOneSessionInEveryPublishedFormat(string format, string expected)
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		var dataRoot = workspace.CreateDirectory("data");
		await SeedAsync(dataRoot, project);

		var result = Run(dataRoot, "mcp", "log", project, "--last", "--format", format);

		Assert.Equal(CommandLineExitCodes.Success, result.ExitCode);
		Assert.Contains(expected, result.StandardOutput, StringComparison.Ordinal);
		Assert.Empty(result.StandardError);
	}

	[Fact]
	public async Task SessionListAndReceiptUseTheSameEnumSpelling()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		var dataRoot = workspace.CreateDirectory("data");
		await SeedAsync(dataRoot, project);

		var list = Run(dataRoot, "mcp", "log", project, "--format", "json");
		var receipt = Run(dataRoot, "mcp", "log", project, "--last", "--format", "json");

		Assert.Equal(CommandLineExitCodes.Success, list.ExitCode);
		Assert.Equal(CommandLineExitCodes.Success, receipt.ExitCode);
		using var listDocument = JsonDocument.Parse(list.StandardOutput);
		using var receiptDocument = JsonDocument.Parse(receipt.StandardOutput);
		var listedSession = listDocument.RootElement.GetProperty("sessions")[0];
		var receiptSession = receiptDocument.RootElement.GetProperty("receipt").GetProperty("session");
		Assert.Equal("standard", listedSession.GetProperty("mode").GetString());
		Assert.Equal(
			listedSession.GetProperty("mode").GetString(),
			receiptSession.GetProperty("mode").GetString());
		Assert.Equal(
			listedSession.GetProperty("toolSet").GetString(),
			receiptSession.GetProperty("toolSet").GetString());
	}

	[Fact]
	public async Task RealCliRequiresConfirmationBeforeClearingMatchingSessions()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		var dataRoot = workspace.CreateDirectory("data");
		await SeedAsync(dataRoot, project);

		var refused = Run(dataRoot, "mcp", "log", project, "--clear");
		var cleared = Run(dataRoot, "mcp", "log", project, "--clear", "--yes");
		var empty = Run(dataRoot, "mcp", "log", project);

		Assert.Equal(CommandLineExitCodes.UsageError, refused.ExitCode);
		Assert.Contains("DPX-CLI-INVALID-SYNTAX", refused.StandardError, StringComparison.Ordinal);
		Assert.Equal(CommandLineExitCodes.Success, cleared.ExitCode);
		Assert.Contains("Cleared 1 completed agent journal session", cleared.StandardOutput, StringComparison.Ordinal);
		Assert.Contains("active sessions were preserved", cleared.StandardOutput, StringComparison.Ordinal);
		Assert.Equal(CommandLineExitCodes.Success, empty.ExitCode);
		Assert.Contains("No agent journal sessions found", empty.StandardOutput, StringComparison.Ordinal);
	}

	[Fact]
	public async Task RealCliClearPreservesAnActiveSession()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		var dataRoot = workspace.CreateDirectory("data");
		var registry = new LiveSessionRegistry(() => dataRoot);
		await using var live = registry.Start([project], AgentJournalMode.Standard);
		var active = Assert.Single(registry.ReadActive(project));
		using (var store = new AgentJournalStore(() => dataRoot))
		{
			await store.StartSession(
				new AgentJournalSession(
					AgentJournalStore.CreateSessionId(DateTimeOffset.UtcNow, active.Pid),
					DateTimeOffset.UtcNow,
					EndedUtc: null,
					active.Pid,
					active.ProcessStartUtc,
					"sample-client",
					"1.0",
					AgentJournalMode.Standard,
					[new AgentJournalRoot(project, "project")],
					AgentJournalToolSet.Full,
					"5.2.0",
					HidePrivateData: false,
					AgentJournalTotals.Empty,
					IsLive: true),
				TestContext.Current.CancellationToken);
		}

		var cleared = Run(dataRoot, "mcp", "log", project, "--clear", "--yes");

		Assert.Equal(CommandLineExitCodes.Success, cleared.ExitCode);
		Assert.Contains("Cleared 0 completed agent journal session", cleared.StandardOutput, StringComparison.Ordinal);
		Assert.Contains("active sessions were preserved", cleared.StandardOutput, StringComparison.Ordinal);
		using var reader = new AgentJournalStore(() => dataRoot);
		Assert.Single(await reader.ListSessionsAsync(project, cancellationToken: TestContext.Current.CancellationToken));
	}

	[Fact]
	public async Task RealCliShowsADeadSessionDurationAsALastEventLowerBound()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		var dataRoot = workspace.CreateDirectory("data");
		var started = new DateTimeOffset(2026, 9, 20, 1, 2, 3, TimeSpan.Zero);
		using (var store = new AgentJournalStore(() => dataRoot, activeSessionProvider: static () => []))
		{
			var session = new AgentJournalSession(
				AgentJournalStore.CreateSessionId(started, 84), started, null, 84, started.AddMinutes(-1),
				"sample-client", "1.0", AgentJournalMode.Standard,
				[new AgentJournalRoot(project, "project")], AgentJournalToolSet.Full, "5.2.0", false,
				AgentJournalTotals.Empty, false);
			await store.StartSession(session, TestContext.Current.CancellationToken);
			await store.RecordCall(
				session.Id,
				new AgentJournalCall(1, started.AddSeconds(7), "get_tree", 0,
					new Dictionary<string, string>(), null, 1, 10, 3, 0, [], 0, 0, 0, [], null),
				TestContext.Current.CancellationToken);
		}

		var result = Run(dataRoot, "mcp", "log", project);

		Assert.Equal(CommandLineExitCodes.Success, result.ExitCode);
		Assert.Contains("≥0:07", result.StandardOutput, StringComparison.Ordinal);
		Assert.DoesNotContain("running", result.StandardOutput, StringComparison.OrdinalIgnoreCase);
	}

	private static async Task SeedAsync(string dataRoot, string project)
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		using var store = new AgentJournalStore(
			() => dataRoot,
			activeSessionProvider: static () => []);
		var started = new DateTimeOffset(2026, 9, 20, 1, 2, 3, TimeSpan.Zero);
		var session = new AgentJournalSession(
			AgentJournalStore.CreateSessionId(started, 42),
			started,
			EndedUtc: null,
			Pid: 42,
			ProcessStartUtc: started.AddMinutes(-1),
			ClientName: "sample-client",
			ClientVersion: "1.0",
			AgentJournalMode.Standard,
			[new AgentJournalRoot(project, "project")],
			AgentJournalToolSet.Full,
			ServerVersion: "5.2.0",
			HidePrivateData: false,
			AgentJournalTotals.Empty,
			IsLive: false);
		var call = new AgentJournalCall(
			Sequence: 1,
			Utc: started.AddSeconds(1),
			Tool: "get_file",
			RootIndex: 0,
			new Dictionary<string, string>(StringComparer.Ordinal) { ["path"] = "Program.cs" },
			Revision: null,
			DurationMs: 4,
			ResultCharacters: 40,
			EstimatedTokens: 10,
			FilesDelivered: 1,
			DeliveredPaths: ["Program.cs"],
			AdditionalDeliveredPaths: 0,
			SecretsMasked: 1,
			PrivateDataMasked: 0,
			Notices: [],
			ErrorCode: null);
		await store.StartSession(session, cancellationToken);
		await store.RecordCall(session.Id, call, cancellationToken);
		await store.EndSession(
			session.Id,
			started.AddSeconds(2),
			new AgentJournalTotals(1, 40, 10, 1, 1, 0, 0),
			cancellationToken);
	}

	private static TerminalTestProcessResult Run(string dataRoot, params string[] arguments)
	{
		var startInfo = new ProcessStartInfo("dotnet")
		{
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true
		};
		startInfo.ArgumentList.Add(PublishedApplicationLocator.FindApplicationAssembly());
		startInfo.ArgumentList.Add("--language");
		startInfo.ArgumentList.Add("en");
		foreach (var argument in arguments)
			startInfo.ArgumentList.Add(argument);
		startInfo.Environment[InvocationEnvironment.TerminalHostVariable] = "1";
		startInfo.Environment[InvocationEnvironment.InternalDataRootVariable] = dataRoot;
		return TerminalTestProcess.Run(startInfo);
	}
}
