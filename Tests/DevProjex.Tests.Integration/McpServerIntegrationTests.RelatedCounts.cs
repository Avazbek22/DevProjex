namespace DevProjex.Tests.Integration;

public sealed partial class McpServerIntegrationTests
{
	[Fact]
	public async Task RelatedFilesReportsNoRelatedFilesWhenTheOnlyResolvedReferenceIsToTheSeedItself()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		File.WriteAllText(Path.Combine(project, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
		File.WriteAllText(
			Path.Combine(project, "Node.cs"),
			"namespace App; public sealed class Node { Node Next; }\n");
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);

		var related = await server.CallAsync(
			"get_dependencies_and_usages",
			new Dictionary<string, object?> { ["path"] = "Node.cs", ["direction"] = "both" });

		Assert.NotEqual(true, related.IsError);
		var text = AllText(related);
		Assert.Contains("[Resolution] resolved=0 · ambiguous=0 · unresolved=0 · external=0", text, StringComparison.Ordinal);
		Assert.Contains("[No related files] in the effective selection.", text, StringComparison.Ordinal);
	}
}
