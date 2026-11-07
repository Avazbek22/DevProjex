using ModelContextProtocol.Protocol;

namespace DevProjex.Tests.Integration;

public sealed partial class McpServerIntegrationTests
{
	[Fact]
	public async Task GetFileOnADirectoryNamesItsReadableEntriesAndSuggestsItsEntryFile()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		WriteProjectFile(project, "src/helper/conninfo/types.ts", "export type AddressType = string;\n");
		WriteProjectFile(project, "src/helper/conninfo/index.ts", "export * from './types';\n");
		WriteProjectFile(project, "src/helper/conninfo/nested/deep.ts", "export const deep = 1;\n");
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);

		var directory = await server.CallAsync(
			"get_file",
			new Dictionary<string, object?> { ["path"] = "src/helper/conninfo" });

		Assert.True(directory.IsError);
		var body = ExtractSpotlightBody(Text(directory));
		Assert.Contains(
			"DPX-MCP-PATH-NOT-FOUND: 'src/helper/conninfo' is a directory; readable entries: index.ts, nested/, types.ts. " +
			"Read one with get_file {\"path\":\"src/helper/conninfo/index.ts\"}, " +
			"or list it with get_tree {\"paths\":[\"src/helper/conninfo\"]}.",
			body,
			StringComparison.Ordinal);

		var suggested = await server.CallAsync(
			"get_file",
			new Dictionary<string, object?> { ["path"] = "src/helper/conninfo/index.ts" });
		Assert.NotEqual(true, suggested.IsError);
		Assert.Contains("export * from './types';", Text(suggested), StringComparison.Ordinal);
	}

	[Fact]
	public async Task GetFileOnADirectoryWithoutAnEntryFileSuggestsItsFirstFile()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		WriteProjectFile(project, "lib/zeta.py", "ZETA = 1\n");
		WriteProjectFile(project, "lib/alpha.py", "ALPHA = 1\n");
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);

		var directory = await server.CallAsync(
			"get_file",
			new Dictionary<string, object?> { ["path"] = "lib" });

		Assert.True(directory.IsError);
		Assert.Contains(
			"'lib' is a directory; readable entries: alpha.py, zeta.py. Read one with get_file {\"path\":\"lib/alpha.py\"}, " +
			"or list it with get_tree {\"paths\":[\"lib\"]}.",
			ExtractSpotlightBody(Text(directory)),
			StringComparison.Ordinal);
	}

	[Fact]
	public async Task GetFileOnADirectoryOfFoldersOnlyGivesTheListingCall()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		WriteProjectFile(project, "packages/web/app.ts", "export const app = 1;\n");
		WriteProjectFile(project, "packages/api/server.ts", "export const server = 1;\n");
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);

		var directory = await server.CallAsync(
			"get_file",
			new Dictionary<string, object?> { ["path"] = "packages" });

		Assert.True(directory.IsError);
		var body = ExtractSpotlightBody(Text(directory));
		Assert.Contains(
			"'packages' is a directory; readable entries: api/, web/. List it with get_tree {\"paths\":[\"packages\"]}.",
			body,
			StringComparison.Ordinal);
		Assert.DoesNotContain("Read one with get_file", body, StringComparison.Ordinal);
	}

	[Fact]
	public async Task GetFileOnALargeDirectoryNamesTwelveEntriesAndCountsTheRest()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		for (var index = 0; index < 15; index++)
			WriteProjectFile(project, $"many/file{index:00}.cs", $"class File{index:00} {{ }}\n");
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);

		var directory = await server.CallAsync(
			"get_file",
			new Dictionary<string, object?> { ["path"] = "many" });

		Assert.True(directory.IsError);
		var expectedEntries = string.Join(", ", Enumerable.Range(0, 12).Select(static index => $"file{index:00}.cs"));
		Assert.Contains(
			$"'many' is a directory; readable entries: {expectedEntries} (+3 more). " +
			"Read one with get_file {\"path\":\"many/file00.cs\"}",
			ExtractSpotlightBody(Text(directory)),
			StringComparison.Ordinal);
	}

	[Fact]
	public async Task GetFileOnADirectoryNeverNamesFilesOutsideTheEffectiveSelection()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		WriteProjectFile(project, "src/visible.ts", "export const visible = 1;\n");
		WriteProjectFile(project, "src/HiddenMarkerQz7.ts", "export const hidden = 1;\n");
		WriteProjectFile(project, "src/HiddenFolderQz7/inner.ts", "export const inner = 1;\n");
		WriteProjectFile(project, "only-hidden/HiddenOnlyQz7.ts", "export const other = 1;\n");
		var profile = WritePortableProfile(project, ".focus-profile.json", ["src/visible.ts"]);
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);

		var directory = await server.CallAsync(
			"get_file",
			new Dictionary<string, object?> { ["path"] = "src", ["profile"] = profile });
		var batch = await server.CallAsync(
			"get_file",
			new Dictionary<string, object?>
			{
				["profile"] = profile,
				["requests"] = new object[] { new Dictionary<string, object?> { ["path"] = "src" } }
			});

		Assert.True(directory.IsError);
		var text = AllText(directory);
		Assert.Contains(
			"'src' is a directory; readable entries: visible.ts. Read one with get_file {\"path\":\"src/visible.ts\"}",
			text,
			StringComparison.Ordinal);
		Assert.DoesNotContain("Qz7", text, StringComparison.Ordinal);
		Assert.DoesNotContain("Qz7", AllText(batch), StringComparison.Ordinal);
	}

	[Fact]
	public async Task BatchedGetFileOnADirectoryPrintsTheEntriesInsideTheUntrustedBlock()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		WriteProjectFile(project, "src/helper/index.ts", "export const helperEntry = 1;\n");
		WriteProjectFile(project, "src/main.ts", "export const mainEntry = 1;\n");
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);

		var batch = await server.CallAsync(
			"get_file",
			new Dictionary<string, object?>
			{
				["requests"] = new object[]
				{
					new Dictionary<string, object?> { ["path"] = "src/helper" },
					new Dictionary<string, object?> { ["path"] = "src/main.ts" }
				}
			});

		Assert.NotEqual(true, batch.IsError);
		var body = ExtractSpotlightBody(Text(batch));
		Assert.Contains(
			"'src/helper' is a directory; readable entries: index.ts. Read one with get_file {\"path\":\"src/helper/index.ts\"}",
			body,
			StringComparison.Ordinal);
		Assert.Contains("export const mainEntry = 1;", body, StringComparison.Ordinal);
		var text = Text(batch);
		var outside = text[..text.IndexOf("<untrusted-data-", StringComparison.Ordinal)] +
					  text[(text.LastIndexOf("</untrusted-data-", StringComparison.Ordinal) + 1)..];
		Assert.DoesNotContain("index.ts", outside, StringComparison.Ordinal);
	}

	[Fact]
	public async Task BatchedGetFileOnADirectoryKeepsEachStatusOnOneLine()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		WriteProjectFile(project, "pkg/a\u2028b.ts", "export const a = 1;\n");
		// Windows does not permit line-feed characters in file names.
		var lineFeedName = !OperatingSystem.IsWindows();
		if (lineFeedName)
			WriteProjectFile(project, "pkg/c\nd.ts", "export const c = 1;\n");
		WriteProjectFile(project, "main.ts", "export const mainEntry = 1;\n");
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);

		var batch = await server.CallAsync(
			"get_file",
			new Dictionary<string, object?>
			{
				["requests"] = new object[]
				{
					new Dictionary<string, object?> { ["path"] = "pkg" },
					new Dictionary<string, object?> { ["path"] = "main.ts" }
				}
			});

		Assert.NotEqual(true, batch.IsError);
		var body = ExtractSpotlightBody(Text(batch));
		Assert.DoesNotContain('\u2028', body);
		var lines = body.Split('\n');
		var status = Assert.Single(lines, static line => line.StartsWith("1.1 — ", StringComparison.Ordinal));
		Assert.Contains("a\\u2028b.ts", status, StringComparison.Ordinal);
		if (lineFeedName)
			Assert.Contains("c\\nd.ts", status, StringComparison.Ordinal);
		Assert.Equal(1, Array.IndexOf(lines, status));
		Assert.Equal("2.1 — ok", lines[2]);
	}

	[Fact]
	public async Task GetFileOnADirectoryWithNothingReadableKeepsThePlainRefusal()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		WriteProjectFile(project, "kept/main.ts", "export const kept = 1;\n");
		WriteProjectFile(project, "dropped/SecretNameQz8.ts", "export const dropped = 1;\n");
		var profile = WritePortableProfile(project, ".focus-profile.json", ["kept/main.ts"]);
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);

		var directory = await server.CallAsync(
			"get_file",
			new Dictionary<string, object?> { ["path"] = "dropped", ["profile"] = profile });

		Assert.True(directory.IsError);
		var text = AllText(directory);
		Assert.DoesNotContain("Qz8", text, StringComparison.Ordinal);
		Assert.DoesNotContain("readable entries", text, StringComparison.Ordinal);
	}

	[Fact]
	public async Task GetFileOnADirectoryKeepsEntryNamesInsideTheUntrustedBlock()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		WriteProjectFile(project, "pkg/TrustMarkerQz9.ts", "export const marker = 1;\n");
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);

		var directory = await server.CallAsync(
			"get_file",
			new Dictionary<string, object?> { ["path"] = "pkg" });

		Assert.True(directory.IsError);
		foreach (var block in directory.Content.OfType<TextContentBlock>())
		{
			var outside = System.Text.RegularExpressions.Regex.Replace(
				block.Text,
				"<untrusted-data-[0-9a-f]{24}>[\\s\\S]*?</untrusted-data-[0-9a-f]{24}>",
				string.Empty);
			Assert.DoesNotContain("TrustMarkerQz9", outside, StringComparison.Ordinal);
		}
		Assert.Contains("TrustMarkerQz9.ts", AllText(directory), StringComparison.Ordinal);
	}

	[Fact]
	public async Task GetTreeReportsFoldersCollapsedByMaxDepthUnderAVisibleRequestedPath()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		WriteProjectFile(project, "src/middleware/cors/index.ts", "export const cors = 1;\n");
		WriteProjectFile(project, "src/middleware/etag/index.ts", "export const etag = 1;\n");
		WriteProjectFile(project, "src/middleware/etag/digest.ts", "export const digest = 1;\n");
		WriteProjectFile(project, "src/middleware/index.ts", "export * from './cors';\n");
		WriteProjectFile(project, "src/other/deep/file.ts", "export const other = 1;\n");
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);

		var collapsed = await server.CallAsync(
			"get_tree",
			new Dictionary<string, object?> { ["paths"] = new[] { "src/middleware" }, ["max_depth"] = 3 });
		var overlapping = await server.CallAsync(
			"get_tree",
			new Dictionary<string, object?>
			{
				["paths"] = new[] { "src/middleware", "src/middleware/etag" },
				["max_depth"] = 3
			});
		var opened = await server.CallAsync(
			"get_tree",
			new Dictionary<string, object?> { ["paths"] = new[] { "src/middleware" }, ["max_depth"] = 4 });
		var wholeProject = await server.CallAsync(
			"get_tree",
			new Dictionary<string, object?> { ["max_depth"] = 2 });

		const string trailer =
			"[Tree depth] max_depth counts from the project root; 2 folder(s) under the requested paths reach depth 3 " +
			"and are shown without their contents. Omit max_depth, or pass 4 to open them.";
		Assert.NotEqual(true, collapsed.IsError);
		AssertTrustedTrailerOutsideSpotlight(collapsed, trailer);
		var collapsedText = AllText(collapsed);
		var trustedTail = collapsedText[(collapsedText.LastIndexOf("</untrusted-data-", StringComparison.Ordinal) + 1)..];
		Assert.DoesNotContain("middleware", trustedTail, StringComparison.Ordinal);

		// A requested path below the depth limit keeps the existing hidden-path wording, which
		// already gives the depth that shows it.
		Assert.NotEqual(true, overlapping.IsError);
		Assert.Contains("requested path(s) sit at depth", AllText(overlapping), StringComparison.Ordinal);
		Assert.DoesNotContain("folder(s) under the requested paths", AllText(overlapping), StringComparison.Ordinal);

		Assert.NotEqual(true, opened.IsError);
		Assert.DoesNotContain("[Tree depth]", AllText(opened), StringComparison.Ordinal);
		Assert.NotEqual(true, wholeProject.IsError);
		Assert.DoesNotContain("[Tree depth]", AllText(wholeProject), StringComparison.Ordinal);
	}

	[Fact]
	public async Task GetTreeDoesNotCountEmptyFoldersAtTheDepthLimit()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		WriteProjectFile(project, "src/lib/main.ts", "export const main = 1;\n");
		Directory.CreateDirectory(Path.Combine(project, "src", "lib", "empty"));
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);

		var tree = await server.CallAsync(
			"get_tree",
			new Dictionary<string, object?> { ["paths"] = new[] { "src" }, ["max_depth"] = 3 });

		Assert.NotEqual(true, tree.IsError);
		Assert.DoesNotContain("[Tree depth]", AllText(tree), StringComparison.Ordinal);
	}

	private static void WriteProjectFile(string project, string relativePath, string content)
	{
		var path = Path.Combine(project, relativePath.Replace('/', Path.DirectorySeparatorChar));
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, content);
	}
}
