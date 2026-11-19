using DevProjex.Application.Secrets;
using DevProjex.Mcp;
using static DevProjex.Mcp.DevProjexMcpTools;

namespace DevProjex.Tests.Unit;

public sealed class McpBatchStructureTests
{
	[Fact]
	public void ShortLinesAreNotLimitedToOneThousandAndLaterFilesKeepRoom()
	{
		var result = Render([string.Join('\n', Enumerable.Repeat("x", 2_000)), "last\nline"]);
		Assert.Contains("Lines: 1-2000 of 2000", result.Text);
		Assert.Contains("2.1 — ok", result.Text);
		Assert.Equal("[Batch read] ok=2 · structure=0 · partial=0 · not-returned=0 · unavailable=0.", result.Summary);
		Assert.True(result.Text.Length <= 50_000);
	}

	[Fact]
	public void LargeFilesUseTheScalarBuilderAndKeepTheirStart()
	{
		var content = Markdown(400, 80);
		var result = Render([content]);
		Assert.Contains("1.1 — structure — large file, 400 lines", result.Text);
		Assert.Contains("Status: structure\nLines: 1-", result.Text);
		Assert.Contains("Structure:\n", result.Text);
		Assert.Contains("# First", result.Text);
		Assert.Single(result.StructuredPaths);
		Assert.Contains("[Batch structure] 1 file(s)", result.Continuations);
		Assert.DoesNotContain("[Batch continuation]", result.Continuations ?? "");
	}

	[Fact]
	public void LaterStructuredFileCannotBeDisplacedByEarlierText()
	{
		var first = string.Join('\n', Enumerable.Repeat(new string('x', 90), 500));
		var later = Markdown(240, 100);
		var result = Render([first, later], ["first.txt", "later.md"]);
		Assert.Contains("1.1 — ok", result.Text);
		Assert.Contains("2.1 — structure — no room left in this response, 240 lines", result.Text);
		Assert.Contains("File: later.md\nRequests: 2.1\nStatus: structure\nLines: none of 240\nStructure:", result.Text);
		Assert.DoesNotContain("File: later.md\nRequests: 2.1\nStatus: structure\nLines: 1-", result.Text);
		Assert.Equal(result.Text, Render([first, later], ["first.txt", "later.md"]).Text);
	}

	[Fact]
	public void NoOutlineMeansWholeOrAtLeastTwentyLinesInsteadOfInventedStructure()
	{
		var plain = string.Join('\n', Enumerable.Repeat(new string('x', 80), 400));
		var result = Render([plain, plain], ["a.txt", "b.txt"]);
		Assert.Contains("1.1 — ok", result.Text);
		Assert.Contains("2.1 — partial", result.Text);
		Assert.DoesNotContain("Structure:", result.Text);
		Assert.All(result.DeliveredRanges, range => Assert.True(range.EndLine - range.StartLine + 1 >= 20));
		Assert.Contains("[Batch partial] requests=2.1; returned through line", result.Continuations);
	}

	[Fact]
	public void BudgetBoundariesAreMeasuredInCharactersIncludingHeadersAndStatuses()
	{
		var content = "first line\n" + new string('x', 240);
		var full = Render([content], ["a.txt"]);
		var exact = Render([content], ["a.txt"], full.Text.Length);
		var below = Render([content], ["a.txt"], full.Text.Length - 1);
		Assert.Equal(full.Text, exact.Text);
		Assert.Equal(full.Text, Render([content], ["a.txt"], full.Text.Length + 1).Text);
		Assert.DoesNotContain("1.1 — ok", below.Text);
		Assert.Contains("no room left in this response", below.Text);
		Assert.True(below.Text.Length <= full.Text.Length - 1);
	}

	[Fact]
	public void EmptyWholeFileIsDeliveredAndLineEndingsDoNotAffectThePayload()
	{
		var content = Markdown(400, 80);
		var lf = Render(["", content]);
		var crlf = Render(["", content.Replace("\n", "\r\n", StringComparison.Ordinal)]);
		Assert.Contains("1.1 — ok", lf.Text);
		Assert.Contains("Lines: 0-0 of 0", lf.Text);
		Assert.Equal(lf.Text, crlf.Text);
		Assert.Equal(lf.Summary, crlf.Summary);
	}

	[Fact]
	public void ExplicitRangesRemainTextEvenWhenAWholeReadUsesStructure()
	{
		var source = Markdown(400, 80);
		var physical = "source";
		var requests = new McpResolvedFileReadRequest[]
		{
			new(new(1, "source.md", [new(1, 1, 1, int.MaxValue, true)]), physical),
			new(new(2, "source.md", [new(2, 1, 200, 210)]), physical)
		};
		var result = RenderBatchFileReads(requests, new Dictionary<string, TransformedTextFile>
		{
			[physical] = new(physical, source, [])
		}, new Dictionary<string, string>(), new("project", null), null, null, TestContext.Current.CancellationToken,
			outlinesByPath: new Dictionary<string, McpLargeFileOutline> { [physical] = Outline("source.md", source)! });
		Assert.Contains("1.1 — structure", result.Text);
		Assert.Contains("2.1 — ok", result.Text);
		Assert.Contains("Requests: 2.1\nStatus: ok\nLines: 200-210 of 400", result.Text);
	}

	private static McpBatchFileReadResult Render(string[] contents, string[]? paths = null, int characters = 50_000)
	{
		paths ??= contents.Select((_, index) => $"source{index}.md").ToArray();
		var requests = contents.Select((_, index) => new McpResolvedFileReadRequest(
			new(index + 1, paths[index], [new(index + 1, 1, 1, int.MaxValue, true)]), $"physical{index}")).ToArray();
		var transformed = contents.Select((content, index) => new TransformedTextFile($"physical{index}", content, []))
			.ToDictionary(static file => file.Path, StringComparer.Ordinal);
		var outlines = requests.Select((request, index) => (Path: request.PhysicalPath!, Outline: Outline(paths[index], contents[index])))
			.Where(static item => item.Outline is not null).ToDictionary(static item => item.Path, static item => item.Outline!);
		return RenderBatchFileReads(requests, transformed, new Dictionary<string, string>(), new("project", null), null, null,
			TestContext.Current.CancellationToken, outlinesByPath: outlines, maximumCharacters: characters);
	}

	private static McpLargeFileOutline? Outline(string path, string content) => McpLargeFileOutlineBuilder.Build(path, content,
		static () => [], TestContext.Current.CancellationToken, requireLargeFile: false);

	private static string Markdown(int lines, int width) => string.Join('\n', Enumerable.Range(0, lines)
		.Select(index => index == 0 ? "# First" : index == lines / 2 ? "# Second" : new string('x', width)));
}
