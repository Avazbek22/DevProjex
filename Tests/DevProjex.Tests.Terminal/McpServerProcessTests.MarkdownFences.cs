namespace DevProjex.Tests.Terminal;

public sealed partial class McpServerProcessTests
{
	[Theory]
	[InlineData(false, 0)]
	[InlineData(true, 0)]
	[InlineData(false, 3)]
	[InlineData(true, 3)]
	public async Task RealProcessMarkdownStructuresKeepHeadingsInsideTheirMatchingFence(bool full, int indentation)
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		var indent = new string(' ', indentation);
		var lines = new List<string> { indent + "# Before", "````text", "```", "# Hidden", "~~~", "# Also hidden", "~~~" };
		lines.AddRange(Enumerable.Repeat(new string('x', 90), 320));
		lines.Add("````");
		lines.Add(indent + "# After");
		var afterLine = lines.Count;
		lines.Add("last line");
		workspace.WriteFile("project/guide µ.md", string.Join("\r\n", lines));
		workspace.WriteFile("project/empty.txt", string.Empty);
		await using var server = await ActualMcpProcess.StartAsync(project, workspace.CreateDirectory("data"),
			useDefaultToolSet: !full);
		foreach (var arguments in new Dictionary<string, object?>[]
			{ new() { ["path"] = "guide µ.md" }, new() { ["paths"] = new[] { "guide µ.md", "empty.txt" } } })
		{
			var result = await CallAsync(server, "get_file", arguments);
			Assert.NotEqual(true, result.IsError);
			Assert.Contains($"Structure:\n1-{afterLine - 1} Before\n{afterLine}-{lines.Count} After",
				BatchData(result), StringComparison.Ordinal);
		}
	}
}
