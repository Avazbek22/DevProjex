using DevProjex.Mcp;
using ModelContextProtocol.Protocol;

namespace DevProjex.Tests.Integration;

public sealed partial class McpServerIntegrationTests
{
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
