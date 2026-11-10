using ModelContextProtocol.Protocol;

namespace DevProjex.Tests.Terminal;

public sealed partial class McpServerProcessTests
{
	[Fact]
	public async Task RealProcessPreservesResponsesWithoutExportDeclarations()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("plain-project");
		workspace.WriteFile("plain-project/src/main.ts", "import { value } from './value';\nexport const main = value;\n");
		workspace.WriteFile("plain-project/src/value.ts", "export const value = 1;\n");
		await using var current = await ActualMcpProcess.StartAsync(project, workspace.CreateDirectory("current-data"));
		await using var reference = await ActualMcpProcess.StartAsync(project, workspace.CreateDirectory("reference-data"),
			executable: Environment.GetEnvironmentVariable("DEVPROJEX_BASELINE_EXECUTABLE"));
		foreach (var tool in new[] { "get_file", "related_files", "get_tree" })
		{
			var arguments = tool == "get_tree"
				? new Dictionary<string, object?> { ["format"] = "text", ["max_depth"] = 2 }
				: new Dictionary<string, object?> { ["path"] = "src/main.ts" };
			var actual = await current.Client.CallToolAsync(tool, arguments, progress: null, options: null, TestContext.Current.CancellationToken);
			var expected = await reference.Client.CallToolAsync(tool, arguments, progress: null, options: null, TestContext.Current.CancellationToken);
			Assert.NotEqual(true, actual.IsError);
			Assert.NotEqual(true, expected.IsError);
			Assert.Equal(Normalize(AllProcessText(expected)), Normalize(AllProcessText(actual)));
		}

		static string Normalize(string text) => System.Text.RegularExpressions.Regex.Replace(text,
			"untrusted-data-[a-f0-9]{24}", "untrusted-data-nonce");
	}

	[Fact]
	public async Task RealProcessNamesPackageManifestsOnlyForWholeEntryPointReads()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("package-project");
		workspace.WriteFile("package-project/src/bun.ts", "export const bun = 1;\n");
		workspace.WriteFile("package-project/src/ordinary.ts", "export const ordinary = 2;\n");
		workspace.WriteFile("package-project/jsr.json", """{"exports":{"./bun":"./src/bun.ts"}}""");
		workspace.WriteFile("package-project/package.json", """{"exports":{"./bun":{"import":"./src/bun.ts"}}}""");
		await using var server = await ActualMcpProcess.StartAsync(project, workspace.CreateDirectory("data"));
		async Task<string> Call(string tool, Dictionary<string, object?> arguments)
		{
			var result = await server.Client.CallToolAsync(tool, arguments, progress: null, options: null, TestContext.Current.CancellationToken);
			Assert.NotEqual(true, result.IsError);
			return AllProcessText(result);
		}
		var whole = await Call("get_file", new() { ["path"] = "src/bun.ts" });
		Assert.Contains("Exported by: jsr.json \"./bun\", package.json \"./bun\"", whole, StringComparison.Ordinal);
		var ranged = await Call("get_file", new() { ["path"] = "src/bun.ts", ["start_line"] = 1, ["end_line"] = 1 });
		Assert.DoesNotContain("Exported by:", ranged, StringComparison.Ordinal);
		var ordinary = await Call("get_file", new() { ["path"] = "src/ordinary.ts" });
		Assert.DoesNotContain("Exported by:", ordinary, StringComparison.Ordinal);
		var related = await Call("related_files", new() { ["path"] = "src/bun.ts", ["direction"] = "dependents" });
		Assert.Contains("jsr.json", related, StringComparison.Ordinal);
		Assert.Contains("package.json", related, StringComparison.Ordinal);
		Assert.Contains("package export", related, StringComparison.Ordinal);
		var hidden = await Call("related_files", new() { ["path"] = "src/bun.ts", ["direction"] = "dependents", ["exclude_patterns"] = new[] { "*.json" } });
		Assert.DoesNotContain("package export", hidden, StringComparison.Ordinal);
		var batch = await Call("get_file", new() { ["paths"] = new[] { "src/bun.ts", "src/ordinary.ts" } });
		Assert.Contains("Exported by:", batch, StringComparison.Ordinal);
	}
}
