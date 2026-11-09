using DevProjex.Mcp;

namespace DevProjex.Tests.Integration;

public sealed class McpTransportLifetimeIntegrationTests
{
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task ClosedOrFailedInputCompletesHostCleanup(bool failed)
	{
		using var directory = new TemporaryDirectory();
		var root = directory.CreateDirectory("project");
		await using var input = failed ? new FailedInput() : new MemoryStream();
		await McpServerHost.RunWithStreamsAsync([root], input, Stream.Null,
			cancellationToken: TestContext.Current.CancellationToken,
			appDataPathProvider: () => Path.Combine(directory.Path, "data"),
			tempRoot: Path.Combine(directory.Path, "temp"))
			.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
	}

	private sealed class FailedInput : MemoryStream
	{
		public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
			ValueTask.FromException<int>(new IOException("Controlled input failure."));
		public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
			Task.FromException<int>(new IOException("Controlled input failure."));
	}
}
