using DevProjex.Application.Secrets;
using DevProjex.Infrastructure.AgentJournal;
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
			journal.RecordNotice(AgentJournalNoticeCodes.OutsideSelection);
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
	public async Task SearchTextAndSymbolValuesAreNotPersisted()
	{
		using var temporary = new TemporaryDirectory();
		var root = temporary.CreateFolder("project");
		var writer = new RecordingWriter();
		await using var journal = new McpAgentJournal(
			writer,
			new McpRootRegistry([root]),
			AgentJournalMode.Live,
			AgentJournalToolSet.Reduced,
			"5.2.0",
			hidePrivateData: true,
			pid: 44,
			processStartUtc: new DateTimeOffset(2026, 9, 20, 1, 0, 0, TimeSpan.Zero));
		await journal.StartAsync("sample-client", "1.0", TestContext.Current.CancellationToken);
		const string searchText = "find token ghp_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA and person@example.com";
		const string symbolText = "person@example.com";
		var request = new CallToolRequestParams
		{
			Name = "search_project",
			Arguments = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
			{
				["pattern"] = JsonSerializer.SerializeToElement(searchText),
				["mode"] = JsonSerializer.SerializeToElement("regex"),
				["symbol"] = JsonSerializer.SerializeToElement(symbolText)
			}
		};

		using (journal.BeginCall("search_project", request))
			journal.Complete(McpToolResults.TextSuccess("no matches"));
		await journal.DisposeAsync();

		var call = Assert.Single(writer.Calls);
		Assert.DoesNotContain(searchText, call.Arguments.Values);
		Assert.DoesNotContain(symbolText, call.Arguments.Values);
		Assert.Equal(searchText.Length.ToString(CultureInfo.InvariantCulture), call.Arguments["query_length"]);
		Assert.Equal("true", call.Arguments["query_present"]);
		Assert.Equal("regex", call.Arguments["mode"]);
		Assert.Equal(symbolText.Length.ToString(CultureInfo.InvariantCulture), call.Arguments["symbol_length"]);
		Assert.Equal("expression", call.Arguments["symbol_class"]);
	}

	[Fact]
	public async Task SearchValuesDoNotReachJsonLinesOrReceiptFormats()
	{
		using var temporary = new TemporaryDirectory();
		var root = temporary.CreateFolder("project");
		using var store = new AgentJournalStore(
			() => temporary.Path,
			activeSessionProvider: static () => []);
		await using var journal = new McpAgentJournal(
			store,
			new McpRootRegistry([root]),
			AgentJournalMode.Standard,
			AgentJournalToolSet.Full,
			"5.2.0",
			hidePrivateData: true,
			pid: 144,
			processStartUtc: new DateTimeOffset(2026, 9, 20, 1, 0, 0, TimeSpan.Zero));
		await journal.StartAsync("sample-client", "1.0", TestContext.Current.CancellationToken);
		const string token = "ghp_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
		const string email = "alice.smith@company.io";
		var request = new CallToolRequestParams
		{
			Name = "search_project",
			Arguments = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
			{
				["pattern"] = JsonSerializer.SerializeToElement($"find {token} owned by {email}"),
				["symbol"] = JsonSerializer.SerializeToElement(email),
				["path"] = JsonSerializer.SerializeToElement(email)
			}
		};
		using (journal.BeginCall("search_project", request))
			journal.Complete(McpToolResults.TextSuccess("no matches"));
		await journal.DisposeAsync();

		var session = Assert.Single(await store.ListSessionsAsync(
			cancellationToken: TestContext.Current.CancellationToken));
		var receipt = Assert.IsType<AgentJournalReceipt>(await store.ReadReceiptAsync(
			session.Id,
			TestContext.Current.CancellationToken));
		var formatter = new AgentJournalReceiptFormatter();
		var persisted = await File.ReadAllTextAsync(
			Path.Combine(store.DirectoryPath, session.Id + ".jsonl"),
			TestContext.Current.CancellationToken);
		var formats = new[] { persisted, formatter.FormatJson(receipt), formatter.FormatMarkdown(receipt) };
		Assert.All(formats, value =>
		{
			Assert.DoesNotContain(token, value, StringComparison.Ordinal);
			Assert.DoesNotContain(email, value, StringComparison.Ordinal);
		});
	}

	[Fact]
	public async Task TransientCallWriteIsRetriedBeforeTotalsAreFinalized()
	{
		using var temporary = new TemporaryDirectory();
		var writer = new FailsFirstCallWriter();
		await using var journal = new McpAgentJournal(
			writer,
			new McpRootRegistry([temporary.CreateFolder("project")]),
			AgentJournalMode.Standard,
			AgentJournalToolSet.Full,
			"5.2.0",
			hidePrivateData: false,
			pid: 45,
			processStartUtc: new DateTimeOffset(2026, 9, 20, 1, 0, 0, TimeSpan.Zero));
		await journal.StartAsync("sample-client", "1.0", TestContext.Current.CancellationToken);
		using (journal.BeginCall("list_projects", new CallToolRequestParams { Name = "list_projects" }))
			journal.Complete(McpToolResults.TextSuccess("ok"));

		await journal.DisposeAsync();

		Assert.Equal(2, writer.RecordAttempts);
		Assert.Single(writer.Calls);
		Assert.Equal(1, Assert.Single(writer.Ended).Totals.Calls);
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
			pid: 145,
			processStartUtc: new DateTimeOffset(2026, 9, 20, 1, 0, 0, TimeSpan.Zero));

		await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
			await journal.StartAsync("sample-client", "1.0", cancellation.Token));
	}

	[Fact]
	public async Task NoticesAreRecordedFromExplicitStateRatherThanResponseText()
	{
		using var temporary = new TemporaryDirectory();
		var writer = new RecordingWriter();
		await using var journal = new McpAgentJournal(
			writer,
			new McpRootRegistry([temporary.CreateFolder("project")]),
			AgentJournalMode.Live,
			AgentJournalToolSet.Full,
			"5.2.0",
			hidePrivateData: false,
			pid: 46,
			processStartUtc: new DateTimeOffset(2026, 9, 20, 1, 0, 0, TimeSpan.Zero));
		await journal.StartAsync("sample-client", "1.0", TestContext.Current.CancellationToken);
		using (journal.BeginCall("pack_context", new CallToolRequestParams { Name = "pack_context" }))
		{
			journal.Complete(McpToolResults.TextSuccess("pack built at revision 1"));
		}
		using (journal.BeginCall("read_pack", new CallToolRequestParams { Name = "read_pack" }))
		{
			journal.RecordNotice(AgentJournalNoticeCodes.StalePack);
			journal.Complete(McpToolResults.TextSuccess("ordinary response"));
		}
		await journal.DisposeAsync();

		Assert.Empty(writer.Calls[0].Notices);
		Assert.Equal(AgentJournalNoticeCodes.StalePack, Assert.Single(writer.Calls[1].Notices));
	}

	[Fact]
	public async Task ProtectionCountsOnlyFindingsInsideTheReturnedRange()
	{
		using var temporary = new TemporaryDirectory();
		var writer = new RecordingWriter();
		await using var journal = new McpAgentJournal(
			writer,
			new McpRootRegistry([temporary.CreateFolder("project")]),
			AgentJournalMode.Standard,
			AgentJournalToolSet.Full,
			"5.2.0",
			hidePrivateData: true,
			pid: 47,
			processStartUtc: new DateTimeOffset(2026, 9, 20, 1, 0, 0, TimeSpan.Zero));
		await journal.StartAsync("sample-client", "1.0", TestContext.Current.CancellationToken);
		using (journal.BeginCall("get_file", new CallToolRequestParams { Name = "get_file" }))
		{
			journal.RecordProtection(
			[
				new EffectiveRedactionFinding("secret", RedactionFindingCategory.Secrets, "src/App.cs", 100),
				new EffectiveRedactionFinding("email", RedactionFindingCategory.PrivateData, "src/App.cs", 2)
			],
				startLine: 1,
				endLine: 3);
			journal.Complete(McpToolResults.TextSuccess("page"));
		}
		await journal.DisposeAsync();

		var call = Assert.Single(writer.Calls);
		Assert.Equal(0, call.SecretsMasked);
		Assert.Equal(1, call.PrivateDataMasked);
	}

	[Fact]
	public async Task PermanentlyLostCallProducesIncompleteHistoryMarkerAndSuccessfulTotalsOnly()
	{
		using var temporary = new TemporaryDirectory();
		var writer = new RejectsToolCallWriter();
		await using var journal = new McpAgentJournal(
			writer,
			new McpRootRegistry([temporary.CreateFolder("project")]),
			AgentJournalMode.Standard,
			AgentJournalToolSet.Full,
			"5.2.0",
			hidePrivateData: false,
			pid: 48,
			processStartUtc: new DateTimeOffset(2026, 9, 20, 1, 0, 0, TimeSpan.Zero));
		await journal.StartAsync("sample-client", "1.0", TestContext.Current.CancellationToken);
		using (journal.BeginCall("list_projects", new CallToolRequestParams { Name = "list_projects" }))
			journal.Complete(McpToolResults.TextSuccess("ok"));
		await journal.DisposeAsync();

		var marker = Assert.Single(writer.Calls);
		Assert.Equal("journal", marker.Tool);
		Assert.Contains("history-incomplete", marker.Notices);
		Assert.Equal("1", marker.Arguments["lost_events"]);
		Assert.Equal(0, Assert.Single(writer.Ended).Totals.Calls);
		var session = Assert.Single(writer.Sessions) with
		{
			EndedUtc = writer.Ended[0].EndedUtc,
			Totals = writer.Ended[0].Totals
		};
		var receipt = new AgentJournalReceipt(session, session.Totals, [], writer.Calls);
		Assert.Contains("History is incomplete: 1 event", new AgentJournalReceiptFormatter().FormatMarkdown(receipt));
	}

	private class RecordingWriter : IAgentJournalWriter
	{
		public List<AgentJournalSession> Sessions { get; } = [];
		public List<AgentJournalCall> Calls { get; } = [];
		public List<(DateTimeOffset EndedUtc, AgentJournalTotals Totals)> Ended { get; } = [];

		public ValueTask StartSession(AgentJournalSession session, CancellationToken cancellationToken = default)
		{
			Sessions.Add(session);
			return ValueTask.CompletedTask;
		}

		public virtual ValueTask RecordCall(string sessionId, AgentJournalCall call, CancellationToken cancellationToken = default)
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

	private sealed class FailsFirstCallWriter : RecordingWriter
	{
		public int RecordAttempts { get; private set; }

		public override ValueTask RecordCall(
			string sessionId,
			AgentJournalCall call,
			CancellationToken cancellationToken = default)
		{
			RecordAttempts++;
			return RecordAttempts == 1
				? ValueTask.FromException(new IOException("temporarily unavailable"))
				: base.RecordCall(sessionId, call, cancellationToken);
		}
	}

	private sealed class RejectsToolCallWriter : RecordingWriter
	{
		public override ValueTask RecordCall(
			string sessionId,
			AgentJournalCall call,
			CancellationToken cancellationToken = default) => call.Tool == "journal"
			? base.RecordCall(sessionId, call, cancellationToken)
			: ValueTask.FromException(new IOException("unavailable"));
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
