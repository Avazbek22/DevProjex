using ModelContextProtocol.Protocol;

namespace DevProjex.Tests.Terminal;

public sealed partial class McpServerProcessTests
{
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task RealProcessPublishesAddressParametersForItsStartupRoots(bool remote)
	{
		using var workspace = new TemporaryDirectory();
		var first = workspace.CreateDirectory("first");
		var second = workspace.CreateDirectory("second");
		workspace.WriteFile("first/Anchor.txt", "first-marker\n");
		workspace.WriteFile("second/Anchor.txt", "second-marker\n");
		await using var server = await ActualMcpProcess.StartAsync(first, workspace.CreateDirectory("data"),
			remote ? ["--allow-remote"] : ["--root", second]);
		var tools = await server.Client.ListToolsAsync(options: null, TestContext.Current.CancellationToken);
		foreach (var tool in tools)
		{
			var addressed = tool.Name is not ("list_projects" or "read_pack");
			var properties = tool.ProtocolTool.InputSchema.GetProperty("properties");
			Assert.Equal(addressed, properties.TryGetProperty("project", out _));
			Assert.Equal(addressed && remote, properties.TryGetProperty("branch", out _));
			if (addressed)
				Assert.EndsWith(" Project: omit for one local root; otherwise unique name, absolute path, or #index.",
					tool.ProtocolTool.Description, StringComparison.Ordinal);
		}
		if (!remote)
		{
			var missing = await server.Client.CallToolAsync("get_file", new Dictionary<string, object?> { ["path"] = "Anchor.txt" },
				progress: null, options: null, TestContext.Current.CancellationToken);
			Assert.True(missing.IsError);
			Assert.Equal("DPX-MCP-UNKNOWN-PROJECT: 'project' is required because multiple roots are available. Call list_projects and use a listed project index: #1, #2.",
				string.Join('\n', missing.Content.OfType<TextContentBlock>().Select(static block => block.Text)));
			var selected = await server.Client.CallToolAsync("get_file",
				new Dictionary<string, object?> { ["path"] = "Anchor.txt", ["project"] = "#2" },
				progress: null, options: null, TestContext.Current.CancellationToken);
			Assert.NotEqual(true, selected.IsError);
			Assert.Contains("second-marker", string.Join('\n', selected.Content.OfType<TextContentBlock>().Select(static block => block.Text)), StringComparison.Ordinal);
		}
		var branch = await server.Client.CallToolAsync("get_file",
			new Dictionary<string, object?> { ["path"] = "Anchor.txt", ["project"] = "#1", ["branch"] = "main" },
			progress: null, options: null, TestContext.Current.CancellationToken);
		Assert.True(branch.IsError);
		Assert.Equal("DPX-MCP-INVALID-ARGUMENTS: 'branch' is valid only when 'project' is a Git URL. To compare a local project with a commit, call get_diff with git_scope \"changes\", \"staged\" or \"diff:<A>..<B>\".",
			string.Join('\n', branch.Content.OfType<TextContentBlock>().Select(static block => block.Text)));
	}
}
