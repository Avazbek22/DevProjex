using ModelContextProtocol.Protocol;

namespace DevProjex.Tests.Terminal;

public sealed partial class McpServerProcessTests
{
	[Fact]
	public async Task RealProcessEnforcesEveryBoundRemovedFromTheInputCatalog()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/Anchor.ts", "export function small() { return 2; }\n");
		await using var server = await ActualMcpProcess.StartAsync(project, workspace.CreateDirectory("data"),
			["--allow-agent-exclusions", "--allow-remote"]);
		var fixture = Path.Combine(PublishedApplicationLocator.FindRepositoryRoot(), "Tests", "Fixtures", "Mcp", "catalog-limits.json");
		using var cases = JsonDocument.Parse(await File.ReadAllTextAsync(fixture, TestContext.Current.CancellationToken));
		Assert.Equal(185, cases.RootElement.GetArrayLength());
		foreach (var item in cases.RootElement.EnumerateArray())
		{
			var id = item.GetProperty("id").GetString();
			TestContext.Current.TestOutputHelper?.WriteLine(id);
			var arguments = item.GetProperty("arguments").EnumerateObject()
				.ToDictionary(static property => property.Name, static property => (object?)property.Value.Clone());
			var result = await server.Client.CallToolAsync(item.GetProperty("tool").GetString()!, arguments,
				progress: null, options: null, TestContext.Current.CancellationToken);
			Assert.True(result.IsError, id);
			Assert.Equal(item.GetProperty("expected").GetString(),
				System.Text.RegularExpressions.Regex.Replace(
					string.Join('\n', result.Content.OfType<TextContentBlock>().Select(static block => block.Text)),
					"untrusted-data-[0-9a-f]{24}", "untrusted-data-NONCE",
					System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2)));
		}
	}

	[Fact]
	public async Task RealProcessKeepsSchemaOnlyBoundsWithoutAddingServerValidation()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		var longName = new string('L', 513);
		workspace.WriteFile("project/Anchor.ts", $"export function {longName}() {{ return 1; }}\n");
		InitializeIsolatedRepository(project);
		RunGit(project, "add", "Anchor.ts");
		RunGit(project, "commit", "--quiet", "-m", "baseline");
		workspace.WriteFile("project/Anchor.ts", $"export function {longName}() {{ return 2; }}\n");
		await using var server = await ActualMcpProcess.StartAsync(project, workspace.CreateDirectory("data"));
		var tools = await server.Client.ListToolsAsync(options: null, TestContext.Current.CancellationToken);
		foreach (var name in new[] { "analyze", "pack_context", "get_file", "related_files", "get_diff" })
		{
			Assert.Equal(1, tools.Single(tool => tool.Name == name).ProtocolTool.InputSchema
				.GetProperty("properties").GetProperty("profile").GetProperty("minLength").GetInt32());
			var arguments = new Dictionary<string, object?> { ["profile"] = string.Empty };
			if (name is "get_file" or "related_files")
				arguments["path"] = "Anchor.ts";
			var result = await server.Client.CallToolAsync(name, arguments,
				progress: null, options: null, TestContext.Current.CancellationToken);
			Assert.NotEqual(true, result.IsError);
		}
		Assert.Equal(512, tools.Single(static tool => tool.Name == "get_file").ProtocolTool.InputSchema
			.GetProperty("properties").GetProperty("symbol").GetProperty("maxLength").GetInt32());
		var symbol = await server.Client.CallToolAsync("get_file",
			new Dictionary<string, object?> { ["path"] = "Anchor.ts", ["symbol"] = longName },
			progress: null, options: null, TestContext.Current.CancellationToken);
		Assert.NotEqual(true, symbol.IsError);
	}
}
