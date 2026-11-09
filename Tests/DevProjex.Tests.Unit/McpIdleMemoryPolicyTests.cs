using DevProjex.Mcp;

namespace DevProjex.Tests.Unit;

[CollectionDefinition("MCP memory", DisableParallelization = true)]
public sealed class McpMemoryCollection;

[Collection("MCP memory")]
public sealed class McpIdleMemoryPolicyTests
{
	[Theory]
	[InlineData(0, 0, 0)]
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
}
