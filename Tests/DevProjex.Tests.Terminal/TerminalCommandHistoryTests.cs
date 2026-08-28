namespace DevProjex.Tests.Terminal;

public sealed class TerminalCommandHistoryTests
{
	[Fact]
	public async Task PersistenceQueueCoalescesPendingSnapshotsToTheLatestHistory()
	{
		var firstWriteStarted = new TaskCompletionSource(
			TaskCreationOptions.RunContinuationsAsynchronously);
		var releaseFirstWrite = new TaskCompletionSource(
			TaskCreationOptions.RunContinuationsAsynchronously);
		var writes = new List<IReadOnlyList<string>>();
		var queue = new TerminalCommandHistoryPersistenceQueue(async (history, _, cancellationToken) =>
		{
			writes.Add(history);
			if (writes.Count == 1)
			{
				firstWriteStarted.SetResult();
				await releaseFirstWrite.Task.WaitAsync(cancellationToken);
			}
		});

		var drain = queue.Enqueue(["first"]);
		Assert.NotNull(drain);
		await firstWriteStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
		Assert.Null(queue.Enqueue(["first", "second"]));
		Assert.Null(queue.Enqueue(["first", "second", "third"]));

		releaseFirstWrite.SetResult();
		await drain.WaitAsync(TestContext.Current.CancellationToken);

		Assert.Equal(2, writes.Count);
		Assert.Equal(["first"], writes[0]);
		Assert.Equal(["first", "second", "third"], writes[1]);
	}

	[Fact]
	public async Task PersistenceQueueContinuesWithLatestSnapshotAfterWriteFailure()
	{
		var firstWriteStarted = new TaskCompletionSource(
			TaskCreationOptions.RunContinuationsAsynchronously);
		var releaseFirstWrite = new TaskCompletionSource(
			TaskCreationOptions.RunContinuationsAsynchronously);
		var writes = new List<IReadOnlyList<string>>();
		var queue = new TerminalCommandHistoryPersistenceQueue(async (history, _, cancellationToken) =>
		{
			writes.Add(history);
			if (writes.Count != 1)
				return;

			firstWriteStarted.SetResult();
			await releaseFirstWrite.Task.WaitAsync(cancellationToken);
			throw new IOException("The settings lock is busy.");
		});

		var drain = queue.Enqueue(["first"]);
		Assert.NotNull(drain);
		await firstWriteStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
		Assert.Null(queue.Enqueue(["latest"]));

		releaseFirstWrite.SetResult();
		await drain.WaitAsync(TestContext.Current.CancellationToken);

		Assert.Equal(2, writes.Count);
		Assert.Equal(["latest"], writes[1]);
	}

	[Fact]
	public async Task PersistenceQueueCoalescesLanguageWithTheLatestHistory()
	{
		var firstWriteStarted = new TaskCompletionSource(
			TaskCreationOptions.RunContinuationsAsynchronously);
		var releaseFirstWrite = new TaskCompletionSource(
			TaskCreationOptions.RunContinuationsAsynchronously);
		var writes = new List<(IReadOnlyList<string> History, AppLanguage? Language)>();
		var queue = new TerminalCommandHistoryPersistenceQueue(
			async (history, language, cancellationToken) =>
			{
				writes.Add((history, language));
				if (writes.Count == 1)
				{
					firstWriteStarted.SetResult();
					await releaseFirstWrite.Task.WaitAsync(cancellationToken);
				}
			});

		var drain = queue.Enqueue(["first"]);
		Assert.NotNull(drain);
		await firstWriteStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
		Assert.Null(queue.Enqueue(["first", "language ja"], AppLanguage.Ja));
		Assert.Null(queue.Enqueue(["first", "language ja", "view content"]));

		releaseFirstWrite.SetResult();
		await drain.WaitAsync(TestContext.Current.CancellationToken);

		Assert.Equal(2, writes.Count);
		Assert.Equal(["first", "language ja", "view content"], writes[1].History);
		Assert.Equal(AppLanguage.Ja, writes[1].Language);
	}

	[Fact]
	public async Task PersistenceQueueObservesTheSharedShutdownToken()
	{
		using var shutdown = new CancellationTokenSource();
		var writeStarted = new TaskCompletionSource(
			TaskCreationOptions.RunContinuationsAsynchronously);
		var queue = new TerminalCommandHistoryPersistenceQueue(
			async (_, _, cancellationToken) =>
			{
				writeStarted.SetResult();
				await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
			},
			shutdown.Token);

		var drain = queue.Enqueue(["language ja"], AppLanguage.Ja);
		Assert.NotNull(drain);
		await writeStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
		await shutdown.CancelAsync();

		await drain.WaitAsync(TestContext.Current.CancellationToken);
	}

	[Fact]
	public async Task PersistenceQueueRecoversAfterUnexpectedWriteFailure()
	{
		var firstWriteStarted = new TaskCompletionSource(
			TaskCreationOptions.RunContinuationsAsynchronously);
		var releaseFirstWrite = new TaskCompletionSource(
			TaskCreationOptions.RunContinuationsAsynchronously);
		var writes = new List<IReadOnlyList<string>>();
		var queue = new TerminalCommandHistoryPersistenceQueue(async (history, _, cancellationToken) =>
		{
			writes.Add(history);
			if (writes.Count != 1)
				return;

			firstWriteStarted.SetResult();
			await releaseFirstWrite.Task.WaitAsync(cancellationToken);
			throw new InvalidOperationException("Unexpected persistence failure.");
		});

		var failedDrain = queue.Enqueue(["first"]);
		Assert.NotNull(failedDrain);
		await firstWriteStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
		Assert.Null(queue.Enqueue(["latest"]));

		releaseFirstWrite.SetResult();
		await Assert.ThrowsAsync<InvalidOperationException>(async () =>
			await failedDrain.WaitAsync(TestContext.Current.CancellationToken));

		Assert.Equal(["latest"], writes[1]);
		var recoveredDrain = queue.Enqueue(["after-recovery"]);
		Assert.NotNull(recoveredDrain);
		await recoveredDrain.WaitAsync(TestContext.Current.CancellationToken);
		Assert.Equal(["after-recovery"], writes[2]);
	}

	[Fact]
	public void AddRetainsTheNewestFiftyCommands()
	{
		var history = new TerminalCommandHistory();

		for (var index = 0; index < 55; index++)
			history.Add($"search item-{index}");

		Assert.Equal(TerminalCommandHistory.MaximumEntries, history.Entries.Count);
		Assert.Equal("search item-5", history.Entries[0]);
		Assert.Equal("search item-54", history.Entries[^1]);
	}

	[Fact]
	public void AddDeduplicatesOnlyConsecutiveCommands()
	{
		var history = new TerminalCommandHistory();

		Assert.True(history.Add("  view content  "));
		Assert.False(history.Add("view content"));
		Assert.True(history.Add("view tree"));
		Assert.True(history.Add("view content"));

		Assert.Equal(["view content", "view tree", "view content"], history.Entries);
	}

	[Fact]
	public void NavigationRestoresTheDraftAfterTheNewestCommand()
	{
		var history = new TerminalCommandHistory(["view tree", "format json"]);

		Assert.Equal("format json", history.Previous("set hide-secrets "));
		Assert.Equal("view tree", history.Previous("ignored"));
		Assert.Equal("view tree", history.Previous("ignored"));
		Assert.Equal("format json", history.Next());
		Assert.Equal("set hide-secrets ", history.Next());
		Assert.Equal("set hide-secrets ", history.Next());
	}

	[Fact]
	public void ConstructorNormalizesPersistedHistory()
	{
		var history = new TerminalCommandHistory([
			" ",
			"view tree",
			"view tree",
			" format json "]);

		Assert.Equal(["view tree", "format json"], history.Entries);
	}

	[Fact]
	public void ConstructorBoundsPersistedCommandsWithoutSplittingUnicodeScalars()
	{
		var prefix = new string('x', 4_095);

		var history = new TerminalCommandHistory([prefix + "😀tail"]);

		Assert.Equal([prefix], history.Entries);
	}

	[Fact]
	public void ConstructorEscapesControlCharactersFromPersistedHistory()
	{
		var history = new TerminalCommandHistory([" search first\r\nsecond\t\u001B "]);

		Assert.Equal([@"search first\r\nsecond\t\u001B"], history.Entries);
		Assert.DoesNotContain(history.Entries[0], char.IsControl);
	}
}
