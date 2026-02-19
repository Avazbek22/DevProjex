namespace DevProjex.Tests.Unit;

public sealed class TreeNodePresentationServiceTests
{
	// Verifies access-denied nodes use localized display names and icons.
	[Fact]
	public void Build_UsesLocalizationForAccessDenied()
	{
		var catalog = new StubLocalizationCatalog(new Dictionary<AppLanguage, IReadOnlyDictionary<string, string>>
		{
			[AppLanguage.En] = new Dictionary<string, string>
			{
				["Tree.AccessDeniedRoot"] = "RootDenied",
				["Tree.AccessDenied"] = "ChildDenied"
			}
		});
		var localization = new LocalizationService(catalog, AppLanguage.En);
		var iconMapper = new StubIconMapper { IconKey = "icon" };
		var service = new TreeNodePresentationService(localization, iconMapper);

		var root = new FileSystemNode(
			name: "root",
			fullPath: "/root",
			isDirectory: true,
			isAccessDenied: true,
			children: new List<FileSystemNode>
			{
				new FileSystemNode("child", "/root/child", true, true, new List<FileSystemNode>())
			});

		var result = service.Build(root);

		Assert.Equal("RootDenied", result.DisplayName);
		Assert.Equal("ChildDenied", result.Children[0].DisplayName);
		Assert.Equal("icon", result.IconKey);
	}

	// Verifies nodes without access issues use their names.
	[Fact]
	public void Build_UsesNodeNameWhenAccessible()
	{
		var catalog = new StubLocalizationCatalog(new Dictionary<AppLanguage, IReadOnlyDictionary<string, string>>
		{
			[AppLanguage.En] = new Dictionary<string, string>()
		});
		var localization = new LocalizationService(catalog, AppLanguage.En);
		var iconMapper = new StubIconMapper { IconKey = "icon" };
		var service = new TreeNodePresentationService(localization, iconMapper);

		var root = new FileSystemNode(
			name: "root",
			fullPath: "/root",
			isDirectory: true,
			isAccessDenied: false,
			children: new List<FileSystemNode>());

		var result = service.Build(root);

		Assert.Equal("root", result.DisplayName);
	}

	// Verifies icon mapping is applied to child nodes.
	[Fact]
	public void Build_AssignsIconsForChildren()
	{
		var catalog = new StubLocalizationCatalog(new Dictionary<AppLanguage, IReadOnlyDictionary<string, string>>
		{
			[AppLanguage.En] = new Dictionary<string, string>()
		});
		var localization = new LocalizationService(catalog, AppLanguage.En);
		var iconMapper = new TestIconMapper();
		var service = new TreeNodePresentationService(localization, iconMapper);

		var root = new FileSystemNode(
			name: "root",
			fullPath: "/root",
			isDirectory: true,
			isAccessDenied: false,
			children: new List<FileSystemNode>
			{
				new FileSystemNode("child.txt", "/root/child.txt", false, false, new List<FileSystemNode>())
			});

		var result = service.Build(root);

		Assert.Equal("folder-icon", result.IconKey);
		Assert.Equal("file-icon", result.Children[0].IconKey);
	}

	// Verifies child metadata is preserved in the presentation model.
	[Fact]
	public void Build_PreservesChildMetadata()
	{
		var catalog = new StubLocalizationCatalog(new Dictionary<AppLanguage, IReadOnlyDictionary<string, string>>
		{
			[AppLanguage.En] = new Dictionary<string, string>()
		});
		var localization = new LocalizationService(catalog, AppLanguage.En);
		var iconMapper = new StubIconMapper { IconKey = "icon" };
		var service = new TreeNodePresentationService(localization, iconMapper);

		var root = new FileSystemNode(
			name: "root",
			fullPath: "/root",
			isDirectory: true,
			isAccessDenied: false,
			children: new List<FileSystemNode>
			{
				new FileSystemNode("child.txt", "/root/child.txt", false, false, new List<FileSystemNode>())
			});

		var result = service.Build(root);

		Assert.False(result.Children[0].IsDirectory);
		Assert.Equal("/root/child.txt", result.Children[0].FullPath);
	}

	// Verifies access-denied child uses child-specific localization.
	[Fact]
	public void Build_UsesChildAccessDeniedLabelWhenRootAccessible()
	{
		var catalog = new StubLocalizationCatalog(new Dictionary<AppLanguage, IReadOnlyDictionary<string, string>>
		{
			[AppLanguage.En] = new Dictionary<string, string>
			{
				["Tree.AccessDeniedRoot"] = "RootDenied",
				["Tree.AccessDenied"] = "ChildDenied"
			}
		});
		var localization = new LocalizationService(catalog, AppLanguage.En);
		var iconMapper = new StubIconMapper { IconKey = "icon" };
		var service = new TreeNodePresentationService(localization, iconMapper);

		var root = new FileSystemNode(
			name: "root",
			fullPath: "/root",
			isDirectory: true,
			isAccessDenied: false,
			children: new List<FileSystemNode>
			{
				new FileSystemNode("child", "/root/child", true, true, new List<FileSystemNode>())
			});

		var result = service.Build(root);

		Assert.Equal("root", result.DisplayName);
		Assert.Equal("ChildDenied", result.Children[0].DisplayName);
	}

	// Verifies nested children preserve directory flags.
	[Fact]
	public void Build_PreservesDirectoryFlags()
	{
		var catalog = new StubLocalizationCatalog(new Dictionary<AppLanguage, IReadOnlyDictionary<string, string>>
		{
			[AppLanguage.En] = new Dictionary<string, string>()
		});
		var localization = new LocalizationService(catalog, AppLanguage.En);
		var iconMapper = new StubIconMapper { IconKey = "icon" };
		var service = new TreeNodePresentationService(localization, iconMapper);

		var root = new FileSystemNode(
			name: "root",
			fullPath: "/root",
			isDirectory: true,
			isAccessDenied: false,
			children: new List<FileSystemNode>
			{
				new FileSystemNode("child", "/root/child", true, false, new List<FileSystemNode>())
			});

		var result = service.Build(root);

		Assert.True(result.IsDirectory);
		Assert.True(result.Children[0].IsDirectory);
	}

	private sealed class TestIconMapper : IIconMapper
	{
		public string GetIconKey(FileSystemNode node)
		{
			return node.IsDirectory ? "folder-icon" : "file-icon";
		}
	}
}
