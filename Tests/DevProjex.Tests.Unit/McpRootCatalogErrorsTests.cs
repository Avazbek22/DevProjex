using DevProjex.Mcp;

namespace DevProjex.Tests.Unit;

public sealed class McpRootCatalogErrorsTests
{
	[Theory]
	[InlineData(1)]
	[InlineData(2)]
	public void UnknownProjectKeepsTheStartupSpecificMessage(int count)
	{
		using var workspace = new TemporaryDirectory();
		var first = workspace.CreateFolder("first");
		var second = workspace.CreateFolder("second");
		var registry = new McpRootRegistry(count == 1 ? [first] : [first, second]);
		var expected = count == 1
			? "DPX-MCP-UNKNOWN-PROJECT: 'project' does not name the configured root. One local root is configured: omit 'project'."
			: "DPX-MCP-UNKNOWN-PROJECT: project is not an allowed root name, path, or index. Call list_projects and use a listed project index: #1, #2.";
		foreach (var name in new[] { "not-configured", "#99", workspace.CreateFolder("outside") })
			Assert.Equal(expected, Assert.Throws<McpToolException>(() => registry.ResolveProject(name)).Message);
		if (count == 1)
			Assert.Equal(registry.Roots[0], registry.ResolveProject("#0"));
		else
			Assert.Equal("DPX-MCP-UNKNOWN-PROJECT: 'project' is required because multiple roots are available. Call list_projects and use a listed project index: #1, #2.",
				Assert.Throws<McpToolException>(() => registry.ResolveProject(null)).Message);
	}

	[Theory]
	[InlineData(1)]
	[InlineData(2)]
	public void HostPathIsRefusedBeforeResolutionWithTheStartupSpecificMessage(int count)
	{
		using var workspace = new TemporaryDirectory();
		var first = workspace.CreateFolder("first");
		var second = workspace.CreateFolder("second");
		var resolutions = 0;
		var registry = new McpRootRegistry(count == 1 ? [first] : [first, second], (path, _) =>
		{
			resolutions++;
			return Path.GetFullPath(path);
		});
		var before = resolutions;
		var failure = Assert.Throws<McpToolException>(() => registry.ResolveProject("//catalog.invalid/share"));
		Assert.Equal(before, resolutions);
		Assert.Equal("DPX-MCP-INVALID-ARGUMENTS: 'project' is written as a path that names a host. " +
			"Such a form is refused before 'project' is resolved against the allowed roots, so that naming one cannot make the server reach it. " +
			(count == 1 ? "One local root is configured: omit 'project'." : "Call list_projects and use a listed project index: #1, #2."), failure.Message);
	}

	[Fact]
	public void AmbiguousNameKeepsItsExistingMessage()
	{
		using var workspace = new TemporaryDirectory();
		var registry = new McpRootRegistry([workspace.CreateFolder("a/same"), workspace.CreateFolder("b/same")]);
		Assert.Equal("DPX-MCP-UNKNOWN-PROJECT: the project name is ambiguous. Call list_projects and use one of these project indexes: #1, #2.",
			Assert.Throws<McpToolException>(() => registry.ResolveProject("same")).Message);
	}
}
