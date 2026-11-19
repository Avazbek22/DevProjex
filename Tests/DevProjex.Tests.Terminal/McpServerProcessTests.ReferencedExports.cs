namespace DevProjex.Tests.Terminal;

public sealed partial class McpServerProcessTests
{
	[Fact]
	public async Task RealProcessReferencedCompilationAddsExportsWithoutWideningProtectedConfiguration()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("exports-project");
		workspace.WriteFile("exports-project/src/adapter/bun/index.ts", "export const entry = 1;");
		workspace.WriteFile("exports-project/jsr.json", """{"exports":{"./bun":"./src/adapter/bun/index.ts"}}""");
		workspace.WriteFile("exports-project/package.json", """{"exports":{"./bun":{"types":"./dist/types/adapter/bun/index.d.ts","import":"./dist/adapter/bun/index.js"}}}""");
		workspace.WriteFile("exports-project/tsconfig.json", """{"files":[],"references":[{"path":"./tsconfig.build.json"}]}""");
		const string build = """{"extends":"./tsconfig.base.json","compilerOptions":{"rootDir":"./src","outDir":"./dist/types"},"include":["src/**/*.ts"],"exclude":["src/**/*.test.ts"]}""";
		workspace.WriteFile("exports-project/tsconfig.build.json", build);
		workspace.WriteFile("exports-project/tsconfig.base.json", """{/* explicit child outputs are authoritative */"compilerOptions":{"outDir":"${configDir}/dist"},}""");
		await using var server = await ActualMcpProcess.StartAsync(project, workspace.CreateDirectory("data"));
		var read = await CallAsync(server, "get_file", new() { ["path"] = "src/adapter/bun/index.ts" });
		Assert.NotEqual(true, read.IsError);
		Assert.Contains("Exported by: jsr.json \"./bun\", package.json \"./bun\"", BatchData(read), StringComparison.Ordinal);
		var related = await CallAsync(server, "related_files", new() { ["path"] = "src/adapter/bun/index.ts", ["direction"] = "dependents" });
		Assert.Contains("package.json", BatchData(related), StringComparison.Ordinal);
		Assert.Contains("package export", BatchData(related), StringComparison.Ordinal);
		workspace.WriteFile("exports-project/tsconfig.build.json", build[..^1] + ",\"token\":\"ghp_" + "a7D9mQ2xK4vN8sR6tY3uW5zB1cE0fG2hJ9pL" + "\"}");
		var protectedRead = await CallAsync(server, "get_file", new() { ["path"] = "src/adapter/bun/index.ts" });
		Assert.NotEqual(true, protectedRead.IsError);
		Assert.Contains("Exported by: jsr.json \"./bun\"", BatchData(protectedRead), StringComparison.Ordinal);
		Assert.DoesNotContain("package.json", BatchData(protectedRead), StringComparison.Ordinal);
	}
}
