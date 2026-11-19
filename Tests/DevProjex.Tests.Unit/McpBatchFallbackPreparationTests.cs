using DevProjex.Application.Secrets;
using DevProjex.Mcp;
using static DevProjex.Mcp.DevProjexMcpTools;

namespace DevProjex.Tests.Unit;

public sealed class McpBatchFallbackPreparationTests
{
	[Fact]
	public void LongDisplayPathsRequireFallbackEvenWhenSourceTextFits()
	{
		var source = "# One\n" + new string('x', 6_490) + "\n# Two\n" + new string('y', 6_490);
		var directory = string.Join('/', Enumerable.Repeat(new string('p', 200), 19));
		var requests = Enumerable.Range(1, 3).Select(index => Whole(index, directory + $"/file-{index}.md", $"physical-{index}")).ToArray();
		var transformed = requests.ToDictionary(request => request.PhysicalPath!, request => new TransformedTextFile(request.PhysicalPath!, source, []));
		Assert.True(transformed.Values.Sum(file => file.Content.Length) < 42_000);
		Assert.True(ShouldPrepareBatchFallbackOutlines(requests, transformed));
		var result = Render(requests, transformed);
		Assert.Contains("3.1 — structure — no room left in this response, 4 lines", result.Text);
		Assert.Equal(3, result.DeliveredPaths.Count);
		Assert.DoesNotContain(" — not-returned", result.Text);
		Assert.True(result.Text.Length <= 50_000);
	}

	[Fact]
	public void AWholeReadAndARangeCountTheSameSourceTwice()
	{
		var source = "# One\n" + new string('x', 12_490) + "\n# Two\n" + new string('y', 12_490);
		var requests = new McpResolvedFileReadRequest[]
		{
			Whole(1, "source.md", "physical"),
			new(new(2, "source.md", [new(2, 1, 1, 4)]), "physical")
		};
		var transformed = new Dictionary<string, TransformedTextFile> { ["physical"] = new("physical", source, []) };
		Assert.True(source.Length < 42_000);
		Assert.True(ShouldPrepareBatchFallbackOutlines(requests, transformed));
		var result = Render(requests, transformed);
		Assert.Contains("1.1 — ok", result.Text);
		Assert.Contains("Requests: 2.1\nStatus: partial\n", result.Text);
		Assert.DoesNotContain("2.1 — structure", result.Text);
		Assert.True(result.Text.Length <= 50_000);
	}

	[Fact]
	public void EveryDisjointRangeReservesItsOwnDisplayPath()
	{
		var requests = new McpResolvedFileReadRequest[]
		{
			Whole(1, "whole.md", "whole"),
			new(new(2, new string('p', 4_000), Enumerable.Range(1, 15)
				.Select(index => new McpGetFileRange(2, index, index * 2, index * 2)).ToArray()), "ranges")
		};
		var transformed = new Dictionary<string, TransformedTextFile>
		{
			["whole"] = new("whole", "# One\nvalue\n# Two\nvalue", []),
			["ranges"] = new("ranges", string.Join('\n', Enumerable.Repeat("line", 30)), [])
		};
		Assert.True(ShouldPrepareBatchFallbackOutlines(requests, transformed));
	}

	[Fact]
	public void AReducedFinalEnvelopeCanRequireFallbackAfterTheInitialEstimateFit()
	{
		var requests = new[] { Whole(1, "source.md", "source") };
		var source = "# One\n" + new string('x', 19_993) + "\n# Two\n" + new string('y', 19_993);
		var transformed = new Dictionary<string, TransformedTextFile> { ["source"] = new("source", source, []) };
		Assert.False(ShouldPrepareBatchFallbackOutlines(requests, transformed, 50_000));
		Assert.True(ShouldPrepareBatchFallbackOutlines(requests, transformed, 35_000));
	}

	[Fact]
	public void SmallBatchKeepsTheCheapPathAndWholeText()
	{
		var requests = new[] { Whole(1, "one.md", "one"), Whole(2, "two.md", "two") };
		var transformed = new Dictionary<string, TransformedTextFile>
		{
			["one"] = new("one", "# One\na\n# Two\nb", []),
			["two"] = new("two", "# Three\nc\n# Four\nd", [])
		};
		Assert.False(ShouldPrepareBatchFallbackOutlines(requests, transformed));
		var result = Render(requests, transformed);
		Assert.Contains("1.1 — ok", result.Text);
		Assert.Contains("2.1 — ok", result.Text);
		Assert.DoesNotContain("Structure:", result.Text);
	}

	private static McpResolvedFileReadRequest Whole(int index, string path, string physical) =>
		new(new(index, path, [new(index, 1, 1, int.MaxValue, IsWholeFile: true)]), physical);

	private static McpBatchFileReadResult Render(IReadOnlyList<McpResolvedFileReadRequest> requests,
		IReadOnlyDictionary<string, TransformedTextFile> transformed)
	{
		var prepare = ShouldPrepareBatchFallbackOutlines(requests, transformed);
		var outlines = new Dictionary<string, McpLargeFileOutline>();
		foreach (var request in requests.Where(request => request.Request.Ranges.All(range => range.IsWholeFile)))
		{
			var file = transformed[request.PhysicalPath!];
			var outline = McpLargeFileOutlineBuilder.Build(request.Request.Path, file.Content, static () => [],
				TestContext.Current.CancellationToken, requireLargeFile: !prepare);
			if (outline is not null) outlines[request.PhysicalPath!] = outline;
		}
		return RenderBatchFileReads(requests, transformed, new Dictionary<string, string>(), new("project", null),
			null, null, TestContext.Current.CancellationToken, outlinesByPath: outlines);
	}
}
