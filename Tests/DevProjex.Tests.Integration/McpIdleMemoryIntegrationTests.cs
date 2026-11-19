using DevProjex.Mcp;
using ModelContextProtocol.Protocol;

namespace DevProjex.Tests.Integration;

[CollectionDefinition("MCP idle memory", DisableParallelization = true)]
public sealed class McpIdleMemoryCollection;

[Collection("MCP idle memory")]
public sealed class McpIdleMemoryIntegrationTests
{
	private const string Secret = "ghp_" + "a7D9mQ2xK4vN8sR6tY3uW5zB1cE0fG2hJ9pL";
	[Fact]
	public async Task IdleResetPreservesCachedProtectionAndProtectsChangedContentAndMetadata()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		File.WriteAllText(Path.Combine(project, "main.txt"), "token = \"" + Secret + "\"\n");
		var trigger = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var collections = 0;
		var resets = 0;
		McpServices? services = null;
		var options = new McpIdleMemoryOptions
		{
			PrivateBytes = () => long.MaxValue,
			Wait = (_, _, token) => Volatile.Read(ref resets) == 0
				? trigger.Task.WaitAsync(token) : Task.Delay(Timeout.InfiniteTimeSpan, token),
			Reset = () => { McpSecretDetectors.Reset(); Interlocked.Increment(ref resets); },
			Collect = () => { if (Interlocked.Increment(ref collections) == 2) completed.SetResult(); }
		};
		await using (var server = await McpServerIntegrationTests.McpTestServer.StartAsync(project, workspace.Path, idleMemoryOptions: options, configureServices: created => services = created))
		{
			async Task Check(string tool, Dictionary<string, object?>? arguments = null)
			{
				var response = await server.CallAsync(tool, arguments);
				var text = string.Join("\n", response.Content.OfType<TextContentBlock>().Select(block => block.Text));
				Assert.DoesNotContain(Secret, text, StringComparison.Ordinal);
			}
			await Check("list_projects");
			await Check("get_file", new() { ["path"] = Secret + ".missing" });
			await Check("get_file", new() { ["path"] = "main.txt" });
			trigger.SetResult();
			await completed.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
			Assert.Equal(1, resets);
			Assert.NotNull(services);
			var runs = services.RedactionSession.GetCacheDiagnostics().DetectionRuns;
			await Check("get_file", new() { ["path"] = "main.txt" });
			Assert.Equal(runs, services.RedactionSession.GetCacheDiagnostics().DetectionRuns);
			File.AppendAllText(Path.Combine(project, "main.txt"), "other = \"" + Secret + "\"\n");
			await Check("get_file", new() { ["path"] = "main.txt" });
			Assert.True(services.RedactionSession.GetCacheDiagnostics().DetectionRuns > runs);
			await Check("list_projects");
			await Check("get_file", new() { ["path"] = Secret + ".missing" });
		}
		foreach (var journal in Directory.EnumerateFiles(workspace.Path, "*.jsonl", SearchOption.AllDirectories))
			Assert.DoesNotContain(Secret, await File.ReadAllTextAsync(journal, TestContext.Current.CancellationToken), StringComparison.Ordinal);
	}
}
