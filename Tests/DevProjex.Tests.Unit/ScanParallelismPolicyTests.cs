namespace DevProjex.Tests.Unit;

public sealed class ScanParallelismPolicyTests
{
	[Fact]
	public void PartitionedParallelismNeverExceedsTheGlobalBudget()
	{
		var global = ScanParallelismPolicy.MaxDegreeOfParallelism;

		for (var requestedPartitions = 1; requestedPartitions <= global * 2; requestedPartitions++)
		{
			var activePartitions = Math.Min(requestedPartitions, global);
			var perPartition = ScanParallelismPolicy.PartitionDegreeOfParallelism(requestedPartitions);

			Assert.InRange(perPartition, 1, global);
			Assert.True(activePartitions * perPartition <= global);
		}
	}

	[Fact]
	public void CreateOptionsClampsAnExplicitBudgetToTheGlobalLimit()
	{
		var options = ScanParallelismPolicy.CreateOptions(
			TestContext.Current.CancellationToken,
			maximumDegreeOfParallelism: int.MaxValue);

		Assert.Equal(ScanParallelismPolicy.MaxDegreeOfParallelism, options.MaxDegreeOfParallelism);
	}
}
