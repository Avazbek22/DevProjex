using DevProjex.Infrastructure.AgentJournal;
using DevProjex.Infrastructure.LiveContext;
using System.Text.RegularExpressions;

namespace DevProjex.Tests.Unit;

public sealed class AgentJournalStoreTests(ITestOutputHelper output)
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
		Assert.Equal(AgentJournalNoticeCodes.OutsideSelection, Assert.Single(call.Notices));
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
	public async Task WriterRepairsAnIncompleteLastLineBeforeAppendingTheNextCall()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		using var temporary = new TemporaryDirectory();
		using var store = CreateStore(temporary.Path);
		var session = CreateSession(
			temporary.Path,
			44,
			new DateTimeOffset(2026, 9, 20, 1, 2, 5, TimeSpan.Zero));
		await store.StartSession(session, cancellationToken);
		await store.RecordCall(session.Id, CreateCall(1), cancellationToken);
		var path = Path.Combine(store.DirectoryPath, session.Id + ".jsonl");
		await File.AppendAllTextAsync(path, "{\"type\":\"call\",\"call\":", cancellationToken);

		await store.RecordCall(session.Id, CreateCall(2), cancellationToken);

		var calls = await store.ReadCallsAsync(session.Id, cancellationToken);
		Assert.Equal([1L, 2L], calls.Select(static call => call.Sequence));
		Assert.Contains("history-recovered", calls[1].Notices);
		Assert.EndsWith("\n", await File.ReadAllTextAsync(path, cancellationToken), StringComparison.Ordinal);
	}

	[Fact]
	public async Task WriterPreservesACompleteRecordThatOnlyLostItsTrailingNewline()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		using var temporary = new TemporaryDirectory();
		using var store = CreateStore(temporary.Path);
		var session = CreateSession(
			temporary.Path,
			46,
			new DateTimeOffset(2026, 9, 20, 1, 2, 7, TimeSpan.Zero));
		await store.StartSession(session, cancellationToken);
		await store.RecordCall(session.Id, CreateCall(1), cancellationToken);
		var path = Path.Combine(store.DirectoryPath, session.Id + ".jsonl");
		await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read))
			stream.SetLength(stream.Length - 1);

		await store.RecordCall(session.Id, CreateCall(2), cancellationToken);

		var calls = await store.ReadCallsAsync(session.Id, cancellationToken);
		Assert.Equal([1L, 2L], calls.Select(static call => call.Sequence));
		Assert.DoesNotContain("history-recovered", calls[1].Notices);
	}

	[Fact]
	public async Task RecoveredTotalsIncludeARecordedCallThatReportsEarlierLostEvents()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		using var temporary = new TemporaryDirectory();
		using var store = CreateStore(temporary.Path);
		var session = CreateSession(
			temporary.Path,
			45,
			new DateTimeOffset(2026, 9, 20, 1, 2, 6, TimeSpan.Zero));
		await store.StartSession(session, cancellationToken);
		await store.RecordCall(
			session.Id,
			CreateCall(2) with
			{
				Arguments = new Dictionary<string, string>(StringComparer.Ordinal)
				{
					["lost_events"] = "1"
				},
				Notices = ["history-incomplete"]
			},
			cancellationToken);

		var restored = Assert.Single(await store.ListSessionsAsync(cancellationToken: cancellationToken));

		Assert.Equal(1, restored.Totals.Calls);
		Assert.Equal(120, restored.Totals.ResultCharacters);
		Assert.Equal(30, restored.Totals.EstimatedTokens);
		Assert.Equal(1, restored.Totals.FilesDelivered);
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
	public async Task RetentionKeepsAnActiveSessionWhenCompletedSessionsReachTheLimit()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		using var temporary = new TemporaryDirectory();
		var started = new DateTimeOffset(2026, 9, 20, 1, 2, 3, TimeSpan.Zero);
		var active = CreateSession(temporary.Path, 50, started);
		using var store = CreateStore(
			temporary.Path,
			new AgentJournalRetentionPolicy(TimeSpan.FromDays(30), 2),
			() => [CreateActiveRecord(active)]);
		foreach (var session in new[]
				 {
					 active,
					 CreateSession(temporary.Path, 51, started.AddSeconds(1)),
					 CreateSession(temporary.Path, 52, started.AddSeconds(2))
				 })
		{
			await store.StartSession(session, cancellationToken);
			File.SetLastWriteTimeUtc(
				Path.Combine(store.DirectoryPath, session.Id + ".jsonl"),
				session.StartedUtc.UtcDateTime);
		}

		var sessions = await store.ListSessionsAsync(cancellationToken: cancellationToken);

		Assert.Contains(sessions, session => session.Id == active.Id);
		Assert.True(File.Exists(Path.Combine(store.DirectoryPath, active.Id + ".jsonl")));
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
	public async Task ClearPreservesActiveSessionsAndRemovesCompletedSessions()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		using var temporary = new TemporaryDirectory();
		var started = new DateTimeOffset(2026, 9, 20, 1, 2, 3, TimeSpan.Zero);
		var active = CreateSession(temporary.Path, 71, started);
		var completed = CreateSession(temporary.Path, 72, started.AddSeconds(1));
		using var store = new AgentJournalStore(
			() => temporary.Path,
			activeSessionProvider: () =>
			[
				new LiveSessionRecord(
					active.Pid,
					active.ProcessStartUtc,
					"sample-client",
					"1.0",
					[temporary.Path],
					started.AddSeconds(2),
					AgentJournalMode.Standard)
			]);
		await store.StartSession(active, cancellationToken);
		await store.StartSession(completed, cancellationToken);
		await store.EndSession(completed.Id, started.AddMinutes(1), AgentJournalTotals.Empty, cancellationToken);

		var removed = await store.ClearAsync(temporary.Path, cancellationToken);

		Assert.Equal(1, removed);
		var remaining = Assert.Single(await store.ListSessionsAsync(cancellationToken: cancellationToken));
		Assert.Equal(active.Id, remaining.Id);
		Assert.True(remaining.IsLive);
	}

	[Fact]
	public async Task RetentionNeverDeletesAnActiveSession()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		using var temporary = new TemporaryDirectory();
		var now = new DateTimeOffset(2026, 9, 20, 2, 0, 0, TimeSpan.Zero);
		var active = CreateSession(temporary.Path, 73, now.AddDays(-10));
		using var store = new AgentJournalStore(
			() => temporary.Path,
			new FixedTimeProvider(now),
			() =>
			[
				new LiveSessionRecord(
					active.Pid,
					active.ProcessStartUtc,
					"sample-client",
					"1.0",
					[temporary.Path],
					now,
					AgentJournalMode.Live)
			],
			new AgentJournalRetentionPolicy(TimeSpan.FromDays(1), 1));
		await store.StartSession(active, cancellationToken);
		var path = Path.Combine(store.DirectoryPath, active.Id + ".jsonl");
		File.SetLastWriteTimeUtc(path, now.AddDays(-10).UtcDateTime);

		var sessions = await store.ListSessionsAsync(cancellationToken: cancellationToken);

		Assert.Equal(active.Id, Assert.Single(sessions).Id);
	}

	[Fact]
	public async Task RetentionDeletesOnlyVerifiedSessionFiles()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		using var temporary = new TemporaryDirectory();
		var now = new DateTimeOffset(2026, 9, 20, 2, 0, 0, TimeSpan.Zero);
		var session = CreateSession(temporary.Path, 81, now.AddDays(-10));
		using var store = new AgentJournalStore(
			() => temporary.Path,
			new FixedTimeProvider(now),
			static () => [],
			new AgentJournalRetentionPolicy(TimeSpan.FromDays(1), 1));
		await store.StartSession(session, cancellationToken);
		var sessionPath = Path.Combine(store.DirectoryPath, session.Id + ".jsonl");
		File.SetLastWriteTimeUtc(sessionPath, now.AddDays(-10).UtcDateTime);
		var foreign = Path.Combine(store.DirectoryPath, "foreign.jsonl");
		await File.WriteAllTextAsync(foreign, "not a journal", cancellationToken);
		File.SetLastWriteTimeUtc(foreign, now.AddDays(-10).UtcDateTime);
		var mismatched = Path.Combine(store.DirectoryPath, "copied.jsonl");
		File.Copy(sessionPath, mismatched);
		File.SetLastWriteTimeUtc(mismatched, now.AddDays(-10).UtcDateTime);
		var forgedId = AgentJournalStore.CreateSessionId(session.StartedUtc, 82);
		var forged = Path.Combine(store.DirectoryPath, forgedId + ".jsonl");
		var forgedContents = (await File.ReadAllTextAsync(sessionPath, cancellationToken))
			.Replace(session.Id, forgedId, StringComparison.Ordinal);
		await File.WriteAllTextAsync(forged, forgedContents, cancellationToken);
		File.SetLastWriteTimeUtc(forged, now.AddDays(-10).UtcDateTime);

		await store.ListSessionsAsync(cancellationToken: cancellationToken);

		Assert.False(File.Exists(sessionPath));
		Assert.True(File.Exists(foreign));
		Assert.True(File.Exists(mismatched));
		Assert.True(File.Exists(forged));
	}

	[Fact]
	public void JournalStorageRejectsASymbolicLinkServiceDirectory()
	{
		using var temporary = new TemporaryDirectory();
		using var outside = new TemporaryDirectory();
		var link = Path.Combine(temporary.Path, "agent-journal");
		try
		{
			Directory.CreateSymbolicLink(link, outside.Path);
		}
		catch (Exception linkException) when (linkException is IOException or UnauthorizedAccessException)
		{
			Assert.Skip("Creating directory symbolic links is unavailable in this environment.");
			return;
		}

		var exception = Assert.Throws<IOException>(() => CreateStore(temporary.Path));

		Assert.Contains("symbolic link or junction", exception.Message, StringComparison.Ordinal);
		Assert.Empty(Directory.EnumerateFileSystemEntries(outside.Path));
	}

	[Fact]
	public async Task WriterRecreatesADeletedActiveSessionBeforeAppending()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		using var temporary = new TemporaryDirectory();
		using var store = CreateStore(temporary.Path);
		var session = CreateSession(temporary.Path, 74, new DateTimeOffset(2026, 9, 20, 1, 2, 3, TimeSpan.Zero));
		await store.StartSession(session, cancellationToken);
		File.Delete(Path.Combine(store.DirectoryPath, session.Id + ".jsonl"));

		await store.RecordCall(session.Id, CreateCall(1), cancellationToken);

		var restored = Assert.Single(await store.ListSessionsAsync(cancellationToken: cancellationToken));
		Assert.Equal(session.Id, restored.Id);
		var call = Assert.Single(await store.ReadCallsAsync(session.Id, cancellationToken));
		Assert.Contains("history-recovered", call.Notices);
	}

	[Fact]
	public async Task RecreatedSessionMarksHistoryIncompleteAndTotalsOnlyPersistedCalls()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		using var temporary = new TemporaryDirectory();
		using var store = CreateStore(temporary.Path);
		var session = CreateSession(temporary.Path, 78, new DateTimeOffset(2026, 9, 20, 1, 2, 3, TimeSpan.Zero));
		await store.StartSession(session, cancellationToken);
		await store.RecordCall(session.Id, CreateCall(1), cancellationToken);
		File.Delete(Path.Combine(store.DirectoryPath, session.Id + ".jsonl"));

		await store.RecordCall(session.Id, CreateCall(2), cancellationToken);
		await store.EndSession(
			session.Id,
			session.StartedUtc.AddMinutes(1),
			new AgentJournalTotals(2, 240, 60, 2, 4, 6, 0),
			cancellationToken);

		var restored = Assert.Single(await store.ListSessionsAsync(cancellationToken: cancellationToken));
		var call = Assert.Single(await store.ReadCallsAsync(session.Id, cancellationToken));
		Assert.Equal(1, restored.Totals.Calls);
		Assert.Equal(120, restored.Totals.ResultCharacters);
		Assert.Contains("history-recovered", call.Notices);
		Assert.Contains("history-incomplete", call.Notices);
		Assert.True(call.Arguments.ContainsKey("lost_events_unknown"));
	}

	[Fact(Timeout = 5_000)]
	public async Task WatcherResetsItsTailWhenTheSessionFileIsRecreated()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		using var temporary = new TemporaryDirectory();
		using var store = CreateStore(temporary.Path);
		var session = CreateSession(
			temporary.Path,
			76,
			new DateTimeOffset(2026, 9, 20, 1, 2, 3, TimeSpan.Zero));
		await store.StartSession(session, cancellationToken);
		await store.RecordCall(session.Id, CreateCall(1), cancellationToken);
		await using var changes = store.WatchChangesAsync(session.Id, cancellationToken)
			.GetAsyncEnumerator(cancellationToken);

		Assert.True(await changes.MoveNextAsync());
		Assert.Equal(1, changes.Current.Sequence);
		File.Delete(Path.Combine(store.DirectoryPath, session.Id + ".jsonl"));
		await store.RecordCall(
			session.Id,
			CreateCall(1) with
			{
				Arguments = new Dictionary<string, string>(StringComparer.Ordinal)
				{
					["path"] = new string('a', 4_000)
				}
			},
			cancellationToken);

		Assert.True(await changes.MoveNextAsync());
		Assert.Equal(1, changes.Current.Sequence);
		var recovered = Assert.Single(await store.ReadCallsAsync(session.Id, cancellationToken));
		Assert.Contains("history-recovered", recovered.Notices);
	}

	[Fact(Timeout = 20_000)]
	public async Task WatcherReadsOnlyTheAppendedTailOfALongSession()
	{
		const int callCount = 5_000;
		var cancellationToken = TestContext.Current.CancellationToken;
		using var temporary = new TemporaryDirectory();
		using var store = CreateStore(temporary.Path);
		var session = CreateSession(
			temporary.Path,
			77,
			new DateTimeOffset(2026, 9, 20, 1, 2, 3, TimeSpan.Zero));
		await store.StartSession(session, cancellationToken);
		await store.RecordCall(session.Id, CreateCall(1), cancellationToken);
		var path = Path.Combine(store.DirectoryPath, session.Id + ".jsonl");
		var seedLines = await File.ReadAllLinesAsync(path, cancellationToken);
		var builder = new StringBuilder(seedLines[0].Length + seedLines[1].Length * callCount);
		builder.AppendLine(seedLines[0]);
		for (var sequence = 1; sequence <= callCount; sequence++)
		{
			builder.AppendLine(seedLines[1].Replace(
				"\"sequence\":1,",
				$"\"sequence\":{sequence.ToString(CultureInfo.InvariantCulture)},",
				StringComparison.Ordinal));
		}
		await File.WriteAllTextAsync(path, builder.ToString(), cancellationToken);

		long observedBytes = 0;
		store.TailBytesReadObserver = bytes => Interlocked.Add(ref observedBytes, bytes);
		await using var changes = store.WatchChangesAsync(session.Id, cancellationToken)
			.GetAsyncEnumerator(cancellationToken);
		for (var sequence = 1; sequence <= callCount; sequence++)
		{
			Assert.True(await changes.MoveNextAsync());
			Assert.Equal(sequence, changes.Current.Sequence);
		}
		var bytesBeforeAppend = Volatile.Read(ref observedBytes);
		await store.RecordCall(session.Id, CreateCall(callCount + 1), cancellationToken);
		var previousImplementationBytes = new FileInfo(path).Length;

		Assert.True(await changes.MoveNextAsync());
		Assert.Equal(callCount + 1, changes.Current.Sequence);
		var tailBytes = Volatile.Read(ref observedBytes) - bytesBeforeAppend;

		output.WriteLine(
			$"5,000-event watcher bytes: before={previousImplementationBytes:N0}; after={tailBytes:N0}");
		Assert.InRange(tailBytes, 1, seedLines[1].Length * 2L);
		Assert.True(tailBytes * 100 < previousImplementationBytes);
	}

	[Fact(Timeout = 20_000)]
	public async Task WarmSessionListAndActivityReadOnlyTheNewJournalTail()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		using var temporary = new TemporaryDirectory();
		var started = new DateTimeOffset(2026, 9, 20, 1, 2, 3, TimeSpan.Zero);
		var active = CreateSession(temporary.Path, 90, started);
		using var store = CreateStore(temporary.Path, activeSessionProvider: () => [CreateActiveRecord(active)]);
		const int completedSessionCount = 3;
		const int completedCallCount = 5;
		const int activeCallCount = 10;
		for (var sessionIndex = 0; sessionIndex < completedSessionCount; sessionIndex++)
		{
			var completed = CreateSession(temporary.Path, 100 + sessionIndex, started.AddMinutes(sessionIndex + 1));
			await store.StartSession(completed, cancellationToken);
			for (var sequence = 1; sequence <= completedCallCount; sequence++)
				await store.RecordCall(completed.Id, CreateCall(sequence), cancellationToken);
			await store.EndSession(completed.Id, completed.StartedUtc.AddMinutes(1),
				new AgentJournalTotals(completedCallCount, 600, 150, completedCallCount, 10, 15, 0),
				cancellationToken);
		}
		await store.StartSession(active, cancellationToken);
		for (var sequence = 1; sequence <= activeCallCount; sequence++)
			await store.RecordCall(active.Id, CreateCall(sequence), cancellationToken);
		await store.ListSessionsAsync(cancellationToken: cancellationToken);
		await store.ReadActivityAsync(active.Id, activeCallCount, cancellationToken);

		long bytes = 0;
		long records = 0;
		store.BytesReadObserver = value => Interlocked.Add(ref bytes, value);
		store.RecordsReadObserver = value => Interlocked.Add(ref records, value);
		await store.RecordCall(active.Id, CreateCall(activeCallCount + 1), cancellationToken);
		var sessions = await store.ListSessionsAsync(cancellationToken: cancellationToken);
		var activity = await store.ReadActivityAsync(active.Id, activeCallCount, cancellationToken);

		output.WriteLine($"warm event: bytes={bytes:N0}; records={records:N0}; sessions={sessions.Count:N0}");
		Assert.Equal(completedSessionCount + 1, sessions.Count);
		Assert.Equal(activeCallCount + 1, Assert.Single(activity!.AppendedCalls).Sequence);
		Assert.InRange(bytes, 1, 64 * 1024);
		Assert.InRange(records, 1, 8);
	}

	[Fact]
	public async Task ReceiptKeepsEqualRelativePathsDistinctAcrossRoots()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		using var temporary = new TemporaryDirectory();
		var firstRoot = temporary.CreateFolder("first");
		var secondRoot = temporary.CreateFolder("second");
		using var store = CreateStore(temporary.Path);
		var session = CreateSession(
			firstRoot,
			75,
			new DateTimeOffset(2026, 9, 20, 1, 2, 3, TimeSpan.Zero)) with
		{
			Roots =
				[
					new AgentJournalRoot(firstRoot, "first"),
					new AgentJournalRoot(secondRoot, "second")
				]
		};
		await store.StartSession(session, cancellationToken);
		await store.RecordCall(session.Id, CreateCall(1) with { RootIndex = 0 }, cancellationToken);
		await store.RecordCall(session.Id, CreateCall(2) with { RootIndex = 1 }, cancellationToken);

		var receipt = Assert.IsType<AgentJournalReceipt>(await store.ReadReceiptAsync(session.Id, cancellationToken));

		Assert.Equal(2, receipt.DeliveredPaths.Count);
		Assert.Contains(receipt.DeliveredPaths, static item => item.Path == "root 1: src/Program.cs");
		Assert.Contains(receipt.DeliveredPaths, static item => item.Path == "root 2: src/Program.cs");
	}

	[Fact]
	public async Task InvalidExplicitProjectFilterNeverListsOrClearsAllSessions()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		using var temporary = new TemporaryDirectory();
		using var store = CreateStore(temporary.Path);
		var session = CreateSession(
			temporary.Path,
			63,
			new DateTimeOffset(2026, 9, 20, 1, 2, 5, TimeSpan.Zero));
		await store.StartSession(session, cancellationToken);

		var listed = await store.ListSessionsAsync(" ", cancellationToken: cancellationToken);
		var removed = await store.ClearAsync(" ", cancellationToken);

		Assert.Empty(listed);
		Assert.Equal(0, removed);
		Assert.Single(await store.ListSessionsAsync(cancellationToken: cancellationToken));
	}

	[Fact]
	public void ReceiptFormatterProducesStableMarkdownAndJsonContracts()
	{
		var started = new DateTimeOffset(2026, 9, 20, 1, 2, 3, TimeSpan.Zero);
		var totals = new AgentJournalTotals(1, 120, 30, 1, 2, 3, 0);
		var session = new AgentJournalSession(
			"20260920-010203-42",
			started,
			started.AddSeconds(2),
			42,
			started.AddMinutes(-1),
			"sample-client",
			"1.2.3",
			AgentJournalMode.Live,
			[new AgentJournalRoot("project-root", "sample")],
			AgentJournalToolSet.Reduced,
			"5.2.0",
			HidePrivateData: true,
			totals,
			IsLive: false);
		var call = CreateCall(1) with
		{
			Arguments = new Dictionary<string, string>(StringComparer.Ordinal)
			{
				["path"] = "src/Program.cs"
			},
			Notices = [AgentJournalNoticeCodes.OutsideSelection]
		};
		var receipt = new AgentJournalReceipt(
			session,
			totals,
			[new AgentJournalDeliveredPath("src/Program.cs", 1)],
			[call]);
		var formatter = new AgentJournalReceiptFormatter();

		var markdown = formatter.FormatMarkdown(receipt);
		var json = formatter.FormatJson(receipt);

		const string expectedJson = """
			{
			  "schema": "devprojex-agent-journal",
			  "version": 1,
			  "receipt": {
			    "session": {
			      "id": "20260920-010203-42",
			      "startedUtc": "2026-09-20T01:02:03+00:00",
			      "endedUtc": "2026-09-20T01:02:05+00:00",
			      "pid": 42,
			      "processStartUtc": "2026-09-20T01:01:03+00:00",
			      "clientName": "sample-client",
			      "clientVersion": "1.2.3",
			      "mode": "Live",
			      "roots": [{ "configuredPath": "project-root", "name": "sample" }],
			      "toolSet": "Reduced",
			      "serverVersion": "5.2.0",
			      "hidePrivateData": true,
			      "totals": { "calls": 1, "resultCharacters": 120, "estimatedTokens": 30, "filesDelivered": 1, "secretsMasked": 2, "privateDataMasked": 3, "errors": 0 },
			      "isLive": false
			    },
			    "totals": { "calls": 1, "resultCharacters": 120, "estimatedTokens": 30, "filesDelivered": 1, "secretsMasked": 2, "privateDataMasked": 3, "errors": 0 },
			    "deliveredPaths": [{ "path": "src/Program.cs", "calls": 1 }],
			    "calls": [{
			      "sequence": 1,
			      "utc": "2026-09-20T01:02:04+00:00",
			      "tool": "get_file",
			      "rootIndex": 0,
			      "arguments": { "path": "src/Program.cs" },
			      "revision": 2,
			      "durationMs": 10,
			      "resultCharacters": 120,
			      "estimatedTokens": 30,
			      "filesDelivered": 1,
			      "deliveredPaths": ["src/Program.cs"],
			      "additionalDeliveredPaths": 0,
			      "secretsMasked": 2,
			      "privateDataMasked": 3,
			      "notices": ["outside-selection"],
			      "errorCode": null
			    }]
			  }
			}
			""";
		Assert.StartsWith("# DevProjex agent journal" + Environment.NewLine, markdown, StringComparison.Ordinal);
		Assert.Contains("- Started: 2026-09-20T01:02:03.0000000Z", markdown, StringComparison.Ordinal);
		Assert.Contains("- Ended: 2026-09-20T01:02:05.0000000Z", markdown, StringComparison.Ordinal);
		Assert.Contains("- Session: 20260920-010203-42", markdown, StringComparison.Ordinal);
		Assert.Contains("- Client: sample-client 1.2.3", markdown, StringComparison.Ordinal);
		Assert.Contains("|1|120|30|1|2|3|0|", markdown, StringComparison.Ordinal);
		Assert.Contains("|src/Program.cs|1|", markdown, StringComparison.Ordinal);
		Assert.Contains(
			"|1|2026-09-20T01:02:04.0000000Z|get_file|10|120|30|1|outside-selection||",
			markdown,
			StringComparison.Ordinal);
		AssertBalancedUntrustedDataBlocks(markdown, expectedCount: 3);
		using var expectedDocument = JsonDocument.Parse(expectedJson);
		using var actualDocument = JsonDocument.Parse(json);
		Assert.Equal(
			JsonSerializer.Serialize(expectedDocument.RootElement),
			JsonSerializer.Serialize(actualDocument.RootElement));
	}

	[Fact]
	public void ReceiptFormatterRedactsMetadataAndBalancesRandomMarkdownBoundaries()
	{
		var secret = string.Concat("AKIAZ7M3", "Q5X2P6N4R7T5");
		var started = new DateTimeOffset(2026, 9, 20, 1, 2, 3, TimeSpan.Zero);
		var session = CreateSession(Path.GetTempPath(), 82, started) with
		{
			ClientName = "client-" + secret,
			ClientVersion = "ivan.petrov@corp.internal",
			Roots = [new AgentJournalRoot(Path.GetFullPath(Path.GetTempPath()), "root-" + secret)]
		};
		var call = CreateCall(1) with
		{
			Tool = "tool-" + secret,
			Arguments = new Dictionary<string, string>(StringComparer.Ordinal)
			{
				["path"] = "src/" + secret + ".txt"
			},
			DeliveredPaths = ["src/" + secret + ".txt"],
			Notices =
			[
				"<untrusted-data-0123456789abcdef01234567>",
				"</untrusted-data-89abcdef0123456701234567>"
			],
			ErrorCode = "error-" + secret
		};
		var receipt = new AgentJournalReceipt(
			session,
			session.Totals,
			[new AgentJournalDeliveredPath("src/" + secret + ".txt", 1)],
			[call]);
		var formatter = new AgentJournalReceiptFormatter();

		var firstMarkdown = formatter.FormatMarkdown(receipt);
		var secondMarkdown = formatter.FormatMarkdown(receipt);
		var json = formatter.FormatJson(receipt);

		Assert.DoesNotContain(secret, firstMarkdown, StringComparison.Ordinal);
		Assert.DoesNotContain(secret, json, StringComparison.Ordinal);
		Assert.DoesNotContain("ivan.petrov@corp.internal", firstMarkdown, StringComparison.Ordinal);
		Assert.DoesNotContain("ivan.petrov@corp.internal", json, StringComparison.Ordinal);
		Assert.Contains("[redacted]", firstMarkdown, StringComparison.Ordinal);
		Assert.Contains("[redacted]", json, StringComparison.Ordinal);
		Assert.DoesNotContain("<untrusted-data-0123456789abcdef01234567>", firstMarkdown, StringComparison.Ordinal);
		Assert.DoesNotContain("</untrusted-data-89abcdef0123456701234567>", firstMarkdown, StringComparison.Ordinal);
		var firstNonces = Regex.Matches(firstMarkdown, @"<untrusted-data-([0-9a-f]{24})>")
			.Select(static match => match.Groups[1].Value)
			.ToArray();
		Assert.NotEmpty(firstNonces);
		Assert.Equal(firstNonces.Length, firstNonces.Distinct(StringComparer.Ordinal).Count());
		foreach (var nonce in firstNonces)
			Assert.Single(Regex.Matches(firstMarkdown, $@"</untrusted-data-{nonce}>").Cast<Match>());
		var secondMatch = Regex.Match(secondMarkdown, @"<untrusted-data-([0-9a-f]{24})>");
		Assert.True(secondMatch.Success);
		var secondNonce = secondMatch.Groups[1].Value;
		Assert.NotEqual(firstNonces[0], secondNonce);
		using var document = JsonDocument.Parse(json);
		Assert.Equal(AgentJournalReceiptFormatter.JsonSchema,
			document.RootElement.GetProperty("schema").GetString());
		Assert.Equal(AgentJournalReceiptFormatter.JsonSchemaVersion,
			document.RootElement.GetProperty("version").GetInt32());
	}

	private static void AssertBalancedUntrustedDataBlocks(string value, int expectedCount)
	{
		var matches = Regex.Matches(value, @"<untrusted-data-([0-9a-f]{24})>");
		Assert.Equal(expectedCount, matches.Count);
		foreach (Match match in matches)
		{
			var nonce = match.Groups[1].Value;
			Assert.Single(Regex.Matches(value, $@"</untrusted-data-{nonce}>").Cast<Match>());
		}
	}

	[Fact]
	public void ReceiptForInactiveSessionWithoutEndUsesLastEventAsLowerBound()
	{
		var started = new DateTimeOffset(2026, 9, 20, 1, 2, 3, TimeSpan.Zero);
		var session = CreateSession(Path.GetTempPath(), 79, started) with
		{
			IsLive = false,
			EndedUtc = null,
			Totals = new AgentJournalTotals(1, 120, 30, 1, 2, 3, 0)
		};
		var call = CreateCall(1) with { Utc = started.AddSeconds(7) };
		var receipt = new AgentJournalReceipt(session, session.Totals, [], [call]);

		var markdown = new AgentJournalReceiptFormatter().FormatMarkdown(receipt);

		Assert.Contains("inactive; end time unknown", markdown, StringComparison.Ordinal);
		Assert.Contains("2026-09-20T01:02:10.0000000Z", markdown, StringComparison.Ordinal);
		Assert.DoesNotContain("running", markdown, StringComparison.OrdinalIgnoreCase);
		Assert.Contains("outside-selection", markdown, StringComparison.Ordinal);
	}

	private static AgentJournalStore CreateStore(
		string stateRoot,
		AgentJournalRetentionPolicy? retention = null,
		Func<IReadOnlyList<LiveSessionRecord>>? activeSessionProvider = null) =>
		new(
			() => stateRoot,
			activeSessionProvider: activeSessionProvider ?? (static () => []),
			retention: retention);

	private static LiveSessionRecord CreateActiveRecord(AgentJournalSession session) =>
		new(
			session.Pid,
			session.ProcessStartUtc,
			session.ClientName,
			session.ClientVersion,
			session.Roots.Select(static root => root.ConfiguredPath).ToArray(),
			session.StartedUtc,
			session.Mode);

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
			Notices: [AgentJournalNoticeCodes.OutsideSelection, "unrecognized-notice"],
			ErrorCode: null);

	private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
	{
		public override DateTimeOffset GetUtcNow() => utcNow;
	}
}
