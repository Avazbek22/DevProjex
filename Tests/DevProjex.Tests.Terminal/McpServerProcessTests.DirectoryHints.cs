using System.Text.RegularExpressions;
using DevProjex.Infrastructure.ProjectProfiles;
using ModelContextProtocol.Protocol;

namespace DevProjex.Tests.Terminal;

public sealed partial class McpServerProcessTests
{
	[Theory]
	[InlineData("index", "DPX-MCP-PATH-NOT-FOUND: 'src' is a directory; readable entries: alpha.ts, index.ts, sub/. Read one with get_file {\"path\":\"src/index.ts\"}, or list it with get_tree {\"paths\":[\"src\"]}.")]
	[InlineData("first", "DPX-MCP-PATH-NOT-FOUND: 'src' is a directory; readable entries: alpha.ts, zeta.ts. Read one with get_file {\"path\":\"src/alpha.ts\"}, or list it with get_tree {\"paths\":[\"src\"]}.")]
	[InlineData("folders", "DPX-MCP-PATH-NOT-FOUND: 'src' is a directory; readable entries: a/, b/. List it with get_tree {\"paths\":[\"src\"]}.")]
	public async Task RealProcessDirectoryHintsUseImmediateReadableEntriesAndThePreferredFile(string scenario, string expected)
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		var files = scenario switch
		{
			"index" => new[] { "alpha.ts", "index.ts", "sub/child.ts", "sub/deeper/child.ts" },
			"first" => ["zeta.ts", "alpha.ts"],
			_ => ["a/deep/file.ts", "b/file.ts"]
		};
		foreach (var file in files)
			workspace.WriteFile("project/src/" + file, "export const value = 1;\n");
		await using var server = await ActualMcpProcess.StartAsync(project, workspace.CreateDirectory("data"));
		var single = await CallAsync(server, "get_file", new Dictionary<string, object?> { ["path"] = "src" });
		Assert.True(single.IsError);
		Assert.Equal(expected, DirectoryFailureData(single));
		var batch = await CallAsync(server, "get_file", new Dictionary<string, object?> { ["requests"] = new[] { new { path = "src" } } });
		Assert.Contains(expected, AllProcessText(batch), StringComparison.Ordinal);
	}

	[Fact]
	public async Task RealProcessDirectoryHintCapsEntriesAndKeepsNamesInsideTheDataBoundary()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		var names = Enumerable.Range(0, 13).Select(index => $"Zq9DirectoryMarker{index:D2}.cs").ToArray();
		foreach (var name in names)
			workspace.WriteFile("project/src/" + name, "public class Example { }\n");
		await using var server = await ActualMcpProcess.StartAsync(project, workspace.CreateDirectory("data"));
		var result = await CallAsync(server, "get_file", new Dictionary<string, object?> { ["path"] = "src" });
		var message = DirectoryFailureData(result);
		Assert.Contains("readable entries: " + string.Join(", ", names.Take(12)) + " (+1 more).", message, StringComparison.Ordinal);
		Assert.DoesNotContain(names[12], message, StringComparison.Ordinal);
		Assert.Contains("{\"path\":\"src/" + names[0] + "\"}", message, StringComparison.Ordinal);
		foreach (var block in result.Content.OfType<TextContentBlock>())
			Assert.DoesNotContain("Zq9DirectoryMarker", Regex.Replace(block.Text,
				@"<untrusted-data-(?<nonce>[0-9a-f]+)>[\s\S]*?</untrusted-data-\k<nonce>>", "", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2)), StringComparison.Ordinal);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task RealProcessDirectoryHintsNeverRevealIgnoredOrExcludedParents(bool allHidden)
	{
		using var repository = new GitModeRepository();
		using var workspace = new TemporaryDirectory();
		repository.Write(".gitignore", "src/HiddenMarker.cs\nsrc/HiddenParent/**\n");
		repository.Write("src/HiddenMarker.cs", "secret\n");
		repository.Write("src/HiddenParent/child.cs", "secret\n");
		repository.Write("src/Visible.cs", "public class Visible { }\n");
		repository.Write("src/FilteredParent/child.ts", "export const value = 1;\n");
		await using var server = await ActualMcpProcess.StartAsync(repository.Root, workspace.Path,
			["--live", "--git-mode", "gitignore", "--exclude", "none"], environment: GitModeEnvironment(repository));
		var listed = await CallAsync(server, "list_projects", new Dictionary<string, object?>());
		var canonical = Assert.Single(Structured(listed).GetProperty("projects").EnumerateArray()).GetProperty("path").GetString()!;
		var ignoreStates = Enum.GetValues<IgnoreOptionId>().ToDictionary(static key => key, static _ => false);
		ignoreStates[IgnoreOptionId.UseGitIgnore] = true;
		Assert.True(new ProjectProfileStore(() => workspace.Path).TrySaveProfile(canonical,
			new ProjectSelectionProfile([], [".cs"], [IgnoreOptionId.UseGitIgnore],
				ExtensionStates: new Dictionary<string, bool> { [".cs"] = true, [".ts"] = false },
				IgnoreOptionStates: ignoreStates, SelectedPaths: allHidden ? ["Other.cs"] : null)));
		var hiddenFile = await CallAsync(server, "get_file", new Dictionary<string, object?> { ["path"] = "src/FilteredParent/child.ts" });
		Assert.True(hiddenFile.IsError, AllProcessText(hiddenFile));
		var result = await CallAsync(server, "get_file", new Dictionary<string, object?> { ["path"] = "src" });
		var message = DirectoryFailureData(result);
		Assert.DoesNotContain("HiddenMarker", message, StringComparison.Ordinal);
		Assert.DoesNotContain("HiddenParent", message, StringComparison.Ordinal);
		Assert.DoesNotContain("FilteredParent", message, StringComparison.Ordinal);
		if (allHidden)
			Assert.Equal("DPX-MCP-PATH-NOT-FOUND: 'src' is a directory; provide a file path returned by get_tree or search_project, or list it with get_tree {\"paths\":[\"src\"]}.", message);
		else
			Assert.Contains("readable entries: Visible.cs.", message, StringComparison.Ordinal);
	}

	private static string DirectoryFailureData(CallToolResult result) => Regex.Match(AllProcessText(result),
		@"<untrusted-data-[0-9a-f]+>\n(?<message>[\s\S]*?)\n</untrusted-data-[0-9a-f]+>",
		RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2)).Groups["message"].Value;
}
