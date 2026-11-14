using System.Diagnostics;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace DevProjex.Tests.Terminal;

public sealed partial class McpServerProcessTests
{
	[Fact]
	public async Task RealProcessExitsCleanlyWhenIdleInputCloses()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("idle-project");
		await using var server = await ActualMcpProcess.StartAsync(project, workspace.CreateDirectory("data"));
		server.ChildProcess.StandardInput.Close();
		await server.ChildProcess.WaitForExitAsync(TestContext.Current.CancellationToken)
			.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
		Assert.Equal(0, server.ChildProcess.ExitCode);
	}

	[Theory]
	[InlineData(0)]
	[InlineData(3)]
	public async Task RealProcessCancelsInFlightAndQueuedCallsBeforeClosingTheJournal(int queued)
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("busy-project");
		var dataRoot = workspace.CreateDirectory("data");
		var temporaryRoot = workspace.CreateDirectory("temp");
		var source = string.Concat(Enumerable.Repeat("public class Example { public string Value = \"ordinary text\"; }\n", 1024));
		for (var index = 0; index < 256; index++) workspace.WriteFile($"busy-project/file{index:D3}.cs", source);
		await using var server = await ActualMcpProcess.StartAsync(project, dataRoot,
			environment: new Dictionary<string, string> { ["TEMP"] = temporaryRoot, ["TMP"] = temporaryRoot, ["TMPDIR"] = temporaryRoot });
		var calls = new List<Task<CallToolResult>>
		{
			server.Client.CallToolAsync("pack_context", new Dictionary<string, object?> { ["view"] = "content" },
				progress: null, options: null, TestContext.Current.CancellationToken).AsTask()
		};
		for (var index = 0; index < queued; index++)
			calls.Add(server.Client.CallToolAsync("get_tree", new Dictionary<string, object?>(),
				progress: null, options: null, TestContext.Current.CancellationToken).AsTask());
		await server.Client.ListToolsAsync(options: null, TestContext.Current.CancellationToken);
		Assert.False(calls[0].IsCompleted);
		server.ChildProcess.StandardInput.Close();
		await server.ChildProcess.WaitForExitAsync(TestContext.Current.CancellationToken)
			.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
		Assert.Equal(0, server.ChildProcess.ExitCode);
		await server.Client.DisposeAsync();
		foreach (var call in calls)
		{
			try { await call; }
			catch (McpException) { }
			catch (OperationCanceledException) { }
			catch (IOException) { }
		}
		Assert.Empty(Directory.EnumerateFiles(temporaryRoot, "*.pack", SearchOption.AllDirectories));
		var records = Directory.EnumerateFiles(dataRoot, "*.jsonl", SearchOption.AllDirectories)
			.SelectMany(File.ReadAllLines).Select(line => JsonDocument.Parse(line)).ToArray();
		try
		{
			Assert.Contains(records, record => record.RootElement.GetProperty("type").GetString() == "end");
			Assert.Contains(records, record => record.RootElement.GetProperty("type").GetString() == "call" &&
				record.RootElement.GetProperty("call").TryGetProperty("outcome", out var outcome) && outcome.GetString() == "cancelled");
		}
		finally { foreach (var record in records) record.Dispose(); }
	}
}
