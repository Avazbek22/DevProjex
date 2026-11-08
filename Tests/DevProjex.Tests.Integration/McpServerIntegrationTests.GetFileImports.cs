namespace DevProjex.Tests.Integration;

public sealed partial class McpServerIntegrationTests
{
	private const string ModulePathResolvedText =
		"[Path resolved] the requested path does not exist; the module file it names was read, and its path is in the File line.";

	[Fact]
	public async Task GetFileMapsEachImportOfTheReturnedLinesToItsProjectFile()
	{
		using var workspace = new TemporaryDirectory();
		var project = WriteScriptImportFixture(workspace);
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);

		var text = Text(await server.CallAsync("get_file", new Dictionary<string, object?> { ["path"] = "src/adapter/bun/server.ts" }));

		AssertTestsLineInsideBlock(text,
			"Imports: ../../helper/conninfo → src/helper/conninfo/index.ts · ../../types → src/types.ts · ./gone → unresolved · external: node:fs/promises");
		Assert.DoesNotContain("Imports:", OutsideUntrustedText(text), StringComparison.Ordinal);
	}

	[Fact]
	public async Task GetFileNamesOnlyTheImportsOnTheReturnedLines()
	{
		using var workspace = new TemporaryDirectory();
		var project = WriteScriptImportFixture(workspace);
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);

		var first = Text(await server.CallAsync("get_file", new Dictionary<string, object?>
		{
			["path"] = "src/adapter/bun/server.ts",
			["start_line"] = 1,
			["end_line"] = 1
		}));
		var body = Text(await server.CallAsync("get_file", new Dictionary<string, object?>
		{
			["path"] = "src/adapter/bun/server.ts",
			["start_line"] = 5,
			["end_line"] = 6
		}));

		AssertTestsLineInsideBlock(first, "Imports: ../../helper/conninfo → src/helper/conninfo/index.ts\n");
		Assert.DoesNotContain("Imports:", body, StringComparison.Ordinal);
	}

	[Fact]
	public async Task GetFileBatchMapsTheImportsOfEachSection()
	{
		using var workspace = new TemporaryDirectory();
		var project = WriteScriptImportFixture(workspace);
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);

		var text = Text(await server.CallAsync("get_file", new Dictionary<string, object?>
		{
			["paths"] = new[] { "src/adapter/bun/server.ts", "src/index.ts" }
		}));

		Assert.Contains("Imports: ../../helper/conninfo → src/helper/conninfo/index.ts", text, StringComparison.Ordinal);
		Assert.Contains("Imports: ./adapter/bun/server → src/adapter/bun/server.ts", text, StringComparison.Ordinal);
	}

	[Fact]
	public async Task GetFileMapsPythonImportsAndSummarizesStandardModules()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		WriteProjectFile(project, "pkg/__init__.py", "from ._client import Client\n");
		WriteProjectFile(project, "pkg/_config.py", "DEFAULT_TIMEOUT = 5\n");
		WriteProjectFile(project, "pkg/_client.py", "import json\nimport typing\nfrom ._config import DEFAULT_TIMEOUT\nfrom . import _config\n\nclass Client:\n    timeout = DEFAULT_TIMEOUT\n");
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);

		var text = Text(await server.CallAsync("get_file", new Dictionary<string, object?> { ["path"] = "pkg/_client.py" }));

		AssertTestsLineInsideBlock(text, "Imports: ._config → pkg/_config.py · . → pkg/_config.py · external: json, typing");
	}

	[Fact]
	public async Task GetFileLeavesOutAnImportTheProtectionWouldChange()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		WriteProjectFile(project, "package.json", "{ \"name\": \"fixture\", \"version\": \"1.0.0\" }\n");
		WriteProjectFile(project, "tsconfig.json", "{ \"compilerOptions\": { \"module\": \"esnext\", \"moduleResolution\": \"bundler\" } }\n");
		WriteProjectFile(project, $"src/{Secret}.ts", "export const value = 1\n");
		WriteProjectFile(project, "src/plain.ts", "export const plain = 1\n");
		WriteProjectFile(project, "src/app.ts", $"import {{ value }} from './{Secret}'\nimport {{ plain }} from './plain'\n");
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);

		var text = Text(await server.CallAsync("get_file", new Dictionary<string, object?> { ["path"] = "src/app.ts" }));

		AssertTestsLineInsideBlock(text, "Imports: ./plain → src/plain.ts\n");
		Assert.DoesNotContain(Secret, text, StringComparison.Ordinal);
	}

	[Fact]
	public async Task GetFileHasNoImportsLineForALanguageWithoutModuleImports()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		WriteProjectFile(project, "src/App.cs", "using System;\nnamespace App;\npublic sealed class Program { }\n");
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);

		var text = Text(await server.CallAsync("get_file", new Dictionary<string, object?> { ["path"] = "src/App.cs" }));

		Assert.DoesNotContain("Imports:", text, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("src/helper/conninfo.ts", "src/helper/conninfo/index.ts")]
	[InlineData("src/types.js", "src/types.ts")]
	[InlineData("pkg/_config", "pkg/_config.py")]
	[InlineData("pkg/sub.py", "pkg/sub/__init__.py")]
	public async Task GetFileReadsTheModuleFileAMissingPathNames(string requested, string expected)
	{
		using var workspace = new TemporaryDirectory();
		var project = WriteScriptImportFixture(workspace);
		WriteProjectFile(project, "pkg/_config.py", "DEFAULT_TIMEOUT = 5\n");
		WriteProjectFile(project, "pkg/sub/__init__.py", "VALUE = 1\n");
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);

		var result = await server.CallAsync("get_file", new Dictionary<string, object?> { ["path"] = requested });

		Assert.NotEqual(true, result.IsError);
		var text = Text(result);
		Assert.Contains($"File: {expected}\n", text, StringComparison.Ordinal);
		Assert.Contains(ModulePathResolvedText, OutsideUntrustedText(text), StringComparison.Ordinal);
		Assert.DoesNotContain(expected, OutsideUntrustedText(text), StringComparison.Ordinal);
	}

	[Fact]
	public async Task GetFileKeepsTheNotFoundErrorWhenTheModuleFileIsAmbiguousOrIgnored()
	{
		using var workspace = new TemporaryDirectory();
		using var repository = new GitModeRepository();
		repository.Write(".gitignore", "ignored/\n");
		repository.Write("src/both.ts", "export const a = 1\n");
		repository.Write("src/both.tsx", "export const a = 1\n");
		repository.Write("ignored/secret/index.ts", "export const hidden = 1\n");
		repository.Commit();
		await using var server = await McpTestServer.StartAsync(repository.Root, workspace.Path);

		var ambiguous = Text(await server.CallAsync("get_file", new Dictionary<string, object?> { ["path"] = "src/both" }));
		var ignoredDirect = Text(await server.CallAsync("get_file", new Dictionary<string, object?> { ["path"] = "ignored/secret/index.ts" }));
		var ignored = Text(await server.CallAsync("get_file", new Dictionary<string, object?> { ["path"] = "ignored/secret.ts" }));

		// Tool errors are returned as text that starts with the error code.
		Assert.StartsWith("DPX-MCP-PATH-NOT-FOUND: request failed.", ambiguous, StringComparison.Ordinal);
		Assert.DoesNotContain("both.ts", ambiguous, StringComparison.Ordinal);
		Assert.StartsWith("DPX-MCP-", ignoredDirect, StringComparison.Ordinal);
		Assert.StartsWith("DPX-MCP-PATH-NOT-FOUND: request failed.", ignored, StringComparison.Ordinal);
		Assert.DoesNotContain("index.ts", ignored, StringComparison.Ordinal);
		Assert.DoesNotContain("hidden", ignored, StringComparison.Ordinal);
	}

	private static string WriteScriptImportFixture(TemporaryDirectory workspace)
	{
		var project = workspace.CreateDirectory("project");
		WriteProjectFile(project, "package.json", "{ \"name\": \"fixture\", \"version\": \"1.0.0\" }\n");
		WriteProjectFile(project, "tsconfig.json", "{ \"compilerOptions\": { \"module\": \"esnext\", \"moduleResolution\": \"bundler\" } }\n");
		WriteProjectFile(project, "src/helper/conninfo/index.ts", "export const getConnInfo = () => 1\n");
		WriteProjectFile(project, "src/types.ts", "export type Env = {}\n");
		WriteProjectFile(project, "src/index.ts", "export { serve } from './adapter/bun/server'\n");
		WriteProjectFile(project, "src/adapter/bun/server.ts",
			"import { getConnInfo } from '../../helper/conninfo'\n" +
			"import type { Env } from '../../types'\n" +
			"import { readFile } from 'node:fs/promises'\n" +
			"import { gone } from './gone'\n" +
			"export const serve = () => getConnInfo()\n" +
			"export const env: Env = {}\n");
		return project;
	}
}
