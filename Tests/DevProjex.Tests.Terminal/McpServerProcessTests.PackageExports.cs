using ModelContextProtocol.Protocol;
using DevProjex.Application.Secrets;
using DevProjex.Infrastructure.ProjectProfiles;

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
		workspace.WriteFile("plain-project/package.json", """{"name":"plain-fixture","version":"1.0.0"}""");
		workspace.WriteFile("plain-project/tsconfig.json", """{"compilerOptions":{"module":"esnext","moduleResolution":"bundler"}}""");
		await using var current = await ActualMcpProcess.StartAsync(project, workspace.CreateDirectory("current-data"));
		foreach (var tool in new[] { "get_file", "related_files", "get_tree" })
		{
			var arguments = tool == "get_tree"
				? new Dictionary<string, object?> { ["format"] = "text", ["max_depth"] = 2 }
				: new Dictionary<string, object?> { ["path"] = "src/main.ts" };
			var actual = await current.Client.CallToolAsync(tool, arguments, progress: null, options: null, TestContext.Current.CancellationToken);
			Assert.NotEqual(true, actual.IsError);
			var text = AllProcessText(actual);
			if (tool != "related_files")
				Assert.Contains("main.ts", text, StringComparison.Ordinal);
			Assert.True(text.Contains("value.ts", StringComparison.Ordinal), tool + "\n" + text);
			Assert.DoesNotContain("Exported by:", text, StringComparison.Ordinal);
			Assert.DoesNotContain("package export", text, StringComparison.Ordinal);
			if (tool == "get_file")
			{
				Assert.Contains("import { value } from './value';\nexport const main = value;", text, StringComparison.Ordinal);
				Assert.Contains("Imports: ./value → src/value.ts", text, StringComparison.Ordinal);
			}
		}
	}

	[Fact]
	public async Task RealProcessOmitsPackageExportPathsProtectedByAPersistentManualMark()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("protected-project");
		var dataRoot = workspace.CreateDirectory("protected-data");
		const string marked = "private-package-location-9361";
		var source = marked + "/entry.ts";
		workspace.WriteFile("protected-project/" + source, "export const entry = 1;\n");
		workspace.WriteFile("protected-project/" + marked + "/package.json", """{"exports":"./entry.ts"}""");
		var store = new ProjectProfileStore(() => dataRoot);
		store.SaveProfile(project, new ProjectSelectionProfile([], [], [], SelectedPaths: null));
		Assert.True(MarkedSecretValueNormalizer.TryCreate(marked, out var normalized, out _));
		Assert.True((await store.AddMarkAsync(project, new MarkedSecretProfileEntry(normalized.Hash, null, normalized.Length),
			TestContext.Current.CancellationToken)).Succeeded);
		await using var server = await ActualMcpProcess.StartAsync(project, dataRoot, arguments: ["--live"]);
		foreach (var tool in new[] { "get_file", "related_files" })
		{
			var response = await server.Client.CallToolAsync(tool, new Dictionary<string, object?> { ["path"] = source },
				progress: null, options: null, TestContext.Current.CancellationToken);
			Assert.NotEqual(true, response.IsError);
			Assert.DoesNotContain("Exported by:", AllProcessText(response), StringComparison.Ordinal);
			Assert.DoesNotContain("package export", AllProcessText(response), StringComparison.Ordinal);
		}
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
		Assert.True(whole.IndexOf("<untrusted-data-", StringComparison.Ordinal) < whole.IndexOf("Exported by:", StringComparison.Ordinal));
		Assert.True(whole.IndexOf("Exported by:", StringComparison.Ordinal) < whole.IndexOf("</untrusted-data-", StringComparison.Ordinal));
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
		workspace.WriteFile("package-project/jsr.json", """{"exports":{"./ordinary-now":"./src/ordinary.ts"}}""");
		workspace.WriteFile("package-project/package.json", """{"name":"no-longer-exported"}""");
		var previous = await Call("get_file", new() { ["path"] = "src/bun.ts" });
		Assert.DoesNotContain("Exported by:", previous, StringComparison.Ordinal);
		var changed = await Call("get_file", new() { ["path"] = "src/ordinary.ts" });
		Assert.Contains("Exported by: jsr.json \"./ordinary-now\"", changed, StringComparison.Ordinal);
		var previousRelations = await Call("related_files", new() { ["path"] = "src/bun.ts", ["direction"] = "dependents" });
		Assert.DoesNotContain("package export", previousRelations, StringComparison.Ordinal);
		var changedRelations = await Call("related_files", new() { ["path"] = "src/ordinary.ts", ["direction"] = "dependents" });
		Assert.Contains("jsr.json", changedRelations, StringComparison.Ordinal);
		Assert.Contains("package export", changedRelations, StringComparison.Ordinal);
	}
}
