using System.Text.RegularExpressions;
using DevProjex.Infrastructure.ProjectProfiles;
using ModelContextProtocol.Protocol;

namespace DevProjex.Tests.Terminal;

public sealed partial class McpServerProcessTests
{
	[Fact(Timeout = 120_000)]
	public async Task RealProcessBatchRangeReasonsKeepCoverageProtectionAndScalarBoundaries()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("range-project");
		workspace.WriteFile("range-project/TrustMarker.txt", string.Join('\n', Enumerable.Range(1, 35)));
		workspace.WriteFile("range-project/Empty.txt", "");
		workspace.WriteFile("range-project/Hidden.md", "hidden content must stay hidden");
		var secretPath = "Protected-" + Secret + ".txt";
		workspace.WriteFile("range-project/" + secretPath, "one");
		var dataRoot = workspace.CreateDirectory("range-data");
		new ProjectProfileStore(() => dataRoot).SaveProfile(
			project, new ProjectSelectionProfile([], [".txt"], [],
				ExtensionStates: new Dictionary<string, bool> { [".txt"] = true, [".md"] = false },
				SelectedPaths: null));
		await using var server = await ActualMcpProcess.StartAsync(project, dataRoot, ["--live"]);

		var valid = new Dictionary<string, object?>
		{
			["requests"] = new[] { new { path = "TrustMarker.txt", ranges = new[] { new { start_line = 1, end_line = 3 } } } }
		};
		_ = await Call(valid);
		var before = await Call(valid);
		var scalar = await Call(new() { ["path"] = "TrustMarker.txt", ["start_line"] = 180, ["end_line"] = 235 });
		Assert.True(scalar.IsError);
		Assert.Contains("requested line range 180-235 is invalid. Valid lines are 1-35", RangeText(scalar), StringComparison.Ordinal);

		var batch = await Call(new()
		{
			["requests"] = new object[]
			{
				new { path = "TrustMarker.txt", ranges = new[] { new { start_line = 1, end_line = 200 }, new { start_line = 180, end_line = 235 } } },
				new { path = "Empty.txt", ranges = new[] { new { start_line = 1, end_line = 1 } } },
				new { path = "Hidden.md", ranges = new[] { new { start_line = 180, end_line = 235 } } },
				new { path = secretPath, ranges = new[] { new { start_line = 2, end_line = 3 } } }
			}
		});
		Assert.NotEqual(true, batch.IsError);
		var text = RangeText(batch);
		Assert.Contains("1.1 — ok", text, StringComparison.Ordinal);
		Assert.Contains("1.2 — not-returned — TrustMarker.txt: requested lines 180-235; valid lines 1-35", text, StringComparison.Ordinal);
		Assert.Contains("2.1 — not-returned — Empty.txt: requested lines 1-1; file is empty", text, StringComparison.Ordinal);
		Assert.True(text.Contains("3.1 — unavailable — hidden by the saved window filters", StringComparison.Ordinal), text);
		Assert.DoesNotContain("Hidden.md", text, StringComparison.Ordinal);
		Assert.DoesNotContain("hidden content", text, StringComparison.Ordinal);
		Assert.DoesNotContain(Secret, text, StringComparison.Ordinal);
		Assert.Contains("[redacted]", text, StringComparison.Ordinal);
		Assert.Contains("[Batch read] ok=1 · partial=0 · not-returned=3 · unavailable=1.", text, StringComparison.Ordinal);
		Assert.Contains("[Range clamped] requests=1.1; end_line exceeded the file; returned through line 35.", text, StringComparison.Ordinal);
		foreach (var block in batch.Content.OfType<TextContentBlock>())
		{
			var trusted = Regex.Replace(block.Text, @"<untrusted-data-[a-f0-9]+>.*?</untrusted-data-[a-f0-9]+>", "", RegexOptions.Singleline);
			Assert.DoesNotContain("TrustMarker", trusted, StringComparison.Ordinal);
			Assert.DoesNotContain("Empty.txt", trusted, StringComparison.Ordinal);
			Assert.DoesNotContain("Protected-", trusted, StringComparison.Ordinal);
		}

		foreach (var range in new[] { (0, 1), (-1, 1), (3, 2) })
		{
			var invalid = await Call(new()
			{
				["requests"] = new[] { new { path = "TrustMarker.txt", ranges = new[] { new { start_line = range.Item1, end_line = range.Item2 } } } }
			});
			Assert.True(invalid.IsError);
			Assert.Contains("DPX-MCP-INVALID-ARGUMENTS", RangeText(invalid), StringComparison.Ordinal);
		}
		var after = await Call(valid);
		Assert.Equal(NormalizeWrapper(RangeText(before)), NormalizeWrapper(RangeText(after)));

		async Task<CallToolResult> Call(Dictionary<string, object?> arguments) =>
			await server.Client.CallToolAsync("get_file", arguments, progress: null, options: null, TestContext.Current.CancellationToken);
	}

	private static string RangeText(CallToolResult result) => string.Join('\n', result.Content.OfType<TextContentBlock>().Select(static block => block.Text));
	private static string NormalizeWrapper(string text) => Regex.Replace(text, "untrusted-data-[a-f0-9]{24}", "untrusted-data-ID");
}
