using DevProjex.Mcp;
using DevProjex.Application.Dependencies;
using DevProjex.Infrastructure.Dependencies;
using ModelContextProtocol;

namespace DevProjex.Tests.Integration;

public sealed class McpTransportLifetimeIntegrationTests
{
	[Theory]
	[InlineData(false, 0)]
	[InlineData(false, 2)]
	[InlineData(true, 2)]
	public async Task ClosingAnEstablishedTransportDrainsBlockedExtractionAndRecordsCancellation(bool failed, int queued)
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		File.WriteAllText(Path.Combine(project, "Model.cs"), "public class Model {}\n");
		using var extractor = new BlockedExtractor();
		using var engine = new DependencyFactsEngine(extractor, new FileDependencyConfigurationProvider());
		await using var server = await McpServerIntegrationTests.McpTestServer.StartAsync(project, workspace.Path, dependencyFactsEngine: engine);
		try
		{
			var pending = new List<Task<ModelContextProtocol.Protocol.CallToolResult>>
			{
				server.CallAsync("related_files", new Dictionary<string, object?> { ["path"] = "Model.cs" })
			};
			await extractor.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
			for (var index = 0; index < queued; index++)
				pending.Add(server.CallAsync("get_tree"));
			Assert.False(extractor.Exited.Task.IsCompleted);
			await server.CompleteInputAsync(failed ? new IOException("Controlled connection loss.") : null);
			await server.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
			Assert.True(extractor.Exited.Task.IsCompletedSuccessfully);
			await server.Client.DisposeAsync();
			foreach (var call in pending)
			{
				var exception = await Record.ExceptionAsync(async () => await call);
				Assert.True(exception is McpException or OperationCanceledException or IOException, exception?.ToString() ?? "Call unexpectedly succeeded.");
			}
			var records = Directory.EnumerateFiles(workspace.Path, "*.jsonl", SearchOption.AllDirectories)
				.SelectMany(File.ReadLines).Select(line => JsonDocument.Parse(line)).ToArray();
			try
			{
				Assert.Contains(records, record => record.RootElement.GetProperty("type").GetString() == "end");
				Assert.Contains(records, record => record.RootElement.GetProperty("type").GetString() == "call" &&
					record.RootElement.GetProperty("call").GetProperty("tool").GetString() == "related_files" &&
					record.RootElement.GetProperty("call").TryGetProperty("outcome", out var outcome) && outcome.GetString() == "cancelled");
			}
			finally
			{
				foreach (var record in records)
					record.Dispose();
			}
		}
		finally
		{
			extractor.Release();
		}
	}

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

	private sealed class BlockedExtractor : IDependencyFactExtractor
	{
		private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource Exited { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public int ParseCount => 0;
		public int CompiledQuerySetCount => 0;

		public async ValueTask<PreparedDependencySource> PrepareAsync(string projectRoot, string fullPath,
			DependencyResolverConfiguration configuration, DependencyFactsLimits limits, CancellationToken cancellationToken,
			string? contentIdentity = null)
		{
			Entered.TrySetResult();
			try
			{
				await release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
				throw new InvalidOperationException("Extraction was not cancelled.");
			}
			finally
			{
				Exited.TrySetResult();
			}
		}

		public FileFacts Extract(PreparedDependencySource source, DependencyFactsLimits limits) =>
			throw new InvalidOperationException("A cancelled preparation cannot be extracted.");

		public void Release() => release.TrySetResult();
		public void Dispose() => Release();
	}
}
