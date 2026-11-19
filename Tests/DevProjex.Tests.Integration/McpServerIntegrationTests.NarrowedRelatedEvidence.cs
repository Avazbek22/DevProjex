using DevProjex.Mcp;
using DevProjex.Tests.Mcp;

namespace DevProjex.Tests.Integration;

public sealed partial class McpServerIntegrationTests
{
	[Fact]
	public async Task NarrowedRelatedFilesChecksTheEvidenceOfEveryRowAndReadsNothingElse()
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
		Write("core/Inside.ts", "import target from './Target.js'; export const inside = target;\n");
		Write("app/Outside.ts", "import target from '../core/Target.js'; export const outside = target;\n");
		// Same evidence text as core/Inside.ts, but related to a different target.
		Write("other/Target.ts", "export default 2;\n");
		Write("other/Copy.ts", "import target from './Target.js'; export const copy = target;\n");
		IReadOnlyList<string> inspected = [];
		using var measurement = McpRelatedEvidenceRetentionDiagnostics.BeginMeasurement(paths =>
		{
			inspected = paths
				.Select(static path => Path.GetFileName(Path.GetDirectoryName(path)) + "/" + Path.GetFileName(path))
				.ToArray();
			File.Delete(Path.Combine(project, "core", "Inside.ts"));
		});
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);

		var result = await server.CallAsync("related_files", new Dictionary<string, object?>
		{
			["path"] = "core/Target.ts",
			["direction"] = "dependents",
			["include_patterns"] = new[] { "core/**" }
		});
		var text = AllText(result);

		// Only the sources of the listed rows are opened, inside the selection and outside it.
		Assert.NotEqual(true, result.IsError);
		Assert.Equal(["app/Outside.ts", "core/Inside.ts"], inspected.Order(StringComparer.Ordinal));
		Assert.Contains("core/Inside.ts — import at line 1 · ", text, StringComparison.Ordinal);
		Assert.DoesNotContain("import ./Target.js at line 1", text, StringComparison.Ordinal);
		Assert.Contains("app/Outside.ts — import ../core/Target.js at line 1 · ", text, StringComparison.Ordinal);
		AssertTrustedTrailerOutsideSpotlight(result, "[Related evidence] partial · uninspected-sources=1");
		AssertTrustedTrailerOutsideSpotlight(result, "[Outside selection] relations=1 · listed=1;");
	}
}
