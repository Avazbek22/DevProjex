using ModelContextProtocol.Protocol;

namespace DevProjex.Tests.Integration;

public sealed partial class McpServerIntegrationTests
{
	[Fact]
	public async Task GetFilePathArrayReturnsTheSameReadAsWholeFileRequests()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		WriteProjectFile(project, "src/a.ts", "export const a = 1;\n");
		WriteProjectFile(project, "src/b.ts", "export const b = 2;\n");
		WriteProjectFile(project, "README.md", "# Title\n");
		var paths = new[] { "src/a.ts", "src/b.ts", "README.md" };

		// Each form runs on a fresh server, so repeated-notice suppression cannot tell them apart.
		async Task<CallToolResult> ReadAsync(string name, object value)
		{
			await using var server = await McpTestServer.StartAsync(project, workspace.Path);
			return await server.CallAsync("get_file", new Dictionary<string, object?> { [name] = value });
		}

		var requests = await ReadAsync("requests", paths.Select(static path => new { path }).ToArray());
		var pathArray = await ReadAsync("path", paths);
		var pathsArray = await ReadAsync("paths", paths);
		var pathsString = await ReadAsync("paths", "src/a.ts");
		var scalar = await ReadAsync("path", "src/a.ts");

		Assert.NotEqual(true, requests.IsError);
		Assert.Contains("export const b = 2;", Text(requests), StringComparison.Ordinal);
		Assert.Equal(WithoutFenceNonce(requests), WithoutFenceNonce(pathArray));
		Assert.Equal(WithoutFenceNonce(requests), WithoutFenceNonce(pathsArray));
		Assert.Equal(WithoutFenceNonce(scalar), WithoutFenceNonce(pathsString));
	}

	[Fact]
	public async Task GetFilePathListsKeepTheBatchLimitAndMergeForms()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		WriteProjectFile(project, "a.cs", "class A;\n");
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);

		var tooMany = await server.CallAsync("get_file", new Dictionary<string, object?>
		{
			["path"] = Enumerable.Range(0, 17).Select(static index => $"File{index}.cs").ToArray()
		});
		var combined = await server.CallAsync("get_file", new Dictionary<string, object?>
		{
			["paths"] = new[] { "a.cs" },
			["requests"] = new[] { new { path = "a.cs" } }
		});

		Assert.True(tooMany.IsError);
		Assert.Contains("'path' must be an array with 1 to 16 entries.", Text(tooMany), StringComparison.Ordinal);
		Assert.NotEqual(true, combined.IsError);
		Assert.Contains("1.1 — ok", Text(combined), StringComparison.Ordinal);
		Assert.Single(Regex.Matches(Text(combined), "File: a\\.cs").Cast<Match>());
	}

	private static string WithoutFenceNonce(CallToolResult result) =>
		Regex.Replace(Text(result), "untrusted-data-[0-9a-f]+", "untrusted-data");
}
