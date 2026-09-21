using DevProjex.Application.Secrets;
using DevProjex.Mcp;
using ModelContextProtocol.Protocol;

namespace DevProjex.Tests.Unit;

public sealed class McpAgentJournalTests
{
	[Fact]
	public async Task CompletedCallIsQueuedWithoutChangingTheToolResult()
	{
		using var temporary = new TemporaryDirectory();
		var root = temporary.CreateFolder("project");
		var writer = new RecordingWriter();
		var registry = new McpRootRegistry([root]);
		await using var journal = new McpAgentJournal(
			writer,
			registry,
			AgentJournalMode.Live,
			AgentJournalToolSet.Reduced,
			"5.2.0",
			hidePrivateData: true,
			pid: 42,
			processStartUtc: new DateTimeOffset(2026, 9, 20, 1, 0, 0, TimeSpan.Zero));
		await journal.StartAsync("sample-client", "1.0", TestContext.Current.CancellationToken);
		var request = new CallToolRequestParams
		{
			Name = "get_file",
			Arguments = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
			{
				["path"] = JsonSerializer.SerializeToElement("src/Program.cs"),
				["pattern"] = JsonSerializer.SerializeToElement("Widget"),
				["project"] = JsonSerializer.SerializeToElement(root)
			}
		};
		var result = McpToolResults.TextSuccess(
			McpSpotlight.Wrap("Results are partial; additional observed matches not shown.\nDPX-MCP-INVALID-ARGUMENTS") +
			"\n[Live context] the named path is outside the current window selection; returned because you named it.");
		var original = JsonSerializer.SerializeToUtf8Bytes(result);

		using (journal.BeginCall("get_file", request))
		{
			journal.RecordDeliveredPaths(root, [Path.Combine(root, "src", "Program.cs")]);
			journal.RecordProtection(new SecretRedactionSnapshot(
				"selection",
				DetectedCount: 5,
				RedactedCount: 5,
				PrivateDataDetectedCount: 2,
				PrivateDataRedactedCount: 2));
			journal.Complete(result);
		}

		await journal.DisposeAsync();

		Assert.Equal(original, JsonSerializer.SerializeToUtf8Bytes(result));
		var session = Assert.Single(writer.Sessions);
		Assert.Equal("sample-client", session.ClientName);
		var call = Assert.Single(writer.Calls);
		Assert.Equal("get_file", call.Tool);
		Assert.Equal("src/Program.cs", call.Arguments["path"]);
		Assert.DoesNotContain("project", call.Arguments.Keys);
		Assert.Equal(3, call.SecretsMasked);
		Assert.Equal(2, call.PrivateDataMasked);
		Assert.Equal(AgentJournalNoticeCodes.OutsideSelection, Assert.Single(call.Notices));
		Assert.Equal("src/Program.cs", Assert.Single(call.DeliveredPaths));
		Assert.Equal(1, Assert.Single(writer.Ended).Totals.Calls);
	}

	[Fact]
	public async Task WriterFailureDoesNotEscapeIntoServerExecution()
	{
		using var temporary = new TemporaryDirectory();
		var root = temporary.CreateFolder("project");
		var writer = new ThrowingWriter();
		await using var journal = new McpAgentJournal(
			writer,
			new McpRootRegistry([root]),
			AgentJournalMode.Standard,
			AgentJournalToolSet.Full,
			"5.2.0",
			hidePrivateData: false,
			pid: 43,
			processStartUtc: new DateTimeOffset(2026, 9, 20, 1, 0, 0, TimeSpan.Zero));

		await journal.StartAsync("sample-client", "1.0", TestContext.Current.CancellationToken);
		using (journal.BeginCall("list_projects", new CallToolRequestParams { Name = "list_projects" }))
			journal.Complete(McpToolResults.TextSuccess("unchanged"));

		await journal.DisposeAsync();
	}

	[Fact]
	public async Task FailedSessionStartDoesNotAppendCallsOrAnEndRecord()
	{
		using var temporary = new TemporaryDirectory();
		var root = temporary.CreateFolder("project");
		var writer = new StartFailureWriter();
		var journal = new McpAgentJournal(
			writer,
			new McpRootRegistry([root]),
			AgentJournalMode.Standard,
			AgentJournalToolSet.Full,
			"5.2.0",
			hidePrivateData: false,
			pid: 44,
			processStartUtc: new DateTimeOffset(2026, 9, 20, 1, 0, 0, TimeSpan.Zero));

		await journal.StartAsync("sample-client", "1.0", TestContext.Current.CancellationToken);
		using (journal.BeginCall("list_projects", new CallToolRequestParams { Name = "list_projects" }))
			journal.Complete(McpToolResults.TextSuccess("unchanged"));
		await journal.DisposeAsync();

		Assert.Equal(0, writer.CallAttempts);
		Assert.Equal(0, writer.EndAttempts);
	}

	[Fact]
	public async Task CanceledSessionStartPropagatesCancellation()
	{
		using var temporary = new TemporaryDirectory();
		var root = temporary.CreateFolder("project");
		using var cancellation = new CancellationTokenSource();
		cancellation.Cancel();
		await using var journal = new McpAgentJournal(
			new CanceledStartWriter(),
			new McpRootRegistry([root]),
			AgentJournalMode.Standard,
			AgentJournalToolSet.Full,
			"5.2.0",
			hidePrivateData: false,
			pid: 45,
			processStartUtc: new DateTimeOffset(2026, 9, 20, 1, 0, 0, TimeSpan.Zero));

		await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
			await journal.StartAsync("sample-client", "1.0", cancellation.Token));
	}

	private sealed class RecordingWriter : IAgentJournalWriter
	{
		public List<AgentJournalSession> Sessions { get; } = [];
		public List<AgentJournalCall> Calls { get; } = [];
		public List<(DateTimeOffset EndedUtc, AgentJournalTotals Totals)> Ended { get; } = [];

		public ValueTask StartSession(AgentJournalSession session, CancellationToken cancellationToken = default)
		{
			Sessions.Add(session);
			return ValueTask.CompletedTask;
		}

		public ValueTask RecordCall(string sessionId, AgentJournalCall call, CancellationToken cancellationToken = default)
		{
			Calls.Add(call);
			return ValueTask.CompletedTask;
		}

		public ValueTask EndSession(
			string sessionId,
			DateTimeOffset endedUtc,
			AgentJournalTotals totals,
			CancellationToken cancellationToken = default)
		{
			Ended.Add((endedUtc, totals));
			return ValueTask.CompletedTask;
		}
	}

	private sealed class ThrowingWriter : IAgentJournalWriter
	{
		public ValueTask StartSession(AgentJournalSession session, CancellationToken cancellationToken = default) =>
			ValueTask.FromException(new IOException("unavailable"));

		public ValueTask RecordCall(string sessionId, AgentJournalCall call, CancellationToken cancellationToken = default) =>
			ValueTask.FromException(new IOException("unavailable"));

		public ValueTask EndSession(
			string sessionId,
			DateTimeOffset endedUtc,
			AgentJournalTotals totals,
			CancellationToken cancellationToken = default) =>
			ValueTask.FromException(new IOException("unavailable"));
	}

	private sealed class StartFailureWriter : IAgentJournalWriter
	{
		public int CallAttempts { get; private set; }
		public int EndAttempts { get; private set; }

		public ValueTask StartSession(AgentJournalSession session, CancellationToken cancellationToken = default) =>
			ValueTask.FromException(new IOException("unavailable"));

		public ValueTask RecordCall(string sessionId, AgentJournalCall call, CancellationToken cancellationToken = default)
		{
			CallAttempts++;
			return ValueTask.CompletedTask;
		}

		public ValueTask EndSession(
			string sessionId,
			DateTimeOffset endedUtc,
			AgentJournalTotals totals,
			CancellationToken cancellationToken = default)
		{
			EndAttempts++;
			return ValueTask.CompletedTask;
		}
	}

	private sealed class CanceledStartWriter : IAgentJournalWriter
	{
		public ValueTask StartSession(AgentJournalSession session, CancellationToken cancellationToken = default) =>
			ValueTask.FromCanceled(cancellationToken);

		public ValueTask RecordCall(string sessionId, AgentJournalCall call, CancellationToken cancellationToken = default) =>
			ValueTask.CompletedTask;

		public ValueTask EndSession(
			string sessionId,
			DateTimeOffset endedUtc,
			AgentJournalTotals totals,
			CancellationToken cancellationToken = default) =>
			ValueTask.CompletedTask;
	}
}
