using DevProjex.Application.Secrets;
using DevProjex.Mcp;
using static DevProjex.Mcp.DevProjexMcpTools;

namespace DevProjex.Tests.Unit;

public sealed class McpBatchRangeReasonTests
{
	[Theory]
	[InlineData("one\ntwo", 3, 4, "valid lines 1-2")]
	[InlineData("one\r\ntwo", 3, 4, "valid lines 1-2")]
	[InlineData("one\rtwo", 3, 4, "valid lines 1-2")]
	[InlineData("", 1, 1, "file is empty")]
	[InlineData("one\n", 3, 9, "valid lines 1-2")]
	public void InvalidRangesKeepTheirPathAndActualBoundary(string content, int start, int end, string boundary)
	{
		var result = Render(content, new McpGetFileRange(1, 1, start, end));
		Assert.Equal($"Requests:\n1.1 — not-returned — Source.txt: requested lines {start}-{end}; {boundary}", result.Text);
		Assert.Equal("[Batch read] ok=0 · structure=0 · partial=0 · not-returned=1 · unavailable=0.", result.Summary);
		Assert.Empty(result.DeliveredPaths);
		Assert.Empty(result.DeliveredRanges);
	}

	[Fact]
	public void OverlappingAndDisjointInvalidRangesKeepTheirOwnCoordinates()
	{
		var result = Render("one\ntwo\nthree",
			new(1, 1, 1, 20), new(1, 2, 8, 12), new(1, 3, 30, 40));
		Assert.Contains("1.1 — ok\n1.2 — not-returned — Source.txt: requested lines 8-12; valid lines 1-3", result.Text);
		Assert.Contains("1.3 — not-returned — Source.txt: requested lines 30-40; valid lines 1-3", result.Text);
		Assert.Contains("Requests: 1.1\nStatus: ok\nLines: 1-3 of 3\none\ntwo\nthree", result.Text);
		Assert.Equal("[Batch read] ok=1 · structure=0 · partial=0 · not-returned=2 · unavailable=0.", result.Summary);
		Assert.Equal(["Source.txt"], result.DeliveredPaths);
	}

	[Fact]
	public void InvalidRangeIsDiagnosedEvenAfterAnotherFileConsumesTheBudget()
	{
		var requests = new[]
		{
			new McpResolvedFileReadRequest(new(1, "Large.txt", [new(1, 1, 1, 1)]), "large"),
			new McpResolvedFileReadRequest(new(2, "Source.txt", [new(2, 1, 8, 9)]), "small")
		};
		var result = RenderBatchFileReads(requests,
			new Dictionary<string, TransformedTextFile>
			{
				["large"] = new("large", new string('x', 60_000), []),
				["small"] = new("small", "one", [])
			}, new Dictionary<string, string>(), new("project", null), null, null, TestContext.Current.CancellationToken);
		Assert.Contains("2.1 — not-returned — Source.txt: requested lines 8-9; valid lines 1-1", result.Text);
		Assert.DoesNotContain("Large.txt: requested lines", result.Text);
		Assert.True(result.Text.Length <= 50_000);
		Assert.DoesNotContain("Source.txt", result.DeliveredPaths);
	}

	[Fact]
	public void WholeEmptyFileAndExplicitRangeRemainDistinct()
	{
		var result = Render("", new(1, 1, 1, int.MaxValue, IsWholeFile: true), new(1, 2, 1, 1));
		Assert.Contains("1.1 — ok\n1.2 — not-returned — Source.txt: requested lines 1-1; file is empty", result.Text);
		Assert.Equal("[Batch read] ok=1 · structure=0 · partial=0 · not-returned=1 · unavailable=0.", result.Summary);
		Assert.Empty(result.DeliveredRanges);
	}

	[Fact]
	public void ValidBatchKeepsItsExactBytesAndClampsAnEndAfterEof()
	{
		var result = Render("one\ntwo", new(1, 1, 1, 2), new(1, 2, 2, 3));
		Assert.Equal("Requests:\n1.1 — ok\n1.2 — ok\n\nFile: Source.txt\nRequests: 1.1, 1.2\nStatus: ok\nLines: 1-2 of 2\none\ntwo", result.Text);
		Assert.Equal("[Batch read] ok=2 · structure=0 · partial=0 · not-returned=0 · unavailable=0.", result.Summary);
		Assert.Equal("[Range clamped] requests=1.2; end_line exceeded the file; returned through line 2.", result.Continuations);
	}

	[Fact]
	public void DiagnosticsProtectAndEscapeThePathAndBoundStatusLines()
	{
		const string secret = "ghp_" + "a7D9mQ2xK4vN8sR6tY3uW5zB1cE0fG2hJ9pL";
		var path = "folder\n" + secret + new string('x', 400) + ".txt";
		var request = new McpResolvedFileReadRequest(new(1, path, [new(1, 1, 9, 10)]), "source");
		var result = RenderBatchFileReads([request],
			new Dictionary<string, TransformedTextFile> { ["source"] = new("source", "one", []) },
			new Dictionary<string, string>(), new("project", null), null, null, TestContext.Current.CancellationToken);
		Assert.Contains("folder\\n[redacted]", result.Text);
		Assert.DoesNotContain(secret, result.Text);
		Assert.Contains("…: requested lines 9-10; valid lines 1-1", result.Text);
		Assert.Equal(2, result.Text.Split('\n').Length);
		Assert.True(result.Text.Length < 400);
	}

	private static McpBatchFileReadResult Render(string content, params McpGetFileRange[] ranges) =>
		RenderBatchFileReads([new(new(1, "Source.txt", ranges), "source")],
			new Dictionary<string, TransformedTextFile> { ["source"] = new("source", content, []) },
			new Dictionary<string, string>(), new("project", null), null, null, TestContext.Current.CancellationToken);
}
