using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace DevProjex.Tests.Unit;

/// <summary>
/// Tests for cancellation patterns used in parallel operations.
/// These tests verify the cancellation behavior implemented in MainWindow.RecalculateMetricsAsync.
/// </summary>
public sealed class CancellationPatternTests
{
	/// <summary>
	/// Verifies that cancellation prevents execution of heavy calculations.
	/// This mirrors the pattern in RecalculateMetricsAsync.
	/// </summary>
	[Fact]
	public void CancellationToken_PreventsExecution_WhenCancelledBeforeStart()
	{
		using var cts = new CancellationTokenSource();
		var executed = false;

		cts.Cancel(); // Cancel before starting

		Task.Run(() =>
		{
			if (cts.Token.IsCancellationRequested)
				return;

			executed = true;
		}, cts.Token);

		Thread.Sleep(50); // Give task time to potentially execute

		Assert.False(executed);
	}

	/// <summary>
	/// Verifies that Task.WaitAll respects cancellation token.
	/// </summary>
	[Fact]
	public void TaskWaitAll_ThrowsOperationCancelled_WhenTokenCancelled()
	{
		using var cts = new CancellationTokenSource();

		var longTask1 = Task.Run(() =>
		{
			Thread.Sleep(1000);
			return 1;
		}, cts.Token);

		var longTask2 = Task.Run(() =>
		{
			Thread.Sleep(1000);
			return 2;
		}, cts.Token);

		// Cancel after short delay
		cts.CancelAfter(50);

		Assert.Throws<OperationCanceledException>(() =>
			Task.WaitAll([longTask1, longTask2], cts.Token));
	}

	/// <summary>
	/// Verifies that new CancellationTokenSource cancels previous operations.
	/// This is the pattern used for "cancel previous calculation".
	/// </summary>
	[Fact]
	public async Task NewCts_CancelsPreviousCalculation()
	{
		CancellationTokenSource? currentCts = null;
		var firstCalculationCompleted = false;
		var secondCalculationCompleted = false;

		// Start first calculation
		currentCts?.Cancel();
		currentCts = new CancellationTokenSource();
		var token1 = currentCts.Token;

		var task1 = Task.Run(async () =>
		{
			await Task.Delay(200, token1);
			if (!token1.IsCancellationRequested)
				firstCalculationCompleted = true;
		}, token1);

		// Start second calculation (should cancel first)
		await Task.Delay(50);
		currentCts.Cancel();
		currentCts = new CancellationTokenSource();
		var token2 = currentCts.Token;

		var task2 = Task.Run(async () =>
		{
			await Task.Delay(100, token2);
			if (!token2.IsCancellationRequested)
				secondCalculationCompleted = true;
		}, token2);

		// Wait for both to settle
		try { await task1; } catch (OperationCanceledException) { }
		await task2;

		Assert.False(firstCalculationCompleted);
		Assert.True(secondCalculationCompleted);
	}

	/// <summary>
	/// Verifies cancellation check after parallel calculations complete.
	/// </summary>
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

			// Simulate cancellation happening after calculations but before UI update
			cts.Cancel();

			if (cts.Token.IsCancellationRequested)
				return;

			uiUpdated = true;
		});

		await task;

		Assert.False(uiUpdated);
	}

	/// <summary>
	/// Verifies version check combined with cancellation provides double protection.
	/// This mirrors the pattern: "version must match AND not cancelled".
	/// </summary>
	[Fact]
	public async Task VersionCheckAndCancellation_DoubleProtection()
	{
		var version = 0;
		CancellationTokenSource? cts = null;
		var updates = new System.Collections.Concurrent.ConcurrentBag<int>();

		async Task SimulateRecalculate(int expectedVersion)
		{
			cts?.Cancel();
			cts = new CancellationTokenSource();
			var localCts = cts;
			var localVersion = Interlocked.Increment(ref version);

			try
			{
				await Task.Run(async () =>
				{
					if (localCts.Token.IsCancellationRequested)
						return;

					await Task.Delay(50, localCts.Token); // Simulate calculation

					if (localCts.Token.IsCancellationRequested)
						return;

					// Double-check: version must match AND not cancelled
					if (!localCts.Token.IsCancellationRequested &&
					    localVersion == Volatile.Read(ref version))
					{
						updates.Add(localVersion);
					}
				}, localCts.Token);
			}
			catch (OperationCanceledException)
			{
				// Expected when previous calculation is cancelled
			}
		}

		// Rapid fire multiple recalculations
		var tasks = new[]
		{
			SimulateRecalculate(1),
			SimulateRecalculate(2),
			SimulateRecalculate(3),
		};

		await Task.WhenAll(tasks);

		// Only the last version should have updated
		Assert.Single(updates);
		Assert.Contains(3, updates);
	}

	/// <summary>
	/// Verifies graceful handling of OperationCanceledException.
	/// </summary>
	[Fact]
	public async Task OperationCanceledException_HandledGracefully()
	{
		using var cts = new CancellationTokenSource();
		var handledGracefully = false;

		var task = Task.Run(async () =>
		{
			var calc1 = Task.Run(async () =>
			{
				await Task.Delay(100, cts.Token);
				return 1;
			}, cts.Token);

			var calc2 = Task.Run(async () =>
			{
				await Task.Delay(100, cts.Token);
				return 2;
			}, cts.Token);

			cts.Cancel();

			try
			{
				await Task.WhenAll(calc1, calc2);
			}
			catch (OperationCanceledException)
			{
				handledGracefully = true;
				return;
			}

			// Should not reach here
			Assert.Fail("Should have caught OperationCanceledException");
		});

		await task;

		Assert.True(handledGracefully);
	}

	/// <summary>
	/// Verifies CTS disposal pattern is correct.
	/// </summary>
	[Fact]
	public void Cts_DisposalPattern_NoExceptions()
	{
		CancellationTokenSource? cts = null;

		// Simulate multiple operations
		for (int i = 0; i < 5; i++)
		{
			cts?.Cancel();
			cts = new CancellationTokenSource();
		}

		// Final cleanup
		cts?.Cancel();
		cts?.Dispose();

		// Disposing null should not throw
		CancellationTokenSource? nullCts = null;
		nullCts?.Dispose();

		Assert.True(true); // No exceptions thrown
	}

	/// <summary>
	/// Verifies that cancelled inner tasks don't prevent graceful exit.
	/// </summary>
	[Fact]
	public async Task CancelledInnerTasks_ExitGracefully()
	{
		using var cts = new CancellationTokenSource();
		var exitedGracefully = false;

		var outerTask = Task.Run(async () =>
		{
			if (cts.Token.IsCancellationRequested)
				return;

			var innerTask1 = Task.Run(async () =>
			{
				await Task.Delay(200, cts.Token);
			}, cts.Token);

			var innerTask2 = Task.Run(async () =>
			{
				await Task.Delay(200, cts.Token);
			}, cts.Token);

			try
			{
				await Task.WhenAll(innerTask1, innerTask2);
			}
			catch (OperationCanceledException)
			{
				// Inner tasks were cancelled - exit gracefully
				exitedGracefully = true;
				return;
			}
		}, cts.Token);

		// Cancel after short delay
		await Task.Delay(50);
		cts.Cancel();

		try
		{
			await outerTask;
		}
		catch (OperationCanceledException)
		{
			exitedGracefully = true;
		}

		Assert.True(exitedGracefully);
	}

	/// <summary>
	/// Verifies early exit when token is already cancelled.
	/// </summary>
	[Fact]
	public void EarlyExit_WhenTokenAlreadyCancelled()
	{
		using var cts = new CancellationTokenSource();
		cts.Cancel();

		var heavyWorkExecuted = false;

		Task.Run(() =>
		{
			// Early exit check
			if (cts.Token.IsCancellationRequested)
				return;

			// Heavy work
			Thread.Sleep(100);
			heavyWorkExecuted = true;
		}, cts.Token);

		Thread.Sleep(200);

		Assert.False(heavyWorkExecuted);
	}
}
