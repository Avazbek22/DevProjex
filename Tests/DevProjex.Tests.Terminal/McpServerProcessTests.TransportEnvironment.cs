using ModelContextProtocol.Protocol;

namespace DevProjex.Tests.Terminal;

public sealed partial class McpServerProcessTests
{
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task RealProcessEnvironmentCannotAdmitAFileUrlOrAnOutsideProject(bool allowRemote)
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		var outside = workspace.CreateDirectory("outside.git");
		workspace.WriteFile("project/Visible.txt", "inside-control-marker\n");
		workspace.WriteFile("outside.git/Hidden.txt", "outside-content-marker\n");
		await using var server = await ActualMcpProcess.StartAsync(project,
			workspace.CreateDirectory("data"), allowRemote ? ["--allow-remote"] : [],
			environment: new Dictionary<string, string>
			{
				["DEVPROJEX_INTERNAL_TEST_ALLOW_FILE_GIT"] = "1",
				["DEVPROJEX_TEST_HOST_ALLOW_FILE_GIT"] = "1",
				["CLAUDE_PROJECT_DIR"] = outside
			});
		var control = await CallAsync(server, "get_file", new Dictionary<string, object?> { ["path"] = "Visible.txt" });
		Assert.NotEqual(true, control.IsError);
		Assert.Contains("inside-control-marker", AllProcessText(control), StringComparison.Ordinal);
		foreach (var tool in new[] { "get_tree", "analyze", "search_project", "get_file", "pack_context", "related_files" })
		{
			var arguments = new Dictionary<string, object?>
			{
				["project"] = new Uri(outside + Path.DirectorySeparatorChar).AbsoluteUri
			};
			if (tool is "get_file" or "related_files")
				arguments["path"] = "Hidden.txt";
			if (tool == "search_project")
				arguments["pattern"] = "outside-content-marker";
			var result = await CallAsync(server, tool, arguments);
			Assert.True(result.IsError);
			var text = AllProcessText(result);
			Assert.StartsWith(allowRemote ? "DPX-MCP-INVALID-ARGUMENTS:" : "DPX-MCP-REMOTE-DISABLED:",
				text, StringComparison.Ordinal);
			Assert.DoesNotContain("outside-content-marker", text, StringComparison.Ordinal);
		}
		var escaped = await CallAsync(server, "get_file", new Dictionary<string, object?>
		{
			["path"] = Path.Combine(outside, "Hidden.txt")
		});
		Assert.True(escaped.IsError);
		Assert.StartsWith("DPX-MCP-ROOT-VIOLATION:", AllProcessText(escaped), StringComparison.Ordinal);
		Assert.DoesNotContain("outside-content-marker", AllProcessText(escaped), StringComparison.Ordinal);
		var listed = await CallAsync(server, "list_projects", new Dictionary<string, object?>());
		Assert.NotEqual(true, listed.IsError);
		var body = ExtractSpotlightBody(AllProcessText(listed));
		Assert.DoesNotContain("outside.git", body, StringComparison.Ordinal);
	}
}
