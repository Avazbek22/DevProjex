namespace DevProjex.Tests.Unit;

public sealed class LatestValueProgressRelayTests
{
	[Fact]
	public async Task BurstKeepsOnePendingDispatchAndFlushesOnlyLatestValue()
	{
		var scheduler = new ManualScheduler();
		var consumed = new List<int>();
		using var relay = new LatestValueProgressRelay<int>(scheduler.Schedule, consumed.Add);

		for (var value = 0; value < 100_000; value++)
			relay.Report(value);
		var completion = relay.CompleteAsync();

		Assert.Equal(1, scheduler.PendingCount);
		Assert.False(completion.IsCompleted);
		var dispatch = scheduler.TakeNext();
		dispatch();
		await completion;

		Assert.Equal([99_999], consumed);
		Assert.Equal(0, scheduler.PendingCount);

		// A scheduler must not be able to replay a callback from a completed generation.
		dispatch();
		Assert.Equal([99_999], consumed);
	}

	[Fact]
	public void DisposeInvalidatesPendingDispatchAndFutureReports()
	{
		var scheduler = new ManualScheduler();
		var consumed = new List<int>();
		var relay = new LatestValueProgressRelay<int>(scheduler.Schedule, consumed.Add);
		relay.Report(1);
		var dispatch = scheduler.TakeNext();

		relay.Dispose();
		dispatch();
		relay.Report(2);

		Assert.Empty(consumed);
		Assert.Equal(0, scheduler.PendingCount);
	}

	private sealed class ManualScheduler
	{
		private readonly Queue<Action> _pending = new();

		public int PendingCount => _pending.Count;

		public void Schedule(Action action) => _pending.Enqueue(action);

		public Action TakeNext() => _pending.Dequeue();
	}
}
