using System.Text.RegularExpressions;
using ModelContextProtocol.Protocol;

namespace DevProjex.Tests.Terminal;

public sealed partial class McpServerProcessTests
{
	[Fact]
	public async Task RealProcessBatchReadsSixteenFilesAndPreservesCoverageBeyondOneThousandLines()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("batch-project");
		workspace.WriteFile("batch-project/large.md", string.Join('\n', Enumerable.Range(0, 500)
			.Select(index => index == 0 ? "# Start" : index == 250 ? "# End" : new string('x', 80))));
		workspace.WriteFile("batch-project/lines.txt", string.Join('\n', Enumerable.Repeat("line", 2_000)));
		for (var index = 0; index < 14; index++) workspace.WriteFile($"batch-project/file{index}.txt", $"tail-{index}");
		var paths = new[] { "large.md", "lines.txt" }.Concat(Enumerable.Range(0, 14).Select(index => $"file{index}.txt")).ToArray();
		await using var server = await ActualMcpProcess.StartAsync(project, workspace.CreateDirectory("data"));
		var first = await CallAsync(server, "get_file", new() { ["paths"] = paths });
		Assert.NotEqual(true, first.IsError);
		var text = AllProcessText(first);
		Assert.Contains("1.1 — structure — large file, 500 lines", text, StringComparison.Ordinal);
		Assert.Contains("[Batch read] ok=15 · structure=1 · partial=0 · not-returned=0 · unavailable=0.", text, StringComparison.Ordinal);
		Assert.Contains("Lines: 1-2000 of 2000", text, StringComparison.Ordinal);
		Assert.True(text.Length <= 50_000, text.Length.ToString());
		var repeated = await CallAsync(server, "get_file", new() { ["paths"] = paths });
		Assert.Equal(BatchData(first), BatchData(repeated));
		for (var index = 0; index < 3; index++)
		{
			var range = await CallAsync(server, "get_file", new() { ["path"] = "large.md", ["start_line"] = 300, ["end_line"] = 302 });
			Assert.DoesNotContain("Structure:", AllProcessText(range), StringComparison.Ordinal);
		}
	}

	[Fact]
	public async Task RealProcessCombinesSelectorsAndReturnsTheSameRangeForEachListedFile()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("shape-project");
		workspace.WriteFile("shape-project/a.cs", "class Demo {\n public void Run() {}\n}\n");
		workspace.WriteFile("shape-project/b.cs", "class Other {\n public void Run() {}\n}\n");
		await using var server = await ActualMcpProcess.StartAsync(project, workspace.CreateDirectory("data"));
		var listed = await CallAsync(server, "get_file", new() { ["paths"] = new[] { "a.cs", "b.cs" }, ["start_line"] = 2, ["end_line"] = 2 });
		Assert.NotEqual(true, listed.IsError);
		Assert.Contains("[Read as] start_line and end_line were applied to each of the 2 files.", AllProcessText(listed), StringComparison.Ordinal);
		Assert.Equal(2, Regex.Matches(BatchData(listed), "Lines: 2-2 of 4", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2)).Count);
		var combined = await CallAsync(server, "get_file", new()
		{
			["path"] = "a.cs",
			["paths"] = new[] { "b.cs", "a.cs" },
			["requests"] = new[] { new { path = "a.cs", symbol = "Demo.Run" } }
		});
		Assert.NotEqual(true, combined.IsError);
		Assert.Contains("[Read as] path and requests were combined; 1 file(s) from path are read whole.", AllProcessText(combined), StringComparison.Ordinal);
		Assert.Contains("Requests: 1.1\nStatus: ok\nLines: 2-2", BatchData(combined), StringComparison.Ordinal);
		var scalar = await CallAsync(server, "get_file", new() { ["path"] = "a.cs", ["symbol"] = "Demo.Run" });
		var singleList = await CallAsync(server, "get_file", new() { ["paths"] = new[] { "a.cs" }, ["symbol"] = "Demo.Run" });
		Assert.Equal(BatchData(scalar), BatchData(singleList));
		var openEnded = await CallAsync(server, "get_file", new() { ["paths"] = new[] { "a.cs", "b.cs" }, ["start_line"] = 2 });
		Assert.NotEqual(true, openEnded.IsError);
		Assert.DoesNotContain("[Range clamped]", AllProcessText(openEnded), StringComparison.Ordinal);
		var canonicalCombined = await CallAsync(server, "get_file", new()
		{
			["path"] = Path.Combine(project, "a.cs"),
			["requests"] = new[] { new { path = "a.cs", symbol = "Demo.Run" } }
		});
		Assert.NotEqual(true, canonicalCombined.IsError);
		Assert.DoesNotContain("[Read as]", AllProcessText(canonicalCombined), StringComparison.Ordinal);
		Assert.DoesNotContain("2.1", BatchData(canonicalCombined), StringComparison.Ordinal);
		Assert.Contains("Requests: 1.1\nStatus: ok\nLines: 2-2", BatchData(canonicalCombined), StringComparison.Ordinal);
		var missingCombined = await CallAsync(server, "get_file", new()
		{
			["path"] = Path.Combine(project, "missing.cs"),
			["requests"] = new[] { new { path = "missing.cs" } }
		});
		Assert.NotEqual(true, missingCombined.IsError);
		Assert.Contains("1.1 — unavailable", BatchData(missingCombined), StringComparison.Ordinal);
		Assert.DoesNotContain("2.1", BatchData(missingCombined), StringComparison.Ordinal);
		Assert.DoesNotContain("[Read as]", AllProcessText(missingCombined), StringComparison.Ordinal);
	}

	[Fact]
	public async Task RealProcessFolderSeedsExpandOnlyDirectSelectedChildrenAndKeepOldRefusals()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("folder-project");
		workspace.WriteFile("folder-project/parts/a.ts", "import { b } from './b'; export const a = b;");
		workspace.WriteFile("folder-project/parts/b.ts", "export const b = 1;");
		workspace.WriteFile("folder-project/parts/deep/hidden.ts", "export const hidden = 1;");
		for (var index = 0; index < 17; index++) workspace.WriteFile($"folder-project/many/file{index}.ts", "export const item = 1;");
		await using var server = await ActualMcpProcess.StartAsync(project, workspace.CreateDirectory("data"));
		var related = await CallAsync(server, "related_files", new() { ["path"] = "parts", ["direction"] = "both" });
		Assert.NotEqual(true, related.IsError);
		Assert.Contains("[Read as] 1 folder(s) were replaced by the 2 file(s) directly inside them as seeds.", AllProcessText(related), StringComparison.Ordinal);
		Assert.Contains("Seed: parts/a.ts", BatchData(related), StringComparison.Ordinal);
		Assert.Contains("Seed: parts/b.ts", BatchData(related), StringComparison.Ordinal);
		Assert.DoesNotContain("Seed: parts/deep/hidden.ts", BatchData(related), StringComparison.Ordinal);
		var oldRefusal = await CallAsync(server, "get_file", new() { ["path"] = "many" });
		var tooMany = await CallAsync(server, "related_files", new() { ["path"] = "many" });
		Assert.True(oldRefusal.IsError);
		Assert.True(tooMany.IsError);
		Assert.Equal(BatchData(oldRefusal), BatchData(tooMany));
	}

	private static string BatchData(CallToolResult result) => string.Join('\n', result.Content.OfType<TextContentBlock>()
		.SelectMany(block => Regex.Matches(block.Text, @"<untrusted-data-(?<nonce>[a-f0-9]{24})>\n(?<body>[\s\S]*?)\n</untrusted-data-\k<nonce>>",
			RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2)).Select(static match => match.Groups["body"].Value)));
}
