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

		var result = await server.CallAsync("related_files", new Dictionary<string, object?>
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
}
