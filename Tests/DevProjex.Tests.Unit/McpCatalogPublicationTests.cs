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
		Assert.Equal(toolSet == McpToolSet.Full ? 9 : rootCount > 1 || allowRemote ? 7 : 6, catalog.Count);
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
			CheckInputSchema(tool.InputSchema, tool.Name, string.Empty);
		}
		Assert.False(service.IsValueCreated);
	}

	private static void CheckInputSchema(JsonElement schema, string tool, string path)
	{
		if (schema.ValueKind == JsonValueKind.Object)
		{
			foreach (var property in schema.EnumerateObject())
			{
				if (property.Name is "minLength" or "maxLength" or "minItems" or "maxItems")
				{
					var retained = property.Name == "minLength" && path == "/properties/profile" ||
						property.Name == "maxLength" && tool == "get_file" && path == "/properties/symbol";
					Assert.True(retained, $"Unexpected published bound: {tool}{path}/{property.Name}");
					Assert.Equal(property.Name == "minLength" ? 1 : 512, property.Value.GetInt32());
				}
				if (property.Name == "pattern" && property.Value.ValueKind == JsonValueKind.String)
				{
					Assert.Equal("/properties/git_scope/oneOf/1", path);
					Assert.Equal("^diff:", property.Value.GetString());
				}
				if (property.Name == "oneOf")
				{
					var types = property.Value.EnumerateArray().Select(static branch => branch.GetProperty("type").GetString()).ToArray();
					if (path != "/properties/git_scope")
						Assert.Equal(types.Length, types.Distinct(StringComparer.Ordinal).Count());
				}
				CheckInputSchema(property.Value, tool, path + "/" + property.Name);
			}
		}
		else if (schema.ValueKind == JsonValueKind.Array)
		{
			var index = 0;
			foreach (var item in schema.EnumerateArray())
				CheckInputSchema(item, tool, path + "/" + index++);
		}
	}
}
