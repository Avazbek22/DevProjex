using System.Security.Cryptography;
using System.Text;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace DevProjex.Tests.Terminal;

public sealed partial class McpServerProcessTests
{
	[Theory]
	[InlineData(false, false, false)]
	[InlineData(true, false, false)]
	[InlineData(false, true, false)]
	[InlineData(false, false, true)]
	public async Task RealProcessDefaultCatalogPublishesOnlyFocusedParameters(bool multiple, bool remote, bool exclusions)
	{
		using var workspace = new TemporaryDirectory();
		var root = workspace.CreateDirectory("first");
		workspace.WriteFile("first/Anchor.txt", "marker\n");
		var arguments = new List<string>();
		if (multiple) arguments.AddRange(["--root", workspace.CreateDirectory("second")]);
		if (remote) arguments.Add("--allow-remote");
		if (exclusions) arguments.Add("--allow-agent-exclusions");
		await using var server = await ActualMcpProcess.StartAsync(root, workspace.CreateDirectory("data"), arguments,
			useDefaultToolSet: true);
		var tools = await server.Client.ListToolsAsync(options: null, TestContext.Current.CancellationToken);
		Assert.Equal(multiple || remote ? new[] { "list_projects" }.Concat(ReducedTools) : ReducedTools,
			tools.Select(static tool => tool.Name));
		var expected = new Dictionary<string, string[]>
		{
			["list_projects"] = [],
			["get_tree"] = ["paths", "include_patterns", "exclude_patterns", "max_depth", "format"],
			["search_project"] = ["pattern", "paths", "include_patterns", "exclude_patterns", "max_file_bytes", "context_lines", "ignore_case", "symbols", "max_results"],
			["get_file"] = ["path", "paths", "requests", "start_line", "end_line", "start_column", "symbol"],
			["related_files"] = ["path", "direction", "include_patterns"],
			["get_diff"] = ["git_scope", "paths", "include_patterns", "context_lines"],
			["read_pack"] = ["pack_id", "start_line", "end_line", "start_column"]
		};
		foreach (var tool in tools)
		{
			var actual = tool.ProtocolTool.InputSchema.GetProperty("properties").EnumerateObject().Select(static p => p.Name).ToArray();
			Assert.Equal(expected[tool.Name], actual.Where(static p => p is not ("project" or "branch" or "exclusions")));
			var addressed = tool.Name is not ("list_projects" or "read_pack");
			Assert.Equal(addressed && (multiple || remote), actual.Contains("project"));
			Assert.Equal(addressed && remote, actual.Contains("branch"));
			Assert.Equal(addressed && exclusions, actual.Contains("exclusions"));
		}
		if (multiple || remote)
			Assert.NotEqual(true, (await CallAsync(server, "list_projects", [])).IsError);
		else
		{
			foreach (var hidden in new[] { "list_projects", "analyze", "pack_context" })
			{
				var error = await Assert.ThrowsAsync<McpProtocolException>(() => server.Client.CallToolAsync(hidden,
					new Dictionary<string, object?>(), progress: null, options: null, TestContext.Current.CancellationToken).AsTask());
				Assert.Equal(-32602, (int)error.ErrorCode);
				Assert.Contains(hidden, error.Message, StringComparison.Ordinal);
			}
		}
	}

	[Fact]
	public async Task RealProcessPublishesTheFullToolSetUnchanged()
	{
		using var workspace = new TemporaryDirectory();
		var root = workspace.CreateDirectory("project");
		var (process, client, error, output) = await StartPublishedMcpAsync(workspace, root, ["--tool-set", "full"]);
		try
		{
			await client.ListToolsAsync(options: null, TestContext.Current.CancellationToken);
			var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(ReadToolsListResult(output.GetRecordedText()))));
			Assert.Equal("5f002f57c051cd506a6ec1ef0e14bd694d25dafa16001e1cb58917fd93b5a988", hash);
		}
		finally { process.StandardInput.Close(); await client.DisposeAsync(); }
		await CompletePublishedMcpAsync(process, error, output);
	}

}
