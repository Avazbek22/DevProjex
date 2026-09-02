using System.Collections.Concurrent;

namespace DevProjex.Tests.Unit;

/// <summary>
/// Tests for cancellation patterns used in parallel operations.
/// These tests verify the cancellation behavior implemented in MainWindow.RecalculateMetricsAsync.
/// </summary>
public sealed class CancellationPatternTests
{
	[Fact]
	public async Task CancellationToken_PreventsExecution_WhenCancelledBeforeStart()
	{
		using var cts = new CancellationTokenSource();
		var executed = false;

		cts.Cancel();

		var task = Task.Run(() => executed = true, cts.Token);

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
		Assert.False(executed);
	}

	[Fact]
	public async Task TaskWaitAll_ThrowsOperationCancelled_WhenTokenCancelled()
	{
		using var cts = new CancellationTokenSource();
		using var gate = new ManualResetEventSlim(false);

		var task1 = Task.Run(() =>
		{
			gate.Wait();
			return 1;
		});

		var task2 = Task.Run(() =>
		{
			gate.Wait();
			return 2;
		});

		cts.Cancel();

		try
		{
			await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
				Task.Run(() => Task.WaitAll([task1, task2], cts.Token), cancellationToken: TestContext.Current.CancellationToken));
		}
		finally
		{
			gate.Set();
			await Task.WhenAll(task1, task2);
		}
	}

	[Fact]
	public async Task NewCts_CancelsPreviousCalculation()
	{
		CancellationTokenSource? currentCts = null;
		var firstCalculationCompleted = false;
		var secondCalculationCompleted = false;
		var firstStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		var releaseFirst = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

		currentCts = new CancellationTokenSource();
		var token1 = currentCts.Token;

		var task1 = Task.Run(async () =>
		{
			firstStarted.TrySetResult(true);
			await releaseFirst.Task.WaitAsync(token1);
			if (!token1.IsCancellationRequested)
				firstCalculationCompleted = true;
		}, token1);

		await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken: TestContext.Current.CancellationToken);

		currentCts.Cancel();
		currentCts.Dispose();

		currentCts = new CancellationTokenSource();
		var token2 = currentCts.Token;
		var task2 = Task.Run(() =>
		{
			if (!token2.IsCancellationRequested)
				secondCalculationCompleted = true;
		}, token2);

		releaseFirst.TrySetResult(true);

		try
		{
			await task1;
		}
		catch (OperationCanceledException)
		{
			// Expected for the superseded calculation.
		}

		await task2;

		Assert.False(firstCalculationCompleted);
		Assert.True(secondCalculationCompleted);
	}

	[Fact]
	public async Task CancellationCheck_AfterParallelCalculations_SkipsUIUpdate()
	{
		using var cts = new CancellationTokenSource();
		var uiUpdated = false;

		var task = Task.Run(async () =>
		{
			var calc1 = Task.Run(() => 1, cts.Token);
			var calc2 = Task.Run(() => 2, cts.Token);

			await Task.WhenAll(calc1, calc2);
			cts.Cancel();

			if (cts.Token.IsCancellationRequested)
				return;

			uiUpdated = true;
		}, TestContext.Current.CancellationToken);

		await task;

		Assert.False(uiUpdated);
	}

	[Fact]
	public async Task VersionCheckAndCancellation_DoubleProtection()
	{
		var version = 0;
		CancellationTokenSource? currentCts = null;
		var updates = new ConcurrentBag<int>();

		async Task<(Task Worker, Task Started, TaskCompletionSource<bool> ReleaseGate, int Version)> StartRecalculationAsync()
		{
			currentCts?.Cancel();
			currentCts?.Dispose();

			currentCts = new CancellationTokenSource();
			var localCts = currentCts;
			// Capture the token once, because the source itself can be disposed by a newer recalculation
			// before the worker reaches the next cancellation check.
			var localToken = localCts.Token;
			var localVersion = Interlocked.Increment(ref version);
			var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
			var releaseGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

			var worker = Task.Run(async () =>
			{
				started.TrySetResult(true);
				await releaseGate.Task.WaitAsync(localToken);

				if (!localToken.IsCancellationRequested &&
				    localVersion == Volatile.Read(ref version))
				{
					updates.Add(localVersion);
				}
			}, localToken);

			await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
			return (worker, started.Task, releaseGate, localVersion);
		}

		var calc1 = await StartRecalculationAsync();
		var calc2 = await StartRecalculationAsync();
		var calc3 = await StartRecalculationAsync();

		calc1.ReleaseGate.TrySetResult(true);
		calc2.ReleaseGate.TrySetResult(true);
		calc3.ReleaseGate.TrySetResult(true);

		foreach (var task in new[] { calc1.Worker, calc2.Worker, calc3.Worker })
		{
			try
			{
				await task;
			}
			catch (OperationCanceledException)
			{
				// Expected for superseded calculations.
			}
		}

		Assert.Single(updates);
		Assert.Contains(calc3.Version, updates);
		currentCts?.Dispose();
	}

	[Fact]
	public async Task OperationCanceledException_HandledGracefully()
	{
		using var cts = new CancellationTokenSource();
		var handledGracefully = false;
		var releaseGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		var calc1 = releaseGate.Task.WaitAsync(cts.Token);
		var calc2 = releaseGate.Task.WaitAsync(cts.Token);

		cts.Cancel();

		try
		{
			await Task.WhenAll(calc1, calc2);
		}
		catch (OperationCanceledException)
		{
			handledGracefully = true;
		}

		Assert.True(handledGracefully);
	}

	[Fact]
	public void Cts_DisposalPattern_NoExceptions()
	{
		var exception = Record.Exception(() =>
		{
			CancellationTokenSource? cts = null;

			for (var i = 0; i < 5; i++)
			{
				cts?.Cancel();
				cts?.Dispose();
				cts = new CancellationTokenSource();
			}

			cts?.Cancel();
			cts?.Dispose();

			CancellationTokenSource? nullCts = null;
			nullCts?.Dispose();
		});

		Assert.Null(exception);
	}

	[Fact]
	public async Task CancelledInnerTasks_ExitGracefully()
	{
		using var cts = new CancellationTokenSource();
		var exitedGracefully = false;
		var innerTasksStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		var releaseGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

		var outerTask = Task.Run(async () =>
		{
			if (cts.Token.IsCancellationRequested)
				return;

			var innerTask1 = Task.Run(async () =>
			{
				innerTasksStarted.TrySetResult(true);
				await releaseGate.Task.WaitAsync(cts.Token);
			}, cts.Token);

			var innerTask2 = Task.Run(async () =>
			{
				await releaseGate.Task.WaitAsync(cts.Token);
			}, cts.Token);

			try
			{
				await Task.WhenAll(innerTask1, innerTask2);
			}
			catch (OperationCanceledException)
			{
				exitedGracefully = true;
			}
		}, cts.Token);

		await innerTasksStarted.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken: TestContext.Current.CancellationToken);
		cts.Cancel();

		try
		{
			await outerTask.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
		}
		catch (OperationCanceledException)
		{
			exitedGracefully = true;
		}
		finally
		{
			releaseGate.TrySetResult(true);
		}

		Assert.True(exitedGracefully || outerTask.IsCanceled);
	}

	[Fact]
	public async Task EarlyExit_WhenTokenAlreadyCancelled()
	{
		using var cts = new CancellationTokenSource();
		cts.Cancel();

		var heavyWorkExecuted = false;
		var task = Task.Run(() =>
		{
			if (cts.Token.IsCancellationRequested)
				return;

			heavyWorkExecuted = true;
		}, cts.Token);

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
		Assert.False(heavyWorkExecuted);
	}
}
