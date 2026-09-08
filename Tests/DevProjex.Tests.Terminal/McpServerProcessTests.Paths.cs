using ModelContextProtocol.Protocol;

namespace DevProjex.Tests.Terminal;

public sealed partial class McpServerProcessTests
{
	[Fact]
	public async Task RealProcessGetTreeAndSearchProjectAcceptLiteralPathsNarrowing()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("paths-project");
		workspace.WriteFile("paths-project/src/Selected file.txt", "selected-process-marker\n");
		workspace.WriteFile("paths-project/src/Other.txt", "other-process-marker\n");
		workspace.WriteFile("paths-project/Outside.txt", "outside-process-marker\n");
		await using var server = await ActualMcpProcess.StartAsync(
			project,
			workspace.CreateDirectory("data"));

		var tree = await server.Client.CallToolAsync(
			"get_tree",
			new Dictionary<string, object?>
			{
				["paths"] = new[] { "src/Selected file.txt" },
				["format"] = "text"
			},
			progress: null,
			options: null,
			TestContext.Current.CancellationToken);
		var search = await server.Client.CallToolAsync(
			"search_project",
			new Dictionary<string, object?>
			{
				["pattern"] = "process-marker",
				["paths"] = new[] { "src" },
				["include_patterns"] = new[] { "**/Selected file.txt" },
				["context_lines"] = 0,
				["ignore_case"] = false
			},
			progress: null,
			options: null,
			TestContext.Current.CancellationToken);

		var treeText = AllProcessText(tree);
		Assert.Contains("Selected file.txt", treeText, StringComparison.Ordinal);
		Assert.DoesNotContain("Other.txt", treeText, StringComparison.Ordinal);
		Assert.DoesNotContain("Outside.txt", treeText, StringComparison.Ordinal);
		var searchText = AllProcessText(search);
		Assert.Contains("src/Selected file.txt:1:selected-process-marker", searchText, StringComparison.Ordinal);
		Assert.DoesNotContain("other-process-marker", searchText, StringComparison.Ordinal);
		Assert.DoesNotContain("outside-process-marker", searchText, StringComparison.Ordinal);
	}
}
