using DevProjex.Infrastructure.AgentJournal;

namespace DevProjex.Tests.Unit;

public sealed class AgentJournalStoreTests
{
	[Fact]
	public async Task SessionAndCallRoundTripThroughJsonLines()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		using var temporary = new TemporaryDirectory();
		using var store = CreateStore(temporary.Path);
		var session = CreateSession(temporary.Path, 42, new DateTimeOffset(2026, 9, 20, 1, 2, 3, TimeSpan.Zero));
		await store.StartSession(session, cancellationToken);
		await store.RecordCall(session.Id, CreateCall(1), cancellationToken);
		await store.EndSession(session.Id, session.StartedUtc.AddSeconds(2), new AgentJournalTotals(1, 120, 30, 1, 2, 3, 0), cancellationToken);

		var restored = Assert.Single(await store.ListSessionsAsync(cancellationToken: cancellationToken));
		var call = Assert.Single(await store.ReadCallsAsync(session.Id, cancellationToken));

		Assert.Equal(session.Id, restored.Id);
		Assert.Equal(AgentJournalMode.Live, restored.Mode);
		Assert.Equal(30, restored.Totals.EstimatedTokens);
		Assert.Equal("src/Program.cs", Assert.Single(call.DeliveredPaths));
		Assert.Equal("src/Program.cs", call.Arguments["path"]);
		Assert.DoesNotContain("ignored", call.Arguments.Keys);
		var jsonLines = await File.ReadAllTextAsync(Path.Combine(store.DirectoryPath, session.Id + ".jsonl"), cancellationToken);
		Assert.Contains("\"mode\":\"Live\"", jsonLines, StringComparison.Ordinal);
		Assert.EndsWith("\n", jsonLines, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ReaderIgnoresAnIncompleteLastLine()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		using var temporary = new TemporaryDirectory();
		using var store = CreateStore(temporary.Path);
		var session = CreateSession(temporary.Path, 43, new DateTimeOffset(2026, 9, 20, 1, 2, 4, TimeSpan.Zero));
		await store.StartSession(session, cancellationToken);
		await store.RecordCall(session.Id, CreateCall(1), cancellationToken);
		var path = Path.Combine(store.DirectoryPath, session.Id + ".jsonl");
		await File.AppendAllTextAsync(path, "{\"type\":\"call\",\"call\":", cancellationToken);

		var call = Assert.Single(await store.ReadCallsAsync(session.Id, cancellationToken));

		Assert.Equal(1, call.Sequence);
	}

	[Fact]
	public async Task RetentionKeepsTheNewestSessionsWithinTheConfiguredLimit()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		using var temporary = new TemporaryDirectory();
		using var store = CreateStore(
			temporary.Path,
			new AgentJournalRetentionPolicy(TimeSpan.FromDays(30), 2));
		var started = new DateTimeOffset(2026, 9, 20, 1, 2, 3, TimeSpan.Zero);
		for (var index = 0; index < 3; index++)
		{
			var session = CreateSession(temporary.Path, 50 + index, started.AddSeconds(index));
			await store.StartSession(session, cancellationToken);
			File.SetLastWriteTimeUtc(
				Path.Combine(store.DirectoryPath, session.Id + ".jsonl"),
				started.AddSeconds(index).UtcDateTime);
		}

		var sessions = await store.ListSessionsAsync(cancellationToken: cancellationToken);

		Assert.Equal(2, sessions.Count);
		Assert.DoesNotContain(sessions, session => session.Pid == 50);
	}

	[Fact]
	public async Task ClearCanRemoveOnlySessionsForTheRequestedProject()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		using var temporary = new TemporaryDirectory();
		var firstRoot = temporary.CreateFolder("first");
		var secondRoot = temporary.CreateFolder("second");
		using var store = CreateStore(temporary.Path);
		var first = CreateSession(firstRoot, 61, new DateTimeOffset(2026, 9, 20, 1, 2, 3, TimeSpan.Zero));
		var second = CreateSession(secondRoot, 62, new DateTimeOffset(2026, 9, 20, 1, 2, 4, TimeSpan.Zero));
		await store.StartSession(first, cancellationToken);
		await store.StartSession(second, cancellationToken);

		var removed = await store.ClearAsync(firstRoot, cancellationToken);

		Assert.Equal(1, removed);
		var remaining = Assert.Single(await store.ListSessionsAsync(cancellationToken: cancellationToken));
		Assert.Equal(second.Id, remaining.Id);
	}

	[Fact]
	public void ReceiptFormatterProducesStableMarkdownAndJsonContracts()
	{
		var started = new DateTimeOffset(2026, 9, 20, 1, 2, 3, TimeSpan.Zero);
		var session = CreateSession("C:\\work", 42, started) with
		{
			EndedUtc = started.AddSeconds(2),
			Totals = new AgentJournalTotals(1, 120, 30, 1, 2, 3, 0)
		};
		var receipt = new AgentJournalReceipt(
			session,
			session.Totals,
			[new AgentJournalDeliveredPath("src/Program.cs", 1)],
			[CreateCall(1)]);
		var formatter = new AgentJournalReceiptFormatter();

		var markdown = formatter.FormatMarkdown(receipt);
		var json = formatter.FormatJson(receipt);

		Assert.Contains("# DevProjex agent journal 20260920-010203-42", markdown, StringComparison.Ordinal);
		Assert.Contains("|1|120|30|1|2|3|0|", markdown, StringComparison.Ordinal);
		Assert.Contains("|src/Program.cs|1|", markdown, StringComparison.Ordinal);
		using var document = JsonDocument.Parse(json);
		Assert.Equal("devprojex-agent-journal", document.RootElement.GetProperty("schema").GetString());
		Assert.Equal(1, document.RootElement.GetProperty("version").GetInt32());
		Assert.Equal(30, document.RootElement.GetProperty("receipt").GetProperty("totals").GetProperty("estimatedTokens").GetInt64());
	}

	private static AgentJournalStore CreateStore(
		string stateRoot,
		AgentJournalRetentionPolicy? retention = null) =>
		new(
			() => stateRoot,
			activeSessionProvider: static () => [],
			retention: retention);

	private static AgentJournalSession CreateSession(
		string root,
		int pid,
		DateTimeOffset startedUtc) =>
		new(
			AgentJournalStore.CreateSessionId(startedUtc, pid),
			startedUtc,
			EndedUtc: null,
			pid,
			startedUtc.AddMinutes(-1),
			"sample-client",
			"1.2.3",
			AgentJournalMode.Live,
			[new AgentJournalRoot(Path.GetFullPath(root), "sample")],
			AgentJournalToolSet.Reduced,
			"5.2.0",
			HidePrivateData: true,
			AgentJournalTotals.Empty,
			IsLive: false);

	private static AgentJournalCall CreateCall(long sequence) =>
		new(
			sequence,
			new DateTimeOffset(2026, 9, 20, 1, 2, 4, TimeSpan.Zero),
			"get_file",
			RootIndex: 0,
			new Dictionary<string, string>(StringComparer.Ordinal)
			{
				["path"] = "src/Program.cs",
				["ignored"] = "must not be stored"
			},
			Revision: 2,
			DurationMs: 10,
			ResultCharacters: 120,
			EstimatedTokens: 30,
			FilesDelivered: 1,
			DeliveredPaths: ["src/Program.cs"],
			AdditionalDeliveredPaths: 0,
			SecretsMasked: 2,
			PrivateDataMasked: 3,
			Notices: [AgentJournalNoticeCodes.OutsideSelection],
			ErrorCode: null);
}
