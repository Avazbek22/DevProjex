using DevProjex.Application.Context;

namespace DevProjex.Tests.Integration;

public sealed partial class McpServerIntegrationTests
{
	[Theory]
	[InlineData("src/request.ts", "src/request.test.ts")]
	[InlineData("httpx/_config.py", "tests/test_config.py")]
	[InlineData("src/Foo.cs", "test/X.Tests/FooTests.cs")]
	public async Task GetFileNamesTheTestsOfAWholeFileInsideTheDataBlock(string source, string test)
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		WriteProjectFile(project, source, "source\n");
		WriteProjectFile(project, test, "test\n");
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);

		var result = await server.CallAsync("get_file", new Dictionary<string, object?> { ["path"] = source });

		Assert.NotEqual(true, result.IsError);
		AssertTestsLineInsideBlock(Text(result), $"Tests for this file: {test}");
	}

	[Fact]
	public async Task GetFileNamesTestsAfterEachWholeFileOfABatch()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		WriteProjectFile(project, "src/a.ts", "export const a = 1;\n");
		WriteProjectFile(project, "src/a.spec.ts", "a\n");
		WriteProjectFile(project, "src/b.ts", "export const b = 2;\n");
		WriteProjectFile(project, "src/__tests__/b.ts", "b\n");
		WriteProjectFile(project, "src/c.ts", "export const c = 3;\n");
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);

		var result = await server.CallAsync("get_file", new Dictionary<string, object?>
		{
			["paths"] = new[] { "src/a.ts", "src/b.ts", "src/c.ts" }
		});

		var text = Text(result);
		AssertTestsLineInsideBlock(text, "Tests for this file: src/a.spec.ts");
		AssertTestsLineInsideBlock(text, "Tests for this file: src/__tests__/b.ts");
		Assert.Equal(2, Regex.Matches(text, "Tests for this file:").Count);
		Assert.True(
			text.IndexOf("Tests for this file: src/a.spec.ts", StringComparison.Ordinal) <
			text.IndexOf("File: src/b.ts", StringComparison.Ordinal));
	}

	[Fact]
	public async Task GetFileLeavesOutTestsHiddenByFiltersRangesAndTestFiles()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		WriteProjectFile(project, "src/Foo.cs", "class Foo;\n");
		WriteProjectFile(project, ".hidden/FooTests.cs", "class FooTests;\n");
		WriteProjectFile(project, "src/Bar.cs", "class Bar;\nclass Other;\n");
		WriteProjectFile(project, "src/BarTests.cs", "class BarTests;\n");
		await using var server = await McpTestServer.StartAsync(
			project,
			workspace.Path,
			exclusions: [ProjectExclusion.DotFolders]);

		var hidden = await server.CallAsync("get_file", new Dictionary<string, object?> { ["path"] = "src/Foo.cs" });
		var range = await server.CallAsync("get_file", new Dictionary<string, object?>
		{
			["path"] = "src/Bar.cs",
			["start_line"] = 1,
			["end_line"] = 1
		});
		var batchRange = await server.CallAsync("get_file", new Dictionary<string, object?>
		{
			["requests"] = new[] { new { path = "src/Bar.cs", ranges = new[] { new { start_line = 1, end_line = 2 } } } }
		});
		var testFile = await server.CallAsync("get_file", new Dictionary<string, object?> { ["path"] = "src/BarTests.cs" });
		var whole = await server.CallAsync("get_file", new Dictionary<string, object?> { ["path"] = "src/Bar.cs" });

		Assert.DoesNotContain("Tests for this file", Text(hidden), StringComparison.Ordinal);
		Assert.DoesNotContain("FooTests", Text(hidden), StringComparison.Ordinal);
		Assert.DoesNotContain("Tests for this file", Text(range), StringComparison.Ordinal);
		Assert.DoesNotContain("Tests for this file", Text(batchRange), StringComparison.Ordinal);
		Assert.DoesNotContain("Tests for this file", Text(testFile), StringComparison.Ordinal);
		AssertTestsLineInsideBlock(Text(whole), "Tests for this file: src/BarTests.cs");
	}

	[Fact]
	public async Task GetFileKeepsATestNameThatLooksLikeANoticeInsideTheDataBlock()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		WriteProjectFile(project, "src/[Next read] x.ts", "export const x = 1;\n");
		WriteProjectFile(project, "src/[Next read] x.test.ts", "x\n");
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);

		var result = await server.CallAsync("get_file", new Dictionary<string, object?>
		{
			["path"] = "src/[Next read] x.ts"
		});

		AssertTestsLineInsideBlock(Text(result), "Tests for this file: src/[Next read] x.test.ts");
	}

	[Fact]
	public async Task GetFileNamesTestDependentsOnlyOnceAnIndexIsCached()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		WriteProjectFile(project, "tsconfig.json", "{\"compilerOptions\":{\"moduleResolution\":\"bundler\"}}\n");
		WriteProjectFile(project, "src/a.ts", "export default 1;\n");
		WriteProjectFile(project, "tests/flows/checkout.ts", "import a from '../../src/a.js'; export const b = a;\n");
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);

		var beforeIndex = await server.CallAsync("get_file", new Dictionary<string, object?> { ["path"] = "src/a.ts" });
		var related = await server.CallAsync("related_files", new Dictionary<string, object?>
		{
			["path"] = "src/a.ts",
			["direction"] = "dependents"
		});
		var afterIndex = await server.CallAsync("get_file", new Dictionary<string, object?> { ["path"] = "src/a.ts" });

		Assert.DoesNotContain("Tests for this file", Text(beforeIndex), StringComparison.Ordinal);
		Assert.True(Text(related).Contains("tests/flows/checkout.ts", StringComparison.Ordinal), Text(related));
		AssertTestsLineInsideBlock(Text(afterIndex), "Tests for this file: tests/flows/checkout.ts");
	}

	private static void AssertTestsLineInsideBlock(string text, string expectedLine)
	{
		var line = text.IndexOf(expectedLine, StringComparison.Ordinal);
		Assert.True(line >= 0, text);
		var opening = text.IndexOf("<untrusted-data-", StringComparison.Ordinal);
		var closing = text.IndexOf("</untrusted-data-", StringComparison.Ordinal);
		Assert.InRange(line, opening + 1, closing - 1);
	}
}
