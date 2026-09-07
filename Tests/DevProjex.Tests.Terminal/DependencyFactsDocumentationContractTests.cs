namespace DevProjex.Tests.Terminal;

public sealed class DependencyFactsDocumentationContractTests
{
	[Fact]
	public void DependencyFactsPublicSurfacesAndSafetyBoundariesStayDocumented()
	{
		var root = PublishedApplicationLocator.FindRepositoryRoot();
		var dependencies = File.ReadAllText(Path.Combine(root, "Docs", "Dependencies.md"));
		var mcp = File.ReadAllText(Path.Combine(root, "Docs", "McpServer.md"));
		var commandLine = File.ReadAllText(Path.Combine(root, "Docs", "CommandLine.md"));
		var version = File.ReadAllText(Path.Combine(root, "Docs", "CLI-V1-Contract.md"));
		var output = File.ReadAllText(Path.Combine(root, "Docs", "CLI-Output-Contract.md"));

		Assert.Contains("**ExplicitImport**", dependencies, StringComparison.Ordinal);
		Assert.Contains("**TypeReference**", dependencies, StringComparison.Ordinal);
		Assert.Contains("**Ambiguous**", dependencies, StringComparison.Ordinal);
		Assert.Contains("Merely failing to find a name in the manifest never", dependencies, StringComparison.Ordinal);
		Assert.Contains("2 Mi characters", dependencies, StringComparison.Ordinal);
		Assert.Contains("related_files.path", mcp, StringComparison.Ordinal);
		Assert.Contains("[Facts coverage]", mcp, StringComparison.Ordinal);
		Assert.Contains("devprojex related <PATH>", commandLine, StringComparison.Ordinal);
		Assert.Contains("v5.2 dependency-facts extension", version, StringComparison.Ordinal);
		Assert.Contains("devprojex-related-files", output, StringComparison.Ordinal);
	}
}
