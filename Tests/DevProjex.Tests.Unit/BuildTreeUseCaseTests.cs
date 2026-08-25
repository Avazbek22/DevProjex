namespace DevProjex.Tests.Unit;

public sealed class BuildTreeUseCaseTests
{
	// Verifies the use case returns a presented tree with icon mapping applied.
	[Fact]
	public void Execute_ReturnsPresentedTree()
	{
		var treeBuilder = new StubTreeBuilder
		{
			Result = new TreeBuildResult(
				new FileSystemNode("root", "/root", true, false, new List<FileSystemNode>()),
				RootAccessDenied: false,
				HadAccessDenied: false)
		};

		var catalog = new StubLocalizationCatalog(new Dictionary<AppLanguage, IReadOnlyDictionary<string, string>>
		{
			[AppLanguage.En] = new Dictionary<string, string>()
		});
		var localization = new LocalizationService(catalog, AppLanguage.En);
		var presenter = new TreeNodePresentationService(localization, new StubIconMapper { IconKey = "folder" });

		var useCase = new BuildTreeUseCase(treeBuilder, presenter);

		var result = useCase.Execute(new BuildTreeRequest("/root", new TreeFilterOptions(
			AllowedExtensions: new HashSet<string>(),
			AllowedRootFolders: new HashSet<string>(),
			IgnoreRules: new IgnoreRules(IgnoreHiddenFolders: false,
			IgnoreHiddenFiles: false,
			IgnoreDotFolders: false,
				IgnoreDotFiles: false,
				SmartIgnoredFolders: new HashSet<string>(),
				SmartIgnoredFiles: new HashSet<string>()))), cancellationToken: TestContext.Current.CancellationToken);

		Assert.Equal("root", result.Root.DisplayName);
		Assert.Equal("folder", result.Root.IconKey);
	}

	// Verifies access denied flags are forwarded from the tree build result.
	[Fact]
	public void Execute_ForwardsAccessDeniedFlags()
	{
		var treeBuilder = new StubTreeBuilder
		{
			Result = new TreeBuildResult(
				new FileSystemNode("root", "/root", true, false, new List<FileSystemNode>()),
				RootAccessDenied: true,
				HadAccessDenied: true)
		};

		var catalog = new StubLocalizationCatalog(new Dictionary<AppLanguage, IReadOnlyDictionary<string, string>>
		{
			[AppLanguage.En] = new Dictionary<string, string>()
		});
		var localization = new LocalizationService(catalog, AppLanguage.En);
		var presenter = new TreeNodePresentationService(localization, new StubIconMapper { IconKey = "folder" });

		var useCase = new BuildTreeUseCase(treeBuilder, presenter);

		var result = useCase.Execute(new BuildTreeRequest("/root", new TreeFilterOptions(
			AllowedExtensions: new HashSet<string>(),
			AllowedRootFolders: new HashSet<string>(),
			IgnoreRules: new IgnoreRules(IgnoreHiddenFolders: false,
			IgnoreHiddenFiles: false,
			IgnoreDotFolders: false,
				IgnoreDotFiles: false,
				SmartIgnoredFolders: new HashSet<string>(),
				SmartIgnoredFiles: new HashSet<string>()))), cancellationToken: TestContext.Current.CancellationToken);

		Assert.True(result.RootAccessDenied);
		Assert.True(result.HadAccessDenied);
	}

	[Fact]
	public void ExecuteWithInventory_ReturnsInventory_WhenBuilderSupportsInventoryContract()
	{
		var treeBuilder = new InventoryTreeBuilderStub();
		var catalog = new StubLocalizationCatalog(new Dictionary<AppLanguage, IReadOnlyDictionary<string, string>>
		{
			[AppLanguage.En] = new Dictionary<string, string>()
		});
		var localization = new LocalizationService(catalog, AppLanguage.En);
		var presenter = new TreeNodePresentationService(localization, new StubIconMapper { IconKey = "folder" });
		var useCase = new BuildTreeUseCase(treeBuilder, presenter);

		var result = useCase.ExecuteWithInventory(
			new BuildTreeRequest("/root", CreateOptions()),
			TestContext.Current.CancellationToken);

		Assert.NotNull(result.Inventory);
		Assert.Equal(1, treeBuilder.ReadInventoryCount);
		Assert.Equal(1, treeBuilder.BuildFromInventoryCount);
		Assert.Equal("root", result.Tree.Root.DisplayName);
	}

	[Fact]
	public void ExecuteWithProvidedInventory_DoesNotReadFilesystemInventoryAgain()
	{
		var treeBuilder = new InventoryTreeBuilderStub();
		var providedInventory = CreateSingleRootInventory("/provided");
		treeBuilder.ExpectedBuildInventory = providedInventory;
		var catalog = new StubLocalizationCatalog(new Dictionary<AppLanguage, IReadOnlyDictionary<string, string>>
		{
			[AppLanguage.En] = new Dictionary<string, string>()
		});
		var localization = new LocalizationService(catalog, AppLanguage.En);
		var presenter = new TreeNodePresentationService(localization, new StubIconMapper { IconKey = "folder" });
		var useCase = new BuildTreeUseCase(treeBuilder, presenter);

		var result = useCase.ExecuteWithInventory(
			new BuildTreeRequest("/root", CreateOptions()),
			providedInventory,
			TestContext.Current.CancellationToken);

		Assert.Same(providedInventory, result.Inventory);
		Assert.Equal(0, treeBuilder.ReadInventoryCount);
		Assert.Equal(1, treeBuilder.BuildFromInventoryCount);
	}

	[Fact]
	public void ReadCompositeInventory_ForwardsRulesAndRootSelectionToSupportedBuilder()
	{
		var treeBuilder = new CompositeInventoryTreeBuilderStub();
		var catalog = new StubLocalizationCatalog(new Dictionary<AppLanguage, IReadOnlyDictionary<string, string>>
		{
			[AppLanguage.En] = new Dictionary<string, string>()
		});
		var localization = new LocalizationService(catalog, AppLanguage.En);
		var useCase = new BuildTreeUseCase(
			treeBuilder,
			new TreeNodePresentationService(localization, new StubIconMapper()));
		var roots = new HashSet<string>(["src"], PathComparer.Default);
		var discoveryRules = CreateOptions().IgnoreRules;
		var projectionRules = discoveryRules with { IgnoreDotFolders = true };

		var result = useCase.ReadCompositeInventory(
			"/root",
			roots,
			discoveryRules,
			projectionRules,
			TestContext.Current.CancellationToken);

		Assert.True(useCase.SupportsCompositeInventory);
		Assert.Same(treeBuilder.Inventory, result);
		Assert.Same(roots, treeBuilder.AllowedRootFolders);
		Assert.Same(discoveryRules, treeBuilder.DiscoveryRules);
		Assert.Same(projectionRules, treeBuilder.ProjectionRules);
	}

	[Fact]
	public void ReadCompositeInventory_UnsupportedBuilderThrowsWithoutStartingBuild()
	{
		var treeBuilder = new StubTreeBuilder();
		var catalog = new StubLocalizationCatalog(new Dictionary<AppLanguage, IReadOnlyDictionary<string, string>>
		{
			[AppLanguage.En] = new Dictionary<string, string>()
		});
		var localization = new LocalizationService(catalog, AppLanguage.En);
		var useCase = new BuildTreeUseCase(
			treeBuilder,
			new TreeNodePresentationService(localization, new StubIconMapper()));
		var rules = CreateOptions().IgnoreRules;

		Assert.False(useCase.SupportsCompositeInventory);
		Assert.Throws<NotSupportedException>(() => useCase.ReadCompositeInventory(
			"/root",
			new HashSet<string>(PathComparer.Default),
			rules,
			rules,
			TestContext.Current.CancellationToken));
	}

	[Fact]
	public void InventoryProjection_DeepTreeDoesNotDependOnTheCallStack()
	{
		const int depth = 4_096;
		var inventory = CreateDeepInventory(depth);
		var options = CreateOptions() with
		{
			AllowedExtensions = new HashSet<string>([".txt"], StringComparer.OrdinalIgnoreCase),
			AllowedRootFolders = new HashSet<string>(["level-0001"], PathComparer.Default)
		};
		var catalog = new StubLocalizationCatalog(new Dictionary<AppLanguage, IReadOnlyDictionary<string, string>>
		{
			[AppLanguage.En] = new Dictionary<string, string>()
		});
		var presenter = new TreeNodePresentationService(
			new LocalizationService(catalog, AppLanguage.En),
			new StubIconMapper { IconKey = "folder" });

		var projected = new DevProjex.Infrastructure.FileSystem.TreeBuilder().Build(
			inventory,
			options,
			TestContext.Current.CancellationToken);
		var result = presenter.BuildWithFilePaths(projected.Root);

		var node = result.Root;
		for (var level = 1; level <= depth; level++)
			node = Assert.Single(node.Children);
		Assert.False(node.IsDirectory);
		Assert.Single(result.OrderedFilePaths);
	}

	private static TreeFilterOptions CreateOptions()
	{
		return new TreeFilterOptions(
			AllowedExtensions: new HashSet<string>(),
			AllowedRootFolders: new HashSet<string>(),
			IgnoreRules: new IgnoreRules(
				IgnoreHiddenFolders: false,
				IgnoreHiddenFiles: false,
				IgnoreDotFolders: false,
				IgnoreDotFiles: false,
				SmartIgnoredFolders: new HashSet<string>(),
				SmartIgnoredFiles: new HashSet<string>()));
	}

	private static ProjectTreeInventorySnapshot CreateSingleRootInventory(string rootPath)
	{
		return new ProjectTreeInventorySnapshot(
			[
				new ProjectTreeInventoryEntry(
					"root",
					rootPath,
					relativePath: string.Empty,
					parentIndex: -1,
					isDirectory: true,
					isHidden: false,
					length: 0)
			],
			rootAccessDenied: false,
			hadAccessDenied: false);
	}

	private static ProjectTreeInventorySnapshot CreateDeepInventory(int depth)
	{
		var entries = new List<ProjectTreeInventoryEntry>(depth + 1)
		{
			new(
				"root",
				"/root",
				relativePath: string.Empty,
				parentIndex: -1,
				isDirectory: true,
				isHidden: false,
				length: 0)
		};

		for (var level = 1; level <= depth; level++)
		{
			var parent = entries[level - 1];
			parent.FirstChildIndex = level;
			parent.ChildCount = 1;
			entries[level - 1] = parent;
			var isDirectory = level < depth;
			var name = isDirectory ? $"level-{level:D4}" : "leaf.txt";
			entries.Add(new ProjectTreeInventoryEntry(
				name,
				$"/root/{name}-{level}",
				name,
				parentIndex: level - 1,
				isDirectory,
				isHidden: false,
				length: isDirectory ? 0 : 1));
		}

		return new ProjectTreeInventorySnapshot(
			entries,
			rootAccessDenied: false,
			hadAccessDenied: false);
	}

	private sealed class InventoryTreeBuilderStub : ITreeBuilder, IProjectTreeInventoryBuilder
	{
		private readonly ProjectTreeInventorySnapshot _inventory = new(
			[
				new ProjectTreeInventoryEntry(
					"root",
					"/root",
					relativePath: string.Empty,
					parentIndex: -1,
					isDirectory: true,
					isHidden: false,
					length: 0)
			],
			rootAccessDenied: false,
			hadAccessDenied: false);

		public int ReadInventoryCount { get; private set; }

		public int BuildFromInventoryCount { get; private set; }

		public ProjectTreeInventorySnapshot? ExpectedBuildInventory { get; set; }

		public TreeBuildResult Build(string rootPath, TreeFilterOptions options, CancellationToken cancellationToken = default)
		{
			_ = rootPath;
			_ = options;
			cancellationToken.ThrowIfCancellationRequested();
			throw new InvalidOperationException("ExecuteWithInventory should use the inventory contract.");
		}

		public ProjectTreeInventorySnapshot ReadInventory(
			string rootPath,
			TreeFilterOptions options,
			CancellationToken cancellationToken = default)
		{
			Assert.Equal("/root", rootPath);
			_ = options;
			cancellationToken.ThrowIfCancellationRequested();
			ReadInventoryCount++;
			return _inventory;
		}

		public TreeBuildResult Build(
			ProjectTreeInventorySnapshot inventory,
			TreeFilterOptions options,
			CancellationToken cancellationToken = default)
		{
			Assert.Same(ExpectedBuildInventory ?? _inventory, inventory);
			_ = options;
			cancellationToken.ThrowIfCancellationRequested();
			BuildFromInventoryCount++;
			return new TreeBuildResult(
				new FileSystemNode("root", "/root", true, false, FileSystemNode.EmptyChildren),
				RootAccessDenied: false,
				HadAccessDenied: false);
		}
	}

	private sealed class CompositeInventoryTreeBuilderStub : ITreeBuilder, IProjectTreeCompositeInventoryBuilder
	{
		public ProjectTreeInventorySnapshot Inventory { get; } = CreateSingleRootInventory("/root");
		public IReadOnlySet<string>? AllowedRootFolders { get; private set; }
		public IgnoreRules? DiscoveryRules { get; private set; }
		public IgnoreRules? ProjectionRules { get; private set; }

		public TreeBuildResult Build(
			string rootPath,
			TreeFilterOptions options,
			CancellationToken cancellationToken = default) =>
			throw new NotSupportedException();

		public ProjectTreeInventorySnapshot ReadCompositeInventory(
			string rootPath,
			IReadOnlySet<string> allowedRootFolders,
			IgnoreRules discoveryRules,
			IgnoreRules projectionRules,
			CancellationToken cancellationToken = default)
		{
			Assert.Equal("/root", rootPath);
			cancellationToken.ThrowIfCancellationRequested();
			AllowedRootFolders = allowedRootFolders;
			DiscoveryRules = discoveryRules;
			ProjectionRules = projectionRules;
			return Inventory;
		}
	}
}




