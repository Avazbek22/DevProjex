using DevProjex.Infrastructure.FileSystem;

namespace DevProjex.Tests.Unit;

public sealed class SharedAsyncOperationTests
{
	[Fact]
	public async Task ReleasingOneWaiter_KeepsOperationAliveForRemainingWaiter()
	{
		var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
		var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var releaseCount = 0;
		var operation = new SharedAsyncOperation<int>(
			async cancellationToken =>
			{
				using var registration = cancellationToken.Register(cancellationObserved.SetResult);
				started.SetResult();
				return await completion.Task.WaitAsync(cancellationToken);
			},
			_ => Interlocked.Increment(ref releaseCount));

		Assert.True(operation.TryAcquire(out var firstLease));
		Assert.True(operation.TryAcquire(out var secondLease));
		var task = operation.Task;
		await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

		firstLease.Dispose();
		Assert.False(cancellationObserved.Task.IsCompleted);
		completion.SetResult(42);

		Assert.Equal(42, await task);
		secondLease.Dispose();
		Assert.Equal(1, Volatile.Read(ref releaseCount));
	}

	[Fact]
	public async Task ReleasingLastWaiter_CancelsOperationAndReleasesOwner()
	{
		var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var operationToken = CancellationToken.None;
		var releaseCount = 0;
		var operation = new SharedAsyncOperation<int>(
			async cancellationToken =>
			{
				operationToken = cancellationToken;
				started.SetResult();
				await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
				return 0;
			},
			_ => Interlocked.Increment(ref releaseCount));

		Assert.True(operation.TryAcquire(out var firstLease));
		Assert.True(operation.TryAcquire(out var secondLease));
		var task = operation.Task;
		await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

		firstLease.Dispose();
		Assert.False(operationToken.IsCancellationRequested);
		secondLease.Dispose();

		Assert.True(operationToken.IsCancellationRequested);
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
		Assert.Equal(1, Volatile.Read(ref releaseCount));
		Assert.False(operation.TryAcquire(out _));
	}
}
