using DevProjex.Mcp;
using ModelContextProtocol.Protocol;

namespace DevProjex.Tests.Integration;

public sealed partial class McpServerIntegrationTests
{
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
		await using (var server = await McpTestServer.StartAsync(project, workspace.Path, idleMemoryOptions: options, configureServices: created => services = created))
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
	[Theory]
	[InlineData("\n")]
	[InlineData("\r\n")]
	[InlineData("\r")]
	public async Task GetFileCodeOutlineUsesTheSameLinesForEveryLineEnding(string ending)
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		var lines = Enumerable.Range(0, 400).Select(index => $"public class Member{index} {{ }} // " + new string('x', 70));
		File.WriteAllText(Path.Combine(project, "Big.cs"), string.Join(ending, lines) + ending);
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);
		var text = Text(await server.CallAsync("get_file", new Dictionary<string, object?> { ["path"] = "Big.cs" }));
		Assert.Contains("Structure:", text, StringComparison.Ordinal);
		Assert.Contains("2-2 Member1 (type)", text, StringComparison.Ordinal);
		Assert.Contains("of 401", text, StringComparison.Ordinal);
	}
}
