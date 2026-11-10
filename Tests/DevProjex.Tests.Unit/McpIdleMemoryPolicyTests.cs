using DevProjex.Mcp;

namespace DevProjex.Tests.Unit;

[CollectionDefinition("MCP memory", DisableParallelization = true)]
public sealed class McpMemoryCollection;

[Collection("MCP memory")]
public sealed class McpIdleMemoryPolicyTests
{
	[Theory]
	[InlineData(0, 0, 0)]
	[InlineData(100, 0, 0)]
	[InlineData(101, 2, 1)]
	public async Task ReclamationRunsOnceAndOnlyAboveThreshold(long bytes, int collections, int resets)
	{
		var trigger = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var collected = 0;
		var reset = 0;
		await using var policy = new McpIdleMemoryPolicy(new()
		{
			Threshold = 100,
			PrivateBytes = () => bytes,
			Collect = () => collected++,
			Reset = () => reset++,
			Wait = (_, _, token) => trigger.Task.WaitAsync(token)
		});
		Assert.True(policy.Completion.IsCompleted);
		using (policy.EnterCall()) { Assert.Equal(0, collected); }
		trigger.SetResult();
		await policy.Completion;
		Assert.Equal(collections, collected);
		Assert.Equal(resets, reset);
		await policy.Completion;
		Assert.Equal(collections, collected);
	}

	[Fact]
	public async Task FirstCollectionCanAvoidResettingTheDetector()
	{
		var bytes = 101L;
		var resets = 0;
		await using var policy = new McpIdleMemoryPolicy(new()
		{
			Threshold = 100,
			PrivateBytes = () => bytes,
			Collect = () => bytes = 0,
			Reset = () => resets++,
			Wait = (_, _, _) => Task.CompletedTask
		});
		using (policy.EnterCall()) { }
		await policy.Completion;
		Assert.Equal(0, resets);
	}

	[Fact]
	public async Task AnIncomingCallCancelsPendingMaintenance()
	{
		var trigger = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var collected = 0;
		await using var policy = new McpIdleMemoryPolicy(new()
		{
			Threshold = 0,
			PrivateBytes = () => 1,
			Collect = () => collected++,
			Reset = () => { },
			Wait = (_, _, token) => trigger.Task.WaitAsync(token)
		});
		using (policy.EnterCall()) { }
		var abandoned = policy.Completion;
		using (policy.EnterCall())
		{
			trigger.SetResult();
			await abandoned;
			Assert.Equal(0, collected);
		}
		await policy.Completion;
		Assert.Equal(2, collected);
	}

	[Fact]
	public async Task BackgroundActivityDelaysMaintenance()
	{
		var trigger = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var collected = 0;
		await using var policy = new McpIdleMemoryPolicy(new()
		{
			Threshold = 0,
			PrivateBytes = () => 1,
			Collect = () => collected++,
			Reset = () => { },
			Wait = (_, _, token) => trigger.Task.WaitAsync(token)
		});
		using (policy.EnterCall()) { }
		using (McpMemoryActivity.Enter())
		{
			trigger.SetResult();
			Assert.Equal(0, collected);
		}
		await policy.Completion;
		Assert.Equal(2, collected);
	}

	[Fact]
	public async Task IncomingCallCancelsMaintenanceWaitingForBackgroundWork()
	{
		var collected = 0;
		await using var policy = new McpIdleMemoryPolicy(new()
		{
			Threshold = 0,
			PrivateBytes = () => 1,
			Collect = () => collected++,
			Reset = () => { },
			Wait = (_, _, _) => Task.CompletedTask
		});
		using (McpMemoryActivity.Enter())
		{
			using (policy.EnterCall()) { }
			var abandoned = policy.Completion;
			Assert.False(abandoned.IsCompleted);
			using (policy.EnterCall())
			{
				await abandoned;
				Assert.Equal(0, collected);
			}
		}
		await policy.Completion;
		Assert.Equal(2, collected);
	}

	[Fact]
	public async Task OverlappingCallsScheduleOnlyAfterTheLastLeaseAndDisposeIsIdempotent()
	{
		var waits = 0;
		var collections = 0;
		var trigger = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		await using var policy = new McpIdleMemoryPolicy(new()
		{
			Threshold = 0,
			PrivateBytes = () => 1,
			Collect = () => collections++,
			Reset = () => { },
			Wait = (_, _, token) =>
			{
				waits++;
				return trigger.Task.WaitAsync(token);
			}
		});
		using var first = policy.EnterCall();
		using var second = policy.EnterCall();
		try
		{
			first.Dispose();
			first.Dispose();
			Assert.Equal(0, waits);
			Assert.Equal(0, collections);
			second.Dispose();
			Assert.Equal(1, waits);
			trigger.SetResult();
			await policy.Completion;
			second.Dispose();
			Assert.Equal(1, waits);
			Assert.Equal(2, collections);
		}
		finally
		{
			trigger.TrySetResult();
		}
	}

	[Theory]
	[InlineData("memory")]
	[InlineData("collect")]
	[InlineData("reset")]
	public async Task FailedMaintenanceReleasesExclusivityAndTheNextIdlePeriodRetries(string stage)
	{
		var fail = true;
		var collections = 0;
		var resets = 0;
		await using var policy = new McpIdleMemoryPolicy(new()
		{
			Threshold = 0,
			PrivateBytes = () => fail && stage == "memory" ? throw new IOException("memory unavailable") : 1,
			Collect = () =>
			{
				if (fail && stage == "collect")
					throw new InvalidOperationException("collection failed");
				collections++;
			},
			Reset = () =>
			{
				if (fail && stage == "reset")
					throw new InvalidOperationException("reset failed");
				resets++;
			},
			Wait = (_, _, _) => Task.CompletedTask
		});
		using (policy.EnterCall()) { }
		await policy.Completion;
		fail = false;
		collections = resets = 0;
		using (policy.EnterCall()) { }
		await policy.Completion;
		Assert.Equal(2, collections);
		Assert.Equal(1, resets);
	}

	[Fact]
	public async Task DisposalCancelsAndDrainsPendingMaintenanceWithoutCollecting()
	{
		var trigger = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var collections = 0;
		var policy = new McpIdleMemoryPolicy(new()
		{
			PrivateBytes = () => long.MaxValue,
			Collect = () => collections++,
			Wait = (_, _, token) => trigger.Task.WaitAsync(token)
		});
		using (policy.EnterCall()) { }
		var pending = policy.Completion;
		await policy.DisposeAsync();
		await pending;
		trigger.SetResult();
		Assert.Equal(0, collections);
		Assert.Throws<ObjectDisposedException>(() => policy.EnterCall());
		await policy.DisposeAsync();
	}
}
