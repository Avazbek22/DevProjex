using System.Text.RegularExpressions;

namespace DevProjex.Tests.Terminal;

public sealed partial class McpServerProcessTests
{
	[Theory]
	[InlineData(true, true, false)]
	[InlineData(true, true, true)]
	[InlineData(true, false, false)]
	[InlineData(false, true, false)]
	[InlineData(false, false, false)]
	public async Task RealProcessCountsCollapsedFoldersOnlyForExplicitDepthAndPaths(bool paths, bool depth, bool overlap)
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		for (var index = 0; index < 26; index++)
			workspace.WriteFile($"project/src/middleware/Folder{index:D2}/File.cs", "public class Example { }\n");
		workspace.WriteFile("project/src/middleware/Excluded/hidden.cs", "hidden\n");
		workspace.CreateDirectory("project/src/middleware/Empty");
		await using var server = await ActualMcpProcess.StartAsync(project, workspace.CreateDirectory("data"));
		var arguments = new Dictionary<string, object?> { ["format"] = "text", ["exclude_patterns"] = new[] { "**/hidden.cs" } };
		if (paths)
			arguments["paths"] = overlap ? new[] { "src", "src/middleware", "src/middleware" } : new[] { "src/middleware" };
		if (depth)
			arguments["max_depth"] = 3;
		var result = await CallAsync(server, "get_tree", arguments);
		Assert.False(result.IsError is true, AllProcessText(result));
		var text = AllProcessText(result);
		var notice = "[Tree depth] max_depth counts from the project root; 26 folder(s) under the requested paths reach depth 3 and are shown without their contents. Omit max_depth, or pass 4 to open them.";
		if (paths && depth)
		{
			Assert.Contains(notice, text, StringComparison.Ordinal);
			Assert.DoesNotContain("File.cs", text, StringComparison.Ordinal);
			var trusted = Regex.Replace(text, @"<untrusted-data-(?<nonce>[0-9a-f]+)>[\s\S]*?</untrusted-data-\k<nonce>>", "", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
			Assert.DoesNotContain("Folder", trusted, StringComparison.Ordinal);
		}
		else
			Assert.DoesNotContain("[Tree depth]", text, StringComparison.Ordinal);
		Assert.DoesNotContain("hidden.cs", text, StringComparison.Ordinal);
	}

	[Fact]
	public async Task RealProcessKeepsExistingDepthAdviceForHiddenRequestedDirectoriesAndFiles()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/src/router/trie/File.cs", "public class Example { }\n");
		await using var server = await ActualMcpProcess.StartAsync(project, workspace.CreateDirectory("data"));
		var directory = AllProcessText(await CallAsync(server, "get_tree", new Dictionary<string, object?>
			{ ["paths"] = new[] { "src/router" }, ["max_depth"] = 1 }));
		Assert.Contains("[Tree depth] max_depth counts from the project root; 1 requested path(s) sit at depth 2 or deeper, so nothing under them is shown. Omit max_depth, or pass 4 to show two levels below them.", directory, StringComparison.Ordinal);
		var file = AllProcessText(await CallAsync(server, "get_tree", new Dictionary<string, object?>
			{ ["paths"] = new[] { "src/router/trie/File.cs" }, ["max_depth"] = 3 }));
		Assert.Contains("[Tree depth] max_depth counts from the project root; 1 requested file(s) sit at depth 4 or deeper, so they are not shown. Omit max_depth, or pass 4 to show them.", file, StringComparison.Ordinal);
	}
}
