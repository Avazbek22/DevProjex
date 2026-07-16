using DevProjex.Infrastructure.RecentProjects;

namespace DevProjex.Tests.Unit;

public sealed class RecentFolderAvailabilityServiceTests
{
	[Fact]
	public async Task CheckAsync_ReturnsAvailabilityForEveryDistinctPath()
	{
		var service = new RecentFolderAvailabilityService(path => path.EndsWith("available", StringComparison.Ordinal));

		var result = await service.CheckAsync(
			["c:/available", "c:/missing", "c:/available"],
			TestContext.Current.CancellationToken);

		Assert.Equal(2, result.Count);
		Assert.True(result["c:/available"]);
		Assert.False(result["c:/missing"]);
	}

	[Fact]
	public async Task IsAvailableAsync_RechecksAndRecoversWhenFolderReturns()
	{
		var isConnected = false;
		var service = new RecentFolderAvailabilityService(_ => isConnected);

		Assert.False(await service.IsAvailableAsync("network/project", TestContext.Current.CancellationToken));

		isConnected = true;

		Assert.True(await service.IsAvailableAsync("network/project", TestContext.Current.CancellationToken));
	}

	[Fact]
	public async Task CheckAsync_ProbeFailure_IsReportedAsUnavailableWithoutFailingBatch()
	{
		var service = new RecentFolderAvailabilityService(path => path switch
		{
			"throw" => throw new IOException("Unavailable volume."),
			_ => true
		});

		var result = await service.CheckAsync(
			["throw", "available"],
			TestContext.Current.CancellationToken);

		Assert.False(result["throw"]);
		Assert.True(result["available"]);
	}

	[Fact]
	public async Task CheckAsync_PreCanceledRequest_DoesNotStartFilesystemProbe()
	{
		var probeCount = 0;
		var service = new RecentFolderAvailabilityService(_ =>
		{
			Interlocked.Increment(ref probeCount);
			return true;
		});
		using var cancellation = new CancellationTokenSource();
		await cancellation.CancelAsync();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(
			() => service.CheckAsync(["c:/project"], cancellation.Token));
		Assert.Equal(0, Volatile.Read(ref probeCount));
	}
}
