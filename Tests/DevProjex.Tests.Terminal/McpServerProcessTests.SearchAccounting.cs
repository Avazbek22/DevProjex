using System.Text.RegularExpressions;
using ModelContextProtocol.Protocol;

namespace DevProjex.Tests.Terminal;

public sealed partial class McpServerProcessTests
{
	[Fact]
	public async Task RealProcessDiscoverySearchReadAndStoredPackWorkflowUsesReturnedIdentifiers()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("workflow-project");
		workspace.WriteFile("workflow-project/src/Needle.ts", "export const workflowNeedle = 1;\n");
		workspace.WriteFile("workflow-project/Large.txt", "large-marker\n" + new string('x', 70_000));
		workspace.WriteFile("workflow-project/tsconfig.json", "{\"compilerOptions\":{\"moduleResolution\":\"bundler\"}}\n");
		workspace.WriteFile("workflow-project/target.ts", "export const target = 1;\n");
		for (var index = 0; index < 700; index++)
		{
			var name = $"very-long-related-target-{index:D4}";
			workspace.WriteFile(
				$"workflow-project/related/{name}.ts",
				$"import target from '../target.js'; export const value{index} = target;\n");
		}
		await using var server = await ActualMcpProcess.StartAsync(
			project,
			workspace.CreateDirectory("data"));

		var listed = await server.Client.CallToolAsync(
			"list_projects",
			new Dictionary<string, object?>(),
			progress: null,
			options: null,
			TestContext.Current.CancellationToken);
		var projectName = listed.StructuredContent!.Value.GetProperty("projects")[0].GetProperty("name").GetString();
		var tree = await server.Client.CallToolAsync(
			"get_tree",
			new Dictionary<string, object?> { ["project"] = projectName, ["format"] = "text" },
			progress: null,
			options: null,
			TestContext.Current.CancellationToken);
		Assert.Contains("Needle.ts", AllProcessText(tree), StringComparison.Ordinal);

		var search = await server.Client.CallToolAsync(
			"search_project",
			new Dictionary<string, object?>
			{
				["project"] = projectName,
				["pattern"] = "workflowNeedle",
				["context_lines"] = 0,
				["ignore_case"] = false
			},
			progress: null,
			options: null,
			TestContext.Current.CancellationToken);
		var match = Regex.Match(AllProcessText(search), @"(?<path>src/Needle\.ts):(?<line>\d+):");
		Assert.True(match.Success, AllProcessText(search));
		var file = await server.Client.CallToolAsync(
			"get_file",
			new Dictionary<string, object?>
			{
				["project"] = projectName,
				["path"] = match.Groups["path"].Value,
				["start_line"] = int.Parse(match.Groups["line"].Value),
				["end_line"] = int.Parse(match.Groups["line"].Value)
			},
			progress: null,
			options: null,
			TestContext.Current.CancellationToken);
		Assert.Contains("workflowNeedle", AllProcessText(file), StringComparison.Ordinal);

		var packed = await server.Client.CallToolAsync(
			"pack_context",
			new Dictionary<string, object?>
			{
				["project"] = projectName,
				["paths"] = new[] { "Large.txt" },
				["view"] = "content",
				["format"] = "text"
			},
			progress: null,
			options: null,
			TestContext.Current.CancellationToken);
		var packText = AllProcessText(packed);
		var packPage = await server.Client.CallToolAsync(
			"read_pack",
			new Dictionary<string, object?> { ["pack_id"] = ExtractPackId(packText) },
			progress: null,
			options: null,
			TestContext.Current.CancellationToken);
		Assert.Contains("large-marker", AllProcessText(packPage), StringComparison.Ordinal);

		var related = await server.Client.CallToolAsync(
			"related_files",
			new Dictionary<string, object?>
			{
				["project"] = projectName,
				["path"] = "target.ts",
				["direction"] = "dependents"
			},
			progress: null,
			options: null,
			TestContext.Current.CancellationToken);
		var relatedText = AllProcessText(related);
		var relatedId = Regex.Match(relatedText, "Related-files result stored as '([^']+)'");
		Assert.True(relatedId.Success, relatedText);
		var relatedPage = await server.Client.CallToolAsync(
			"read_pack",
			new Dictionary<string, object?> { ["pack_id"] = relatedId.Groups[1].Value },
			progress: null,
			options: null,
			TestContext.Current.CancellationToken);
		Assert.Contains("very-long-related-target-", AllProcessText(relatedPage), StringComparison.Ordinal);
	}

	[Fact]
	public async Task RealProcessSearchAccountsOnlyFullyRenderedMatchesWhenAGroupIsTruncated()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile(
			"project/A-unicode.txt",
			string.Join("\r\n", Enumerable.Range(1, 100)
				.Select(index => $"needle-{index:D3}-😀-{new string('a', 290)}")) + "\r\n");
		workspace.WriteFile(
			"project/B-ascii.txt",
			string.Join("\n", Enumerable.Range(101, 100)
				.Select(index => $"needle-{index:D3}-{new string('b', 296)}")) + "\n");
		await using var server = await ActualMcpProcess.StartAsync(
			project,
			workspace.CreateDirectory("data"));

		var result = await server.Client.CallToolAsync(
			"search_project",
			new Dictionary<string, object?>
			{
				["pattern"] = "needle",
				["context_lines"] = 0,
				["ignore_case"] = false,
				["max_results"] = 200
			},
			progress: null,
			options: null,
			TestContext.Current.CancellationToken);
		var text = AllProcessText(result).Replace("\r\n", "\n", StringComparison.Ordinal);
		var shown = Regex.Matches(
			text,
			@"^(?:A-unicode\.txt:\d+:needle-\d{3}-😀-a{290}|B-ascii\.txt:\d+:needle-\d{3}-b{296})$",
			RegexOptions.Multiline).Count;
		var additionalMatch = Regex.Match(text, @"\[(\d+) additional matches not shown;");

		Assert.NotEqual(true, result.IsError);
		Assert.InRange(shown, 1, 199);
		Assert.True(additionalMatch.Success, text);
		var additional = int.Parse(additionalMatch.Groups[1].Value);
		Assert.True(
			shown + additional == 200,
			$"shown={shown}, additional={additional}\n{text[^Math.Min(text.Length, 2_000)..]}");
		Assert.Contains("[Search group truncated at the response character limit.]", text, StringComparison.Ordinal);
		Assert.Contains("😀", text, StringComparison.Ordinal);
		Assert.DoesNotContain("\uD83D</untrusted-data-", text, StringComparison.Ordinal);
	}
}
