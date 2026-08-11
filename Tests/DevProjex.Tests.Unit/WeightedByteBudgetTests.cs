using DevProjex.Application.Compression;

namespace DevProjex.Tests.Unit;

public sealed class WeightedByteBudgetTests
{
	[Fact]
	public async Task AcquireAsync_DoesNotBypassTheHeadWaiter()
	{
		using var budget = new WeightedByteBudget(maximumBytes: 10);
		var held = await budget.AcquireAsync(6, TestContext.Current.CancellationToken);
		var first = budget.AcquireAsync(7, TestContext.Current.CancellationToken).AsTask();
		var second = budget.AcquireAsync(4, TestContext.Current.CancellationToken).AsTask();

		Assert.False(first.IsCompleted);
		Assert.False(second.IsCompleted);
		AssertDiagnostics(budget, availableBytes: 4, issuedBytes: 6, pendingWaiters: 2);

		held.Dispose();
		var firstLease = await first.WaitAsync(
			TimeSpan.FromSeconds(5),
			TestContext.Current.CancellationToken);
		Assert.False(second.IsCompleted);
		AssertDiagnostics(budget, availableBytes: 3, issuedBytes: 7, pendingWaiters: 1);

		firstLease.Dispose();
		var secondLease = await second.WaitAsync(
			TimeSpan.FromSeconds(5),
			TestContext.Current.CancellationToken);
		AssertDiagnostics(budget, availableBytes: 6, issuedBytes: 4, pendingWaiters: 0);
		secondLease.Dispose();
		AssertDiagnostics(budget, availableBytes: 10, issuedBytes: 0, pendingWaiters: 0);
	}

	[Fact]
	public async Task AcquireAsync_CanceledHeadImmediatelyUnblocksTheNextWaiter()
	{
		using var budget = new WeightedByteBudget(maximumBytes: 10);
		using var held = await budget.AcquireAsync(6, TestContext.Current.CancellationToken);
		using var cancellation = new CancellationTokenSource();
		var head = budget.AcquireAsync(7, cancellation.Token).AsTask();
		var next = budget.AcquireAsync(4, TestContext.Current.CancellationToken).AsTask();

		cancellation.Cancel();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => head);
		var nextLease = await next.WaitAsync(
			TimeSpan.FromSeconds(5),
			TestContext.Current.CancellationToken);
		AssertDiagnostics(budget, availableBytes: 0, issuedBytes: 10, pendingWaiters: 0);
		nextLease.Dispose();
	}

	[Fact]
	public async Task AcquireAsync_CanceledMiddleWaiterDoesNotConsumeReleasedCapacity()
	{
		using var budget = new WeightedByteBudget(maximumBytes: 10);
		var held = await budget.AcquireAsync(10, TestContext.Current.CancellationToken);
		var first = budget.AcquireAsync(4, TestContext.Current.CancellationToken).AsTask();
		using var cancellation = new CancellationTokenSource();
		var canceled = budget.AcquireAsync(3, cancellation.Token).AsTask();
		var last = budget.AcquireAsync(6, TestContext.Current.CancellationToken).AsTask();

		cancellation.Cancel();
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
		held.Dispose();

		var firstLease = await first.WaitAsync(
			TimeSpan.FromSeconds(5),
			TestContext.Current.CancellationToken);
		var lastLease = await last.WaitAsync(
			TimeSpan.FromSeconds(5),
			TestContext.Current.CancellationToken);
		AssertDiagnostics(budget, availableBytes: 0, issuedBytes: 10, pendingWaiters: 0);
		firstLease.Dispose();
		lastLease.Dispose();
	}

	[Fact]
	public async Task Lease_DisposeIsIdempotentAndRestoresTheEntireCapacity()
	{
		using var budget = new WeightedByteBudget(maximumBytes: 10);
		var lease = await budget.AcquireAsync(10, TestContext.Current.CancellationToken);

		lease.Dispose();
		lease.Dispose();

		using var completeCapacity = await budget.AcquireAsync(
			10,
			TestContext.Current.CancellationToken);
	}

	[Fact]
	public async Task Dispose_FaultsPendingWaitersAndAllowsOutstandingLeasesToReturn()
	{
		var budget = new WeightedByteBudget(maximumBytes: 10);
		var held = await budget.AcquireAsync(10, TestContext.Current.CancellationToken);
		var pending = budget.AcquireAsync(1, TestContext.Current.CancellationToken).AsTask();

		budget.Dispose();

		await Assert.ThrowsAsync<ObjectDisposedException>(() => pending);
		held.Dispose();
		budget.Dispose();
	}

	[Fact]
	public async Task ConcurrentWeightedLeases_NeverExceedCapacityOrLeakUnits()
	{
		const int capacity = 32;
		using var budget = new WeightedByteBudget(capacity);
		var inUse = 0;
		var maximumInUse = 0;
		var workers = Enumerable.Range(0, 16).Select(async worker =>
		{
			for (var iteration = 0; iteration < 50; iteration++)
			{
				var weight = 1 + ((worker * 17 + iteration * 7) % 8);
				using var lease = await budget.AcquireAsync(
					weight,
					TestContext.Current.CancellationToken);
				var current = Interlocked.Add(ref inUse, weight);
				UpdateMaximum(ref maximumInUse, current);
				Assert.InRange(current, 1, capacity);
				await Task.Yield();
				Interlocked.Add(ref inUse, -weight);
			}
		});

		await Task.WhenAll(workers).WaitAsync(
			TimeSpan.FromSeconds(10),
			TestContext.Current.CancellationToken);

		Assert.Equal(0, Volatile.Read(ref inUse));
		Assert.InRange(Volatile.Read(ref maximumInUse), 1, capacity);
		AssertDiagnostics(budget, capacity, issuedBytes: 0, pendingWaiters: 0);
		using var completeCapacity = await budget.AcquireAsync(
			capacity,
			TestContext.Current.CancellationToken);
	}

	[Fact]
	public async Task SeededWeightedQueue_WithMiddleCancellationsCompletesWithoutLeaks()
	{
		const int capacity = 64;
		using var budget = new WeightedByteBudget(capacity);
		var held = await budget.AcquireAsync(capacity, TestContext.Current.CancellationToken);
		var random = new Random(73_421);
		var requests = new List<(Task<WeightedByteBudget.Lease> Task, CancellationTokenSource? Cancellation)>();
		for (var index = 0; index < 40; index++)
		{
			var cancellation = index % 5 is 1 or 3 ? new CancellationTokenSource() : null;
			var weight = random.Next(1, 17);
			requests.Add((
				budget.AcquireAsync(
					weight,
					cancellation?.Token ?? TestContext.Current.CancellationToken).AsTask(),
				cancellation));
		}

		foreach (var request in requests.Where(static request => request.Cancellation is not null))
			request.Cancellation!.Cancel();
		held.Dispose();

		foreach (var request in requests)
		{
			if (request.Cancellation is not null)
			{
				await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request.Task);
				request.Cancellation.Dispose();
				continue;
			}

			using var lease = await request.Task.WaitAsync(
				TimeSpan.FromSeconds(5),
				TestContext.Current.CancellationToken);
		}

		AssertDiagnostics(budget, capacity, issuedBytes: 0, pendingWaiters: 0);
	}

	[Fact]
	public async Task ConsumerException_StillReturnsTheLeaseCapacity()
	{
		using var budget = new WeightedByteBudget(maximumBytes: 16);

		await Assert.ThrowsAsync<InvalidOperationException>(async () =>
		{
			using var lease = await budget.AcquireAsync(
				16,
				TestContext.Current.CancellationToken);
			await Task.Yield();
			throw new InvalidOperationException("consumer failed");
		});

		AssertDiagnostics(budget, availableBytes: 16, issuedBytes: 0, pendingWaiters: 0);
	}

	[Fact]
	public async Task CancellationReleaseRace_NeverLosesOrDuplicatesCapacity()
	{
		using var budget = new WeightedByteBudget(maximumBytes: 1);
		for (var iteration = 0; iteration < 250; iteration++)
		{
			var held = await budget.AcquireAsync(1, TestContext.Current.CancellationToken);
			using var cancellation = new CancellationTokenSource();
			var pending = budget.AcquireAsync(1, cancellation.Token).AsTask();
			var cancel = Task.Run(cancellation.Cancel, TestContext.Current.CancellationToken);

			held.Dispose();
			await cancel;
			try
			{
				using var granted = await pending.WaitAsync(
					TimeSpan.FromSeconds(5),
					TestContext.Current.CancellationToken);
			}
			catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
			{
			}

			using var capacityCheck = await budget.AcquireAsync(
				1,
				TestContext.Current.CancellationToken);
		}
	}

	private static void UpdateMaximum(ref int maximum, int candidate)
	{
		var current = Volatile.Read(ref maximum);
		while (candidate > current)
		{
			var observed = Interlocked.CompareExchange(ref maximum, candidate, current);
			if (observed == current)
				return;
			current = observed;
		}
	}

	private static void AssertDiagnostics(
		WeightedByteBudget budget,
		long availableBytes,
		long issuedBytes,
		int pendingWaiters)
	{
		var diagnostics = budget.Diagnostics;
		Assert.Equal(diagnostics.MaximumBytes, diagnostics.AvailableBytes + diagnostics.IssuedBytes);
		Assert.Equal(availableBytes, diagnostics.AvailableBytes);
		Assert.Equal(issuedBytes, diagnostics.IssuedBytes);
		Assert.Equal(pendingWaiters, diagnostics.PendingWaiters);
	}
}
