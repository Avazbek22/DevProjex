namespace DevProjex.Tests.Unit;

public sealed class TreeNodePresentationServiceTests
{
	// Verifies access-denied nodes preserve their names and append one localized marker.
	[Fact]
	public void Build_UsesLocalizationForAccessDenied()
	{
		var catalog = new StubLocalizationCatalog(new Dictionary<AppLanguage, IReadOnlyDictionary<string, string>>
		{
			[AppLanguage.En] = new Dictionary<string, string>
			{
				["Tree.AccessDenied"] = "access denied"
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
				new FileSystemNode("child", "/root/child", true, true, new List<FileSystemNode>()),
				new FileSystemNode("name.ext", "/root/name.ext", false, true, new List<FileSystemNode>())
			});

		var result = service.Build(root);

		Assert.Equal("root [access denied]", result.DisplayName);
		Assert.Equal("child [access denied]", result.Children[0].DisplayName);
		Assert.Equal("name.ext [access denied]", result.Children[1].DisplayName);
		Assert.DoesNotContain("⛔", result.DisplayName, StringComparison.Ordinal);
		Assert.All(result.Children, static child =>
			Assert.DoesNotContain("⛔", child.DisplayName, StringComparison.Ordinal));
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

	[Fact]
	public void BuildWithFilePaths_ReturnsVisibleFilesInTreeOrder()
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
				new("a.txt", "/root/a.txt", false, false, FileSystemNode.EmptyChildren),
				new(
					"src",
					"/root/src",
					true,
					false,
					new List<FileSystemNode>
					{
						new("b.cs", "/root/src/b.cs", false, false, FileSystemNode.EmptyChildren),
						new("c.cs", "/root/src/c.cs", false, false, FileSystemNode.EmptyChildren)
					})
			});

		var result = service.BuildWithFilePaths(root);

		Assert.Equal("root", result.Root.DisplayName);
		Assert.Equal(["/root/a.txt", "/root/src/b.cs", "/root/src/c.cs"], result.OrderedFilePaths);
	}

	[Fact]
	public void BuildWithFilePaths_CancelsDuringProjection()
	{
		using var cancellation = new CancellationTokenSource();
		var localization = new LocalizationService(
			new StubLocalizationCatalog(new Dictionary<AppLanguage, IReadOnlyDictionary<string, string>>
			{
				[AppLanguage.En] = new Dictionary<string, string>()
			}),
			AppLanguage.En);
		var service = new TreeNodePresentationService(
			localization,
			new CancellingIconMapper(cancellation, cancelOnCall: 3));
		var children = Enumerable.Range(0, 8)
			.Select(index => new FileSystemNode(
				$"file-{index}.txt",
				$"/root/file-{index}.txt",
				false,
				false,
				FileSystemNode.EmptyChildren))
			.ToArray();
		var root = new FileSystemNode("root", "/root", true, false, children);

		Assert.Throws<OperationCanceledException>(() =>
			service.BuildWithFilePathsWithCancellation(root, cancellation.Token));
	}

	// Verifies access-denied child uses child-specific localization.
	[Fact]
	public void Build_UsesChildAccessDeniedLabelWhenRootAccessible()
	{
		var catalog = new StubLocalizationCatalog(new Dictionary<AppLanguage, IReadOnlyDictionary<string, string>>
		{
			[AppLanguage.En] = new Dictionary<string, string>
			{
				["Tree.AccessDenied"] = "access denied"
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
		Assert.Equal("child [access denied]", result.Children[0].DisplayName);
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

	private sealed class CancellingIconMapper(
		CancellationTokenSource cancellation,
		int cancelOnCall) : IIconMapper
	{
		private int _callCount;

		public string GetIconKey(FileSystemNode node)
		{
			if (Interlocked.Increment(ref _callCount) == cancelOnCall)
				cancellation.Cancel();
			return node.IsDirectory ? "folder-icon" : "file-icon";
		}
	}
}
