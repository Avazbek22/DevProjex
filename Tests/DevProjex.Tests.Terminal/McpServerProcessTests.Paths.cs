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

	[Fact]
	public async Task RealProcessListsOneDirectoryInASingleCallAndReadsAScalarLikeAOneItemList()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("listing-project");
		workspace.WriteFile("listing-project/src/router/router.ts", "export const router = 1\n");
		workspace.WriteFile("listing-project/src/router/trie/node.ts", "export const node = 1\n");
		workspace.WriteFile("listing-project/src/context.ts", "export const context = 1\n");
		workspace.WriteFile("listing-project/docs/guide.md", "guide\n");
		await using var server = await ActualMcpProcess.StartAsync(
			project,
			workspace.CreateDirectory("data"));

		// One call, no depth or pattern tuning, must answer "what is in this directory".
		var listing = await server.Client.CallToolAsync(
			"get_tree",
			new Dictionary<string, object?>
			{
				["paths"] = new[] { "src/router" },
				["format"] = "text"
			},
			progress: null,
			options: null,
			TestContext.Current.CancellationToken);
		var byName = await server.Client.CallToolAsync(
			"get_tree",
			new Dictionary<string, object?>
			{
				["include_patterns"] = new[] { "**/*router*.ts" },
				["format"] = "text"
			},
			progress: null,
			options: null,
			TestContext.Current.CancellationToken);
		var scalar = await server.Client.CallToolAsync(
			"get_tree",
			new Dictionary<string, object?> { ["paths"] = "src/router" },
			progress: null,
			options: null,
			TestContext.Current.CancellationToken);
		var wrongType = await server.Client.CallToolAsync(
			"get_tree",
			new Dictionary<string, object?> { ["paths"] = 7 },
			progress: null,
			options: null,
			TestContext.Current.CancellationToken);

		var listingText = AllProcessText(listing);
		Assert.NotEqual(true, listing.IsError);
		Assert.Contains("router.ts", listingText, StringComparison.Ordinal);
		Assert.Contains("node.ts", listingText, StringComparison.Ordinal);
		Assert.DoesNotContain("context.ts", listingText, StringComparison.Ordinal);
		Assert.DoesNotContain("guide.md", listingText, StringComparison.Ordinal);

		var byNameText = AllProcessText(byName);
		Assert.Contains("router.ts", byNameText, StringComparison.Ordinal);
		Assert.DoesNotContain("node.ts", byNameText, StringComparison.Ordinal);
		Assert.DoesNotContain("guide.md", byNameText, StringComparison.Ordinal);

		// One path written as one string selects what the one-item list selects. The two
		// responses are not compared byte for byte because service notices are reported once
		// per session, so the later call legitimately carries the continuation line instead.
		var scalarText = AllProcessText(scalar);
		Assert.NotEqual(true, scalar.IsError);
		Assert.Contains("router.ts", scalarText, StringComparison.Ordinal);
		Assert.Contains("node.ts", scalarText, StringComparison.Ordinal);
		Assert.DoesNotContain("context.ts", scalarText, StringComparison.Ordinal);
		Assert.DoesNotContain("guide.md", scalarText, StringComparison.Ordinal);

		// A value that is neither a string nor an array of strings is still refused, with the
		// same code, so widening the accepted shape did not widen the accepted types.
		var wrongTypeText = AllProcessText(wrongType);
		Assert.True(wrongType.IsError);
		Assert.Contains("DPX-MCP-INVALID-ARGUMENTS", wrongTypeText, StringComparison.Ordinal);
		Assert.Contains("'paths'", wrongTypeText, StringComparison.Ordinal);
	}

	[Fact]
	public async Task RealProcessCountsTreeDepthFromTheProjectRootWhateverPathsNarrowsTo()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("depth-project");
		workspace.WriteFile("depth-project/src/router/trie/node.ts", "export const node = 1\n");
		await using var server = await ActualMcpProcess.StartAsync(
			project,
			workspace.CreateDirectory("data"));

		async Task<string> TreeAsync(int depth)
		{
			var result = await server.Client.CallToolAsync(
				"get_tree",
				new Dictionary<string, object?>
				{
					["paths"] = new[] { "src/router" },
					["max_depth"] = depth,
					["format"] = "text"
				},
				progress: null,
				options: null,
				TestContext.Current.CancellationToken);
			Assert.NotEqual(true, result.IsError);
			return AllProcessText(result);
		}

		// max_depth counts levels below the project root, never below a paths entry:
		// src is level 1, router level 2, trie level 3 and node.ts level 4.
		var depthOne = await TreeAsync(1);
		var depthTwo = await TreeAsync(2);
		var depthFour = await TreeAsync(4);

		Assert.Contains("src", depthOne, StringComparison.Ordinal);
		Assert.DoesNotContain("router", depthOne, StringComparison.Ordinal);
		Assert.Contains("router", depthTwo, StringComparison.Ordinal);
		Assert.DoesNotContain("trie", depthTwo, StringComparison.Ordinal);
		Assert.Contains("node.ts", depthFour, StringComparison.Ordinal);
	}
}
