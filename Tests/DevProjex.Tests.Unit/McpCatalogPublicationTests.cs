using DevProjex.Mcp;

namespace DevProjex.Tests.Unit;

public sealed class McpCatalogPublicationTests
{
	[Theory]
	[InlineData(1, false, McpToolSet.Full, false, false)]
	[InlineData(2, false, McpToolSet.Full, false, false)]
	[InlineData(1, true, McpToolSet.Full, false, false)]
	[InlineData(2, true, McpToolSet.Full, true, true)]
	[InlineData(1, false, McpToolSet.Reduced, true, true)]
	[InlineData(2, false, McpToolSet.Reduced, true, false)]
	[InlineData(1, true, McpToolSet.Reduced, false, true)]
	[InlineData(3, false, McpToolSet.Full, false, true)]
	public void CatalogPublishesOnlyApplicableAddressParameters(
		int rootCount, bool allowRemote, McpToolSet toolSet, bool live, bool exclusions)
	{
		using var workspace = new TemporaryDirectory();
		using var packs = new McpPackRegistry(workspace.Path);
		var service = new Lazy<McpProjectService>(() =>
			throw new InvalidOperationException("Catalog construction must not load project services."));
		var target = new DevProjexMcpTools(new McpRootRegistry([workspace.Path]), service, packs);
		var catalog = new DevProjexMcpToolCatalog(target, allowRemote, exclusions, toolSet,
			live: live, rootCount: rootCount);
		Assert.Equal(toolSet == McpToolSet.Full ? 9 : 7, catalog.Count);
		foreach (var entry in catalog)
		{
			var tool = entry.ProtocolTool;
			var addressed = tool.Name is not ("list_projects" or "read_pack");
			var properties = tool.InputSchema.GetProperty("properties");
			var project = addressed && (rootCount > 1 || allowRemote);
			Assert.Equal(project, properties.TryGetProperty("project", out var projectSchema));
			Assert.Equal(addressed && allowRemote, properties.TryGetProperty("branch", out var branchSchema));
			Assert.Equal(project, tool.Description!.Contains("Project:", StringComparison.Ordinal));
			if (project)
			{
				Assert.Equal("Omit for one local root; otherwise unique name, absolute path, or #index.",
					projectSchema.GetProperty("description").GetString());
				Assert.EndsWith(" Project: omit for one local root; otherwise unique name, absolute path, or #index.",
					tool.Description, StringComparison.Ordinal);
			}
			if (addressed && allowRemote)
				Assert.Equal("Remote Git branch; invalid for local projects.", branchSchema.GetProperty("description").GetString());
			Assert.Equal(addressed && exclusions, properties.TryGetProperty("exclusions", out _));
			Assert.InRange(tool.Description.Length, 1, 800);
			Assert.InRange(tool.Description.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length, 40, 100);
		}
		Assert.False(service.IsValueCreated);
	}
}
