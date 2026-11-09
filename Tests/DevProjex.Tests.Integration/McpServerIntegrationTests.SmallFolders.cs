using ModelContextProtocol.Protocol;

namespace DevProjex.Tests.Integration;

public sealed partial class McpServerIntegrationTests
{
	private const string SmallFoldersTrailer =
		"[Tree depth] folders past max_depth with up to 12 entries are listed in full.";

	[Theory]
	[InlineData("markdown")]
	[InlineData("text")]
	public async Task GetTreeListsASmallFolderPastMaxDepthInFull(string format)
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		WriteProjectFile(project, "pkg/core.py", "core = 1\n");
		for (var index = 0; index < 12; index++)
			WriteProjectFile(project, $"pkg/transports/transport{index:D2}.py", "value = 1\n");
		for (var index = 0; index < 13; index++)
			WriteProjectFile(project, $"pkg/large/module{index:D2}.py", "value = 1\n");
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);

		var tree = await server.CallAsync(
			"get_tree",
			new Dictionary<string, object?> { ["max_depth"] = 2, ["format"] = format });

		Assert.NotEqual(true, tree.IsError);
		var body = ExtractSpotlightBody(Text(tree));
		for (var index = 0; index < 12; index++)
			Assert.Contains($"transport{index:D2}.py", body, StringComparison.Ordinal);
		Assert.Contains("large", body, StringComparison.Ordinal);
		Assert.DoesNotContain("module00.py", body, StringComparison.Ordinal);
		AssertTrustedTrailerOutsideSpotlight(tree, SmallFoldersTrailer);
		var text = AllText(tree);
		var trustedTail = text[(text.LastIndexOf("</untrusted-data-", StringComparison.Ordinal) + 1)..];
		Assert.DoesNotContain("pkg", trustedTail, StringComparison.Ordinal);
		Assert.DoesNotContain("transports", trustedTail, StringComparison.Ordinal);
		Assert.Single(System.Text.RegularExpressions.Regex.Matches(text, System.Text.RegularExpressions.Regex.Escape("[Tree depth]")));

		// The expanded lines are exactly the lines the complete tree renders for that folder.
		var complete = await server.CallAsync(
			"get_tree",
			new Dictionary<string, object?> { ["format"] = format });
		var completeLines = ExtractSpotlightBody(Text(complete)).ReplaceLineEndings("\n").Split('\n');
		foreach (var line in body.ReplaceLineEndings("\n").Split('\n').Where(static line => line.Contains("transport", StringComparison.Ordinal)))
			Assert.Contains(line, completeLines);
	}

	[Fact]
	public async Task GetTreeCountsFoldersAndFilesRecursivelyAndListsANestedSmallFolderWhole()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		WriteProjectFile(project, "pkg/nested/top.py", "top = 1\n");
		WriteProjectFile(project, "pkg/nested/inner/leaf.py", "leaf = 1\n");
		WriteProjectFile(project, "pkg/nested/inner/deeper/bottom.py", "bottom = 1\n");
		// Eleven files and one folder holding one file make thirteen entries.
		for (var index = 0; index < 11; index++)
			WriteProjectFile(project, $"pkg/thirteen/file{index:D2}.py", "value = 1\n");
		WriteProjectFile(project, "pkg/thirteen/sub/hidden-by-size.py", "value = 1\n");
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);

		var tree = await server.CallAsync(
			"get_tree",
			new Dictionary<string, object?> { ["max_depth"] = 2, ["format"] = "text" });

		Assert.NotEqual(true, tree.IsError);
		var body = ExtractSpotlightBody(Text(tree));
		Assert.Contains("top.py", body, StringComparison.Ordinal);
		Assert.Contains("inner", body, StringComparison.Ordinal);
		Assert.Contains("leaf.py", body, StringComparison.Ordinal);
		Assert.Contains("deeper", body, StringComparison.Ordinal);
		Assert.Contains("bottom.py", body, StringComparison.Ordinal);
		Assert.Contains("thirteen", body, StringComparison.Ordinal);
		Assert.DoesNotContain("file00.py", body, StringComparison.Ordinal);
		Assert.DoesNotContain("hidden-by-size.py", body, StringComparison.Ordinal);
		AssertTrustedTrailerOutsideSpotlight(tree, SmallFoldersTrailer);
	}

	[Fact]
	public async Task GetTreeSpendsAtMostSixtyExtraLinesOnSmallFoldersInTreeOrder()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		for (var folder = 0; folder < 10; folder++)
		{
			for (var file = 0; file < 10; file++)
				WriteProjectFile(project, $"dir{folder:D2}/f{folder:D2}-{file:D2}.txt", string.Empty);
		}
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);

		var expanded = await server.CallAsync(
			"get_tree",
			new Dictionary<string, object?> { ["max_depth"] = 1, ["format"] = "text" });
		var plain = await server.CallAsync(
			"get_tree",
			new Dictionary<string, object?> { ["max_depth"] = 1, ["format"] = "json" });

		Assert.NotEqual(true, expanded.IsError);
		var body = ExtractSpotlightBody(Text(expanded));
		var lineCount = body.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n').Length;
		// The address line and ten folders, plus six folders of ten files each.
		Assert.Equal(1 + 10 + 60, lineCount);
		for (var folder = 0; folder < 10; folder++)
		{
			Assert.Contains($"dir{folder:D2}", body, StringComparison.Ordinal);
			if (folder < 6)
				Assert.Contains($"f{folder:D2}-09.txt", body, StringComparison.Ordinal);
			else
				Assert.DoesNotContain($"f{folder:D2}-00.txt", body, StringComparison.Ordinal);
		}
		AssertTrustedTrailerOutsideSpotlight(expanded, SmallFoldersTrailer);

		// Structured formats keep the exact max_depth document.
		Assert.NotEqual(true, plain.IsError);
		Assert.DoesNotContain("f00-00.txt", Text(plain), StringComparison.Ordinal);
		Assert.DoesNotContain("[Tree depth]", AllText(plain), StringComparison.Ordinal);
	}

	[Fact]
	public async Task GetTreeDoesNotExpandDotFoldersOrFoldersUnderThem()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		WriteProjectFile(project, ".github/workflows/ci.yml", "on: push\n");
		WriteProjectFile(project, ".github/workflows/release.yml", "on: push\n");
		WriteProjectFile(project, ".github/.config/settings.yml", "a: 1\n");
		WriteProjectFile(project, "pkg/small/one.py", "one = 1\n");
		WriteProjectFile(project, "pkg/small/two.py", "two = 1\n");
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);

		var tree = await server.CallAsync(
			"get_tree",
			new Dictionary<string, object?> { ["max_depth"] = 2, ["format"] = "text" });
		var dotOnly = await server.CallAsync(
			"get_tree",
			new Dictionary<string, object?>
			{
				["paths"] = new[] { ".github" },
				["max_depth"] = 2,
				["format"] = "text"
			});
		var dotContents = await server.CallAsync(
			"get_tree",
			new Dictionary<string, object?> { ["paths"] = new[] { ".github/workflows" }, ["format"] = "text" });

		Assert.NotEqual(true, tree.IsError);
		var body = ExtractSpotlightBody(Text(tree));
		Assert.Contains("workflows", body, StringComparison.Ordinal);
		Assert.DoesNotContain("ci.yml", body, StringComparison.Ordinal);
		Assert.DoesNotContain("settings.yml", body, StringComparison.Ordinal);
		Assert.Contains("one.py", body, StringComparison.Ordinal);
		Assert.Contains("two.py", body, StringComparison.Ordinal);
		AssertTrustedTrailerOutsideSpotlight(tree, SmallFoldersTrailer);

		Assert.NotEqual(true, dotOnly.IsError);
		Assert.DoesNotContain("ci.yml", ExtractSpotlightBody(Text(dotOnly)), StringComparison.Ordinal);
		Assert.DoesNotContain(SmallFoldersTrailer, AllText(dotOnly), StringComparison.Ordinal);

		Assert.NotEqual(true, dotContents.IsError);
		Assert.Contains("ci.yml", ExtractSpotlightBody(Text(dotContents)), StringComparison.Ordinal);
		Assert.Contains("release.yml", ExtractSpotlightBody(Text(dotContents)), StringComparison.Ordinal);
	}

	[Fact]
	public async Task GetTreeExpansionShowsNothingTheSelectionHides()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		WriteProjectFile(project, "app/small/keep.ts", "export const keep = 1;\n");
		WriteProjectFile(project, "app/small/drop.log", "log\n");
		WriteProjectFile(project, "app/small/node_modules/pkg/index.js", "module.exports = 1;\n");
		WriteProjectFile(project, "app/small/bin/Debug/out.dll", "binary\n");
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);

		var arguments = new Dictionary<string, object?>
		{
			["max_depth"] = 2,
			["format"] = "text",
			["exclude_patterns"] = new[] { "**/*.log" }
		};
		var expanded = await server.CallAsync("get_tree", arguments);
		var complete = await server.CallAsync(
			"get_tree",
			new Dictionary<string, object?> { ["format"] = "text", ["exclude_patterns"] = new[] { "**/*.log" } });

		Assert.NotEqual(true, expanded.IsError);
		var body = ExtractSpotlightBody(Text(expanded));
		Assert.Contains("keep.ts", body, StringComparison.Ordinal);
		Assert.DoesNotContain("drop.log", body, StringComparison.Ordinal);
		AssertTrustedTrailerOutsideSpotlight(expanded, SmallFoldersTrailer);
		// Every expanded entry is one the complete tree of the same selection already lists.
		Assert.Equal(
			ExtractSpotlightBody(Text(complete)).ReplaceLineEndings("\n"),
			body.ReplaceLineEndings("\n"));
	}

	[Fact]
	public async Task GetTreeKeepsTheExactTreeWithoutExpansionForDepthZeroAndFilePaths()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		WriteProjectFile(project, "pkg/small/one.py", "one = 1\n");
		WriteProjectFile(project, "pkg/small/two.py", "two = 1\n");
		WriteProjectFile(project, "top.py", "top = 1\n");
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);

		var depthZero = await server.CallAsync(
			"get_tree",
			new Dictionary<string, object?> { ["max_depth"] = 0, ["format"] = "text" });
		var filePath = await server.CallAsync(
			"get_tree",
			new Dictionary<string, object?>
			{
				["paths"] = new[] { "pkg/small/one.py" },
				["max_depth"] = 2,
				["format"] = "text"
			});
		var nothingSmall = await server.CallAsync(
			"get_tree",
			new Dictionary<string, object?> { ["max_depth"] = 3, ["format"] = "text" });

		Assert.NotEqual(true, depthZero.IsError);
		Assert.DoesNotContain("pkg", ExtractSpotlightBody(Text(depthZero)), StringComparison.Ordinal);
		Assert.DoesNotContain(SmallFoldersTrailer, AllText(depthZero), StringComparison.Ordinal);

		Assert.NotEqual(true, filePath.IsError);
		Assert.DoesNotContain("one.py", ExtractSpotlightBody(Text(filePath)), StringComparison.Ordinal);
		Assert.DoesNotContain(SmallFoldersTrailer, AllText(filePath), StringComparison.Ordinal);
		Assert.Contains("requested file(s) sit at depth 3", AllText(filePath), StringComparison.Ordinal);

		// A requested folder at exactly max_depth is listed in full, so it no longer reads as hidden.
		var requestedFolder = await server.CallAsync(
			"get_tree",
			new Dictionary<string, object?>
			{
				["paths"] = new[] { "pkg/small" },
				["max_depth"] = 2,
				["format"] = "text"
			});
		Assert.NotEqual(true, requestedFolder.IsError);
		Assert.Contains("one.py", ExtractSpotlightBody(Text(requestedFolder)), StringComparison.Ordinal);
		Assert.DoesNotContain("requested path(s)", AllText(requestedFolder), StringComparison.Ordinal);
		AssertTrustedTrailerOutsideSpotlight(requestedFolder, SmallFoldersTrailer);

		Assert.NotEqual(true, nothingSmall.IsError);
		Assert.Contains("two.py", ExtractSpotlightBody(Text(nothingSmall)), StringComparison.Ordinal);
		Assert.DoesNotContain("[Tree depth]", AllText(nothingSmall), StringComparison.Ordinal);
	}
}
