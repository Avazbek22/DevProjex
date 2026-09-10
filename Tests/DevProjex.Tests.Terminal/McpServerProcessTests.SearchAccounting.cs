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

	[Fact]
	public async Task RealProcessBoundsAWideSearchAndSizesItWithExactTotals()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("wide-search-project");
		for (var file = 0; file < 40; file++)
		{
			var lines = Enumerable
				.Range(0, 60)
				.Select(line => line % 3 == 0
					? $"const needle{line:D2} = {new string('n', 60)}"
					: $"const filler{line:D2} = {new string('f', 60)}");
			workspace.WriteFile($"wide-search-project/src/Module{file:D2}.ts", string.Join("\n", lines) + "\n");
		}
		workspace.WriteFile("wide-search-project/src/Single.ts", "const solitaryMarker = 1\n");
		await using var server = await ActualMcpProcess.StartAsync(
			project,
			workspace.CreateDirectory("data"));

		var wide = await server.Client.CallToolAsync(
			"search_project",
			new Dictionary<string, object?>
			{
				["pattern"] = "needle",
				["context_lines"] = 3,
				["ignore_case"] = false,
				["max_results"] = 200
			},
			progress: null,
			options: null,
			TestContext.Current.CancellationToken);
		var narrow = await server.Client.CallToolAsync(
			"search_project",
			new Dictionary<string, object?>
			{
				["pattern"] = "solitaryMarker",
				["context_lines"] = 0,
				["ignore_case"] = false
			},
			progress: null,
			options: null,
			TestContext.Current.CancellationToken);

		var wideText = AllProcessText(wide).Replace("\r\n", "\n", StringComparison.Ordinal);
		Assert.NotEqual(true, wide.IsError);
		Assert.True(wideText.Length <= 18_000, $"Wide search returned {wideText.Length} characters.");
		Assert.Contains("[Search truncated]", wideText, StringComparison.Ordinal);
		Assert.Contains("Narrow the pattern", wideText, StringComparison.Ordinal);
		Assert.Contains("lower context_lines", wideText, StringComparison.Ordinal);
		// 40 files carry 20 matching lines each, and the counters stay exact under the cap.
		Assert.Contains("[Search totals] matches=800 · files=40", wideText, StringComparison.Ordinal);
		var shown = Regex.Matches(wideText, @"^src/Module\d{2}\.ts:\d+:const needle", RegexOptions.Multiline).Count;
		var additional = Regex.Match(wideText, @"\[(\d+) additional matches not shown;");
		Assert.True(additional.Success, wideText);
		Assert.Equal(800, shown + int.Parse(additional.Groups[1].Value));

		// A search that returns everything it found keeps its previous response exactly.
		var narrowText = AllProcessText(narrow).Replace("\r\n", "\n", StringComparison.Ordinal);
		Assert.NotEqual(true, narrow.IsError);
		Assert.Contains("src/Single.ts:1:const solitaryMarker = 1", narrowText, StringComparison.Ordinal);
		Assert.DoesNotContain("[Search totals]", narrowText, StringComparison.Ordinal);
		Assert.DoesNotContain("[Search truncated]", narrowText, StringComparison.Ordinal);
		Assert.DoesNotContain("additional matches", narrowText, StringComparison.Ordinal);
	}
}
