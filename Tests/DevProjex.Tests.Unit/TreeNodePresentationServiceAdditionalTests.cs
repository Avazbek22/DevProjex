namespace DevProjex.Tests.Unit;

public sealed class TreeNodePresentationServiceAdditionalTests
{
	private static readonly IReadOnlyDictionary<AppLanguage, IReadOnlyDictionary<string, string>> CatalogData =
		new Dictionary<AppLanguage, IReadOnlyDictionary<string, string>>
		{
			[AppLanguage.En] = new Dictionary<string, string>
			{
				["Tree.AccessDenied"] = "Access denied",
				["Tree.AccessDeniedRoot"] = "Access denied root"
			}
		};

	[Fact]
	// Verifies access denied root uses the root-specific localization key.
	public void Build_AccessDeniedRoot_UsesRootLabel()
	{
		var localization = new LocalizationService(new StubLocalizationCatalog(CatalogData), AppLanguage.En);
		var iconMapper = new StubIconMapper { IconKey = "rootIcon" };
		var service = new TreeNodePresentationService(localization, iconMapper);
		var root = new FileSystemNode("root", "/root", true, true, new List<FileSystemNode>());

		var descriptor = service.Build(root);

		Assert.Equal("Access denied root", descriptor.DisplayName);
	}

	[Fact]
	// Verifies access denied non-root uses the non-root localization key.
	public void Build_AccessDeniedChild_UsesChildLabel()
	{
		var localization = new LocalizationService(new StubLocalizationCatalog(CatalogData), AppLanguage.En);
		var iconMapper = new StubIconMapper { IconKey = "childIcon" };
		var service = new TreeNodePresentationService(localization, iconMapper);
		var child = new FileSystemNode("child", "/root/child", true, true, new List<FileSystemNode>());
		var root = new FileSystemNode("root", "/root", true, false, new List<FileSystemNode> { child });

		var descriptor = service.Build(root);

		Assert.Equal("Access denied", descriptor.Children[0].DisplayName);
	}

	[Fact]
	// Verifies non-access-denied nodes keep their original names.
	public void Build_NonDeniedNode_UsesOriginalName()
	{
		var localization = new LocalizationService(new StubLocalizationCatalog(CatalogData), AppLanguage.En);
		var iconMapper = new StubIconMapper { IconKey = "icon" };
		var service = new TreeNodePresentationService(localization, iconMapper);
		var root = new FileSystemNode("root", "/root", true, false, new List<FileSystemNode>());

		var descriptor = service.Build(root);

		Assert.Equal("root", descriptor.DisplayName);
	}

	[Fact]
	// Verifies icon mapper is applied to the root node.
	public void Build_UsesIconMapperForRoot()
	{
		var localization = new LocalizationService(new StubLocalizationCatalog(CatalogData), AppLanguage.En);
		var iconMapper = new StubIconMapper { IconKey = "mappedIcon" };
		var service = new TreeNodePresentationService(localization, iconMapper);
		var root = new FileSystemNode("root", "/root", true, false, new List<FileSystemNode>());

		var descriptor = service.Build(root);

		Assert.Equal("mappedIcon", descriptor.IconKey);
	}

	[Fact]
	// Verifies icon mapper is applied to child nodes.
	public void Build_UsesIconMapperForChildren()
	{
		var localization = new LocalizationService(new StubLocalizationCatalog(CatalogData), AppLanguage.En);
		var iconMapper = new StubIconMapper { IconKey = "childIcon" };
		var service = new TreeNodePresentationService(localization, iconMapper);
		var child = new FileSystemNode("child", "/root/child", false, false, new List<FileSystemNode>());
		var root = new FileSystemNode("root", "/root", true, false, new List<FileSystemNode> { child });

		var descriptor = service.Build(root);

		Assert.Equal("childIcon", descriptor.Children[0].IconKey);
	}

	[Fact]
	// Verifies child nodes are converted and preserved in order.
	public void Build_MapsChildrenRecursively()
	{
		var localization = new LocalizationService(new StubLocalizationCatalog(CatalogData), AppLanguage.En);
		var iconMapper = new StubIconMapper { IconKey = "icon" };
		var service = new TreeNodePresentationService(localization, iconMapper);
		var child1 = new FileSystemNode("alpha", "/root/alpha", false, false, new List<FileSystemNode>());
		var child2 = new FileSystemNode("beta", "/root/beta", false, false, new List<FileSystemNode>());
		var root = new FileSystemNode("root", "/root", true, false, new List<FileSystemNode> { child1, child2 });

		var descriptor = service.Build(root);

		Assert.Equal(2, descriptor.Children.Count);
		Assert.Equal("alpha", descriptor.Children[0].DisplayName);
		Assert.Equal("beta", descriptor.Children[1].DisplayName);
	}
}
