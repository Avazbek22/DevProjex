namespace DevProjex.Tests.Terminal;

public sealed partial class McpServerProcessTests
{
	[Theory]
	[InlineData("full", false, false)]
	[InlineData("full", false, true)]
	[InlineData("full", true, false)]
	[InlineData("full", true, true)]
	[InlineData("reduced", false, false)]
	[InlineData("reduced", false, true)]
	[InlineData("reduced", true, false)]
	[InlineData("reduced", true, true)]
	public async Task RealProcessGetDiffPublishesTheSharedSelectionSchemas(string toolSet, bool live, bool delegated)
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/App.cs", "class App;\n");
		var arguments = new List<string> { "--tool-set", toolSet };
		if (live)
			arguments.Add("--live");
		if (delegated)
			arguments.Add("--allow-agent-exclusions");
		await using var server = await ActualMcpProcess.StartAsync(project, workspace.CreateDirectory("data"), arguments);

		var tools = await server.Client.ListToolsAsync(options: null, TestContext.Current.CancellationToken);
		var diff = tools.Single(tool => tool.Name == "get_diff").ProtocolTool.InputSchema;
		var properties = diff.GetProperty("properties");
		foreach (var (argument, neighbour) in new[]
		{
			("profile", "get_file"),
			("tracked_only", "get_tree"),
			("max_file_bytes", "get_tree")
		})
		{
			var shared = tools.Single(tool => tool.Name == neighbour).ProtocolTool.InputSchema
				.GetProperty("properties").GetProperty(argument);
			Assert.Equal(shared.GetRawText(), properties.GetProperty(argument).GetRawText());
		}
		Assert.Equal(delegated, properties.TryGetProperty("exclusions", out var exclusions));
		if (delegated)
		{
			var shared = tools.Single(tool => tool.Name == "get_tree").ProtocolTool.InputSchema
				.GetProperty("properties").GetProperty("exclusions");
			Assert.Equal(shared.GetRawText(), exclusions.GetRawText());
		}
		Assert.Equal("changes", properties.GetProperty("git_scope").GetProperty("default").GetString());
		Assert.False(diff.GetProperty("additionalProperties").GetBoolean());
	}
}
