using DevProjex.Mcp;
using DevProjex.Tests.Mcp;

namespace DevProjex.Tests.Integration;

public sealed partial class McpServerIntegrationTests
{
	[Fact]
	public async Task RelatedFilesChecksEvidenceOnlyInTheSourcesOfTheListedRows()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		void Write(string relativePath, string content)
		{
			var path = Path.Combine(project, relativePath);
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			File.WriteAllText(path, content);
		}
		Write("tsconfig.json", "{\"compilerOptions\":{\"moduleResolution\":\"bundler\"}}\n");
		Write("a/Dep.ts", "export default 1;\n");
		Write("a/Main.ts", "import dep from './Dep.js'; export const main = dep;\n");
		// An unrelated file whose evidence reads exactly like the seed's: same import, same line.
		Write("b/Dep.ts", "export default 2;\n");
		Write("b/Other.ts", "import dep from './Dep.js'; export const other = dep;\n");
		IReadOnlyList<string> inspected = [];
		using var measurement = McpRelatedEvidenceRetentionDiagnostics.BeginMeasurement(paths =>
		{
			inspected = paths
				.Select(static path => Path.GetFileName(Path.GetDirectoryName(path)) + "/" + Path.GetFileName(path))
				.ToArray();
			// A source that cannot be read withholds every row quoting the same text, so a foreign
			// source must never be consulted for the seed's rows.
			var foreign = Path.Combine(project, "b", "Other.ts");
			if (File.Exists(foreign))
				File.Delete(foreign);
		});
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);

		var result = await server.CallAsync("get_dependencies_and_usages", new Dictionary<string, object?>
		{
			["path"] = "a/Main.ts",
			["direction"] = "dependencies"
		});
		var text = AllText(result);

		Assert.NotEqual(true, result.IsError);
		Assert.Equal(["a/Main.ts"], inspected);
		Assert.Contains("a/Dep.ts — import ./Dep.js at line 1 · ", text, StringComparison.Ordinal);
		Assert.DoesNotContain("[Related evidence]", text, StringComparison.Ordinal);
	}

	[Fact]
	public async Task OutsideSelectionEvidenceStaysWithinTheTwentyListedDependentsEvenWhenTheirReasonTextMatches()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		void Write(string relativePath, string content)
		{
			var path = Path.Combine(project, relativePath);
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			File.WriteAllText(path, content);
		}
		Write("tsconfig.json", "{\"compilerOptions\":{\"moduleResolution\":\"bundler\"}}\n");
		Write("core/Target.ts", "export default 1;\n");
		// 25 dependents outside the selection, every one importing the seed the same way on the
		// same line: their evidence reason text is byte-for-byte identical, the way two files can
		// independently produce the same "import ... at line 1" sentence.
		for (var index = 0; index < 25; index++)
		{
			Write(
				$"deps/Dep{index:D2}.ts",
				"import target from '../core/Target.js'; export const value = target;\n");
		}
		IReadOnlyList<string> inspected = [];
		using var measurement = McpRelatedEvidenceRetentionDiagnostics.BeginMeasurement(paths =>
		{
			inspected = paths
				.Select(static path => Path.GetFileName(Path.GetDirectoryName(path)) + "/" + Path.GetFileName(path))
				.ToArray();
			// The selection's own section has nothing to inspect here; removing an outside
			// dependent past the display cap must change nothing, because the fix never reads it.
			var beyondTheCap = Path.Combine(project, "deps", "Dep24.ts");
			if (File.Exists(beyondTheCap))
				File.Delete(beyondTheCap);
		});
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);

		var result = await server.CallAsync("get_dependencies_and_usages", new Dictionary<string, object?>
		{
			["path"] = "core/Target.ts",
			["direction"] = "dependents",
			["include_patterns"] = new[] { "core/**" }
		});
		var text = AllText(result);

		Assert.NotEqual(true, result.IsError);
		// Exactly the first 20 dependents in path order were opened for evidence; the other 5,
		// including the one deleted mid-call, were never a candidate.
		Assert.Equal(
			Enumerable.Range(0, 20).Select(static index => $"deps/Dep{index:D2}.ts").Order(StringComparer.Ordinal),
			inspected.Order(StringComparer.Ordinal));
		Assert.Contains("Dependents outside the selection (20 of 25):", text, StringComparison.Ordinal);
		Assert.DoesNotContain("[Related evidence] partial", text, StringComparison.Ordinal);
	}
}
