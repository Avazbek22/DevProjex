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
		foreach (var tool in tools.Where(static tool => tool.Name is
			"get_tree" or "analyze" or "pack_context" or "search_project" or "related_files" or "get_file" or "get_diff"))
		{
			Assert.Contains("omit for one local root", tool.ProtocolTool.Description, StringComparison.Ordinal);
			Assert.InRange(tool.ProtocolTool.Description!.Length, 1, 800);
			Assert.InRange(tool.ProtocolTool.Description.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length, 40, 100);
			var schema = tool.ProtocolTool.InputSchema;
			Assert.Contains("Omit for one local root", schema.GetProperty("properties").GetProperty("project")
				.GetProperty("description").GetString(), StringComparison.Ordinal);
			if (schema.TryGetProperty("required", out var required))
				Assert.DoesNotContain(required.EnumerateArray(), static parameter => parameter.GetString() == "project");
		}
		var result = await server.Client.CallToolAsync("get_file", new Dictionary<string, object?> { ["path"] = "Anchor.txt" },
			progress: null, options: null, TestContext.Current.CancellationToken);
		Assert.NotEqual(true, result.IsError);
		Assert.Contains("discovery-free-marker", string.Join('\n', result.Content.OfType<TextContentBlock>().Select(static block => block.Text)),
			StringComparison.Ordinal);
	}
}
