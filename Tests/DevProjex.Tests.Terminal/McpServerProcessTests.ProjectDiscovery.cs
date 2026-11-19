using ModelContextProtocol.Protocol;

namespace DevProjex.Tests.Terminal;

public sealed partial class McpServerProcessTests
{
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task RealProcessProjectMetadataDoesNotRequireDiscoveryForOneLocalRoot(bool reduced)
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/Anchor.txt", "discovery-free-marker\n");
		await using var server = await ActualMcpProcess.StartAsync(project,
			workspace.CreateDirectory("data"), reduced ? ["--tool-set", "reduced"] : []);
		var tools = await server.Client.ListToolsAsync(options: null, TestContext.Current.CancellationToken);
		foreach (var tool in tools)
		{
			Assert.DoesNotContain("Project:", tool.ProtocolTool.Description, StringComparison.Ordinal);
			Assert.InRange(tool.ProtocolTool.Description!.Length, 1, 800);
			Assert.InRange(tool.ProtocolTool.Description.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length, 40, 100);
			var properties = tool.ProtocolTool.InputSchema.GetProperty("properties");
			Assert.False(properties.TryGetProperty("project", out _));
			Assert.False(properties.TryGetProperty("branch", out _));
		}
		foreach (var alias in new[] { project, "project", "#1", "#0" })
		{
			var explicitResult = await server.Client.CallToolAsync("get_file",
				new Dictionary<string, object?> { ["path"] = "Anchor.txt", ["project"] = alias },
				progress: null, options: null, TestContext.Current.CancellationToken);
			Assert.NotEqual(true, explicitResult.IsError);
			Assert.Contains("discovery-free-marker", string.Join('\n', explicitResult.Content.OfType<TextContentBlock>().Select(static block => block.Text)), StringComparison.Ordinal);
		}
		var result = await server.Client.CallToolAsync("get_file", new Dictionary<string, object?> { ["path"] = "Anchor.txt" },
			progress: null, options: null, TestContext.Current.CancellationToken);
		Assert.NotEqual(true, result.IsError);
		Assert.Contains("discovery-free-marker", string.Join('\n', result.Content.OfType<TextContentBlock>().Select(static block => block.Text)),
			StringComparison.Ordinal);
	}
}
