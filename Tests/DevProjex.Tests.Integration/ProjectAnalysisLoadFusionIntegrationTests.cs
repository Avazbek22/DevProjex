namespace DevProjex.Tests.Integration;

public sealed class ProjectAnalysisLoadFusionIntegrationTests
{
	[Fact]
	public async Task Load_FusedWorkspaceScanMatchesLegacyPipelineAcrossSelectionMatrix()
	{
		using var temp = CreateMixedWorkspace();
		var fusedService = CreateService(new FileSystemScanner(), new TreeBuilder());
		var legacyService = CreateService(
			new LegacyForwardingScanner(new FileSystemScanner()),
			new TreeBuilder());
		var requests = new ProjectAnalysisRequest[]
		{
			new(temp.Path),
			new(temp.Path, SelectedRootFolders: ["api"]),
			new(
				temp.Path,
				SelectedRootFolders: ["api", "client"],
				SelectedExtensions: [".cs", ".ts"],
				SelectedIgnoreOptions: []),
			new(
				temp.Path,
				SelectedRootFolders: ["missing"],
				SelectedExtensions: [".missing"],
				SelectedIgnoreOptions: []),
			new(
				temp.Path,
				SelectedExtensions: [".cs", ".md", ".txt"],
				SelectedIgnoreOptions:
				[
					IgnoreOptionId.UseGitIgnore,
					IgnoreOptionId.SmartIgnore,
					IgnoreOptionId.HiddenFolders,
					IgnoreOptionId.HiddenFiles,
					IgnoreOptionId.DotFolders,
					IgnoreOptionId.DotFiles,
					IgnoreOptionId.EmptyFolders,
					IgnoreOptionId.EmptyFiles,
					IgnoreOptionId.ExtensionlessFiles
				])
		};

		foreach (var request in requests)
		{
			var legacy = legacyService.Load(request, TestContext.Current.CancellationToken);
			var fused = fusedService.Load(request, TestContext.Current.CancellationToken);

			AssertLoadedProjectEquivalent(legacy, fused);

			var legacyReport = await legacyService
				.BuildReportFromTreeAsync(legacy, TestContext.Current.CancellationToken);
			var fusedReport = await fusedService
				.BuildReportFromTreeAsync(fused, TestContext.Current.CancellationToken);
			AssertReportEquivalent(legacyReport, fusedReport);
		}
	}

	[Fact]
	public void Load_FusedWorkspaceScanProjectsCapturedInventoryWithoutDirectTreeRead()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("src/App.cs", "class App {}\n");
		var treeBuilder = new CountingInventoryTreeBuilder();
		var service = CreateService(new FileSystemScanner(), treeBuilder);

		var loaded = service.Load(
			new ProjectAnalysisRequest(temp.Path, SelectedIgnoreOptions: []),
			TestContext.Current.CancellationToken);

		Assert.Single(loaded.Tree.OrderedFilePaths!);
		Assert.Equal(0, treeBuilder.DirectBuildCount);
		Assert.Equal(0, treeBuilder.InventoryReadCount);
		Assert.Equal(1, treeBuilder.CompositeInventoryReadCount);
		Assert.Equal(1, treeBuilder.InventoryProjectionCount);
	}

	[Fact]
	public void CompositeInventory_ExtensionDiscoveryMatchesDirectScannerAcrossRuleMatrix()
	{
		using var temp = CreateMixedWorkspace();
		var scanner = new FileSystemScanner();
		var treeBuilder = new TreeBuilder();
		var ignoreRules = CreateIgnoreRulesService();
		var optionMatrix = new IReadOnlyCollection<IgnoreOptionId>[]
		{
			[],
			[
				IgnoreOptionId.DotFolders,
				IgnoreOptionId.DotFiles,
				IgnoreOptionId.EmptyFolders,
				IgnoreOptionId.EmptyFiles,
				IgnoreOptionId.ExtensionlessFiles
			],
			[IgnoreOptionId.UseGitIgnore, IgnoreOptionId.SmartIgnore],
			[
				IgnoreOptionId.UseGitIgnore,
				IgnoreOptionId.SmartIgnore,
				IgnoreOptionId.HiddenFolders,
				IgnoreOptionId.HiddenFiles,
				IgnoreOptionId.DotFolders,
				IgnoreOptionId.DotFiles,
				IgnoreOptionId.EmptyFolders,
				IgnoreOptionId.EmptyFiles,
				IgnoreOptionId.ExtensionlessFiles
			]
		};

		foreach (var selectedOptions in optionMatrix)
		{
			var discoveryRules = ignoreRules.Build(temp.Path, selectedOptions, selectedRootFolders: []);
			var rootFolders = scanner.GetRootFolderNames(
				temp.Path,
				discoveryRules,
				TestContext.Current.CancellationToken);
			var projectionRules = ignoreRules.Build(temp.Path, selectedOptions, rootFolders.Value);
			var inventory = treeBuilder.ReadCompositeInventory(
				temp.Path,
				rootFolders.Value.ToHashSet(PathComparer.Default),
				discoveryRules,
				projectionRules,
				TestContext.Current.CancellationToken);

			var expected = scanner.GetExtensions(
				temp.Path,
				discoveryRules,
				TestContext.Current.CancellationToken);
			var actual = ProjectTreeInventoryExtensionDiscovery.GetVisibleExtensions(
				inventory,
				discoveryRules,
				TestContext.Current.CancellationToken);

			Assert.Equal(
				expected.Value.OrderBy(static value => value, StringComparer.OrdinalIgnoreCase),
				actual.OrderBy(static value => value, StringComparer.OrdinalIgnoreCase));
		}
	}

	[Fact]
	public void InventoryExtensionDiscovery_CanceledTokenStopsBeforeTraversal()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("src/App.cs", "class App {}\n");
		var rules = CreateIgnoreRulesService().Build(temp.Path, selectedOptions: []);
		var inventory = new TreeBuilder().ReadInventory(
			temp.Path,
			new TreeFilterOptions(
				new HashSet<string>([".cs"], StringComparer.OrdinalIgnoreCase),
				new HashSet<string>(["src"], PathComparer.Default),
				rules),
			TestContext.Current.CancellationToken);
		using var cancellation = new CancellationTokenSource();
		cancellation.Cancel();

		Assert.Throws<OperationCanceledException>(() =>
			ProjectTreeInventoryExtensionDiscovery.GetVisibleExtensions(
				inventory,
				rules,
				cancellation.Token));
	}

	[Fact]
	public void Load_ExplicitRootSelectionKeepsLegacyBoundedInventoryPath()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("src/App.cs", "class App {}\n");
		temp.CreateFile("docs/readme.md", "# docs\n");
		var treeBuilder = new CountingInventoryTreeBuilder();
		var service = CreateService(new FileSystemScanner(), treeBuilder);

		var loaded = service.Load(
			new ProjectAnalysisRequest(
				temp.Path,
				SelectedRootFolders: ["src"],
				SelectedIgnoreOptions: []),
			TestContext.Current.CancellationToken);

		Assert.Single(loaded.Tree.OrderedFilePaths!);
		Assert.Equal(1, treeBuilder.DirectBuildCount);
		Assert.Equal(0, treeBuilder.CompositeInventoryReadCount);
		Assert.Equal(0, treeBuilder.InventoryProjectionCount);
	}

	[Fact]
	public void Load_DefaultSelectionDiscoversDeepGitIgnoreBeyondBoundedProjectScopes()
	{
		using var temp = new TemporaryDirectory();
		var segments = new List<string> { "workspace" };
		for (var depth = 0; depth < 12; depth++)
			segments.Add($"level-{depth:D2}");
		segments.Add("repo");
		var repo = Path.Combine([.. segments]);
		temp.CreateFile(Path.Combine(repo, ".gitignore"), "*.noise\n");
		var visiblePath = temp.CreateFile(Path.Combine(repo, "visible.txt"), "visible\n");
		var ignoredPath = temp.CreateFile(Path.Combine(repo, "generated.noise"), "ignored\n");
		var service = CreateService(new FileSystemScanner(), new TreeBuilder());

		var loaded = service.Load(
			new ProjectAnalysisRequest(temp.Path),
			TestContext.Current.CancellationToken);

		Assert.Contains(IgnoreOptionId.UseGitIgnore, loaded.SelectedIgnoreOptions);
		Assert.Contains(visiblePath, loaded.Tree.OrderedFilePaths!);
		Assert.DoesNotContain(ignoredPath, loaded.Tree.OrderedFilePaths!);
		Assert.DoesNotContain(".noise", loaded.AvailableExtensions, StringComparer.OrdinalIgnoreCase);
	}

	private static TemporaryDirectory CreateMixedWorkspace()
	{
		var temp = new TemporaryDirectory();
		temp.CreateFile(".gitignore", "ignored-by-git/\n*.tmp\n");
		temp.CreateFile("README.txt", "root\n");
		temp.CreateFile("LICENSE", "license\n");
		temp.CreateFile("root.tmp", "ignored\n");
		temp.CreateFile("api/api.csproj", "<Project />\n");
		temp.CreateFile("api/.gitignore", "generated/\n");
		temp.CreateFile("api/src/App.cs", "class App {}\n");
		temp.CreateFile("api/src/.env", "SECRET=value\n");
		temp.CreateFile("api/src/empty.cs", string.Empty);
		temp.CreateFile("api/generated/Generated.cs", "class Generated {}\n");
		temp.CreateFile("api/bin/Debug/App.dll", "binary\n");
		temp.CreateFile("client/package.json", "{}\n");
		temp.CreateFile("client/src/app.ts", "export {};\n");
		temp.CreateFile("client/node_modules/pkg/index.js", "module.exports = {};\n");
		temp.CreateFile("client/.cache/cache.json", "{}\n");
		temp.CreateFile("docs/readme.md", "# docs\n");
		temp.CreateDirectory("docs/empty");
		temp.CreateFile(".idea/workspace.xml", "<project />\n");
		temp.CreateFile("ignored-by-git/payload.log", "ignored\n");
		return temp;
	}

	private static ProjectAnalysisService CreateService(IFileSystemScanner scanner, ITreeBuilder treeBuilder)
	{
		var localization = new LocalizationService(new TestLocalizationCatalog(), AppLanguage.En);
		var rules = CreateIgnoreRulesService();

		return new ProjectAnalysisService(
			new ScanOptionsUseCase(scanner),
			new BuildTreeUseCase(
				treeBuilder,
				new TreeNodePresentationService(localization, new TestIconMapper())),
			new IgnoreOptionsService(localization),
			rules,
			new TreeExportService(),
			new FileContentAnalyzer(),
			utcNowProvider: () => new DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero));
	}

	private static IgnoreRulesService CreateIgnoreRulesService()
	{
		var smartIgnore = new SmartIgnoreService(
		[
			new CommonSmartIgnoreRule(),
			new FrontendArtifactsIgnoreRule(),
			new DotNetArtifactsIgnoreRule(),
			new PythonArtifactsIgnoreRule(),
			new JvmArtifactsIgnoreRule(),
			new RustArtifactsIgnoreRule(),
			new GoArtifactsIgnoreRule(),
			new PhpArtifactsIgnoreRule(),
			new RubyArtifactsIgnoreRule()
		]);
		return new IgnoreRulesService(smartIgnore);
	}

	private static void AssertLoadedProjectEquivalent(
		LoadedProjectAnalysisRequest expected,
		LoadedProjectAnalysisRequest actual)
	{
		Assert.Equal(expected.RootPath, actual.RootPath);
		Assert.Equal(expected.AvailableRootFolders, actual.AvailableRootFolders);
		Assert.Equal(expected.AvailableExtensions, actual.AvailableExtensions);
		Assert.Equal(expected.SelectedRootFolders, actual.SelectedRootFolders);
		Assert.Equal(expected.SelectedExtensions, actual.SelectedExtensions);
		Assert.Equal(
			expected.SelectedIgnoreOptions.OrderBy(static value => value),
			actual.SelectedIgnoreOptions.OrderBy(static value => value));
		Assert.Equal(expected.RootAccessDenied, actual.RootAccessDenied);
		Assert.Equal(expected.HadAccessDenied, actual.HadAccessDenied);
		Assert.Equal(FlattenTree(expected.Tree.Root), FlattenTree(actual.Tree.Root));
		Assert.Equal(expected.Tree.OrderedFilePaths, actual.Tree.OrderedFilePaths);
		var expectedDiagnostics = ProjectAnalysisService.BuildDiagnostics(expected);
		var actualDiagnostics = ProjectAnalysisService.BuildDiagnostics(actual);
		Assert.Equal(expectedDiagnostics.RootAccessDenied, actualDiagnostics.RootAccessDenied);
		Assert.Equal(expectedDiagnostics.HadAccessDenied, actualDiagnostics.HadAccessDenied);
		Assert.Equal(expectedDiagnostics.Warnings, actualDiagnostics.Warnings);
	}

	private static void AssertReportEquivalent(ProjectAnalysisReport expected, ProjectAnalysisReport actual)
	{
		Assert.Equal(expected.SchemaVersion, actual.SchemaVersion);
		Assert.Equal(expected.GeneratedUtc, actual.GeneratedUtc);
		Assert.Equal(expected.RootPath, actual.RootPath);
		Assert.Equal(expected.Selection.SelectedRootFolders, actual.Selection.SelectedRootFolders);
		Assert.Equal(expected.Selection.SelectedExtensions, actual.Selection.SelectedExtensions);
		Assert.Equal(expected.Selection.SelectedIgnoreOptions, actual.Selection.SelectedIgnoreOptions);
		Assert.Equal(expected.Inventory.AvailableRootFolders, actual.Inventory.AvailableRootFolders);
		Assert.Equal(expected.Inventory.AvailableExtensions, actual.Inventory.AvailableExtensions);
		Assert.Equal(expected.Inventory.Tree, actual.Inventory.Tree);
		Assert.Equal(expected.Metrics, actual.Metrics);
		Assert.Equal(expected.Diagnostics.RootAccessDenied, actual.Diagnostics.RootAccessDenied);
		Assert.Equal(expected.Diagnostics.HadAccessDenied, actual.Diagnostics.HadAccessDenied);
		Assert.Equal(expected.Diagnostics.Warnings, actual.Diagnostics.Warnings);
	}

	private static List<string> FlattenTree(TreeNodeDescriptor root)
	{
		var result = new List<string>();
		var pending = new Stack<TreeNodeDescriptor>();
		pending.Push(root);
		while (pending.Count > 0)
		{
			var node = pending.Pop();
			result.Add(string.Join(
				"|",
				node.DisplayName,
				node.FullPath,
				node.IsDirectory,
				node.IsAccessDenied,
				node.IconKey));
			for (var index = node.Children.Count - 1; index >= 0; index--)
				pending.Push(node.Children[index]);
		}

		return result;
	}

	private sealed class LegacyForwardingScanner(FileSystemScanner inner)
		: IFileSystemScanner, IFileSystemScannerAdvanced
	{
		public bool CanReadRoot(string rootPath) => inner.CanReadRoot(rootPath);

		public ScanResult<HashSet<string>> GetExtensions(
			string rootPath,
			IgnoreRules rules,
			CancellationToken cancellationToken = default) =>
			inner.GetExtensions(rootPath, rules, cancellationToken);

		public ScanResult<HashSet<string>> GetRootFileExtensions(
			string rootPath,
			IgnoreRules rules,
			CancellationToken cancellationToken = default) =>
			inner.GetRootFileExtensions(rootPath, rules, cancellationToken);

		public ScanResult<List<string>> GetRootFolderNames(
			string rootPath,
			IgnoreRules rules,
			CancellationToken cancellationToken = default) =>
			inner.GetRootFolderNames(rootPath, rules, cancellationToken);

		public ScanResult<ExtensionsScanData> GetExtensionsWithIgnoreOptionCounts(
			string rootPath,
			IgnoreRules rules,
			CancellationToken cancellationToken = default) =>
			inner.GetExtensionsWithIgnoreOptionCounts(rootPath, rules, cancellationToken);

		public ScanResult<ExtensionsScanData> GetRootFileExtensionsWithIgnoreOptionCounts(
			string rootPath,
			IgnoreRules rules,
			CancellationToken cancellationToken = default) =>
			inner.GetRootFileExtensionsWithIgnoreOptionCounts(rootPath, rules, cancellationToken);
	}

	private sealed class CountingInventoryTreeBuilder
		: ITreeBuilder, IProjectTreeInventoryBuilder, IProjectTreeCompositeInventoryBuilder
	{
		private readonly TreeBuilder _inner = new();

		public int DirectBuildCount { get; private set; }
		public int InventoryReadCount { get; private set; }
		public int CompositeInventoryReadCount { get; private set; }
		public int InventoryProjectionCount { get; private set; }

		public TreeBuildResult Build(
			string rootPath,
			TreeFilterOptions options,
			CancellationToken cancellationToken = default)
		{
			DirectBuildCount++;
			return _inner.Build(rootPath, options, cancellationToken);
		}

		public ProjectTreeInventorySnapshot ReadInventory(
			string rootPath,
			TreeFilterOptions options,
			CancellationToken cancellationToken = default)
		{
			InventoryReadCount++;
			return _inner.ReadInventory(rootPath, options, cancellationToken);
		}

		public ProjectTreeInventorySnapshot ReadCompositeInventory(
			string rootPath,
			IReadOnlySet<string> allowedRootFolders,
			IgnoreRules discoveryRules,
			IgnoreRules projectionRules,
			CancellationToken cancellationToken = default)
		{
			CompositeInventoryReadCount++;
			return _inner.ReadCompositeInventory(
				rootPath,
				allowedRootFolders,
				discoveryRules,
				projectionRules,
				cancellationToken);
		}

		public TreeBuildResult Build(
			ProjectTreeInventorySnapshot inventory,
			TreeFilterOptions options,
			CancellationToken cancellationToken = default)
		{
			InventoryProjectionCount++;
			return _inner.Build(inventory, options, cancellationToken);
		}
	}

	private sealed class TestLocalizationCatalog : ILocalizationCatalog
	{
		public IReadOnlyDictionary<string, string> Get(AppLanguage language) =>
			new Dictionary<string, string>
			{
				["Tree.AccessDeniedRoot"] = "Access denied",
				["Tree.AccessDenied"] = "Access denied",
				["Settings.Ignore.SmartIgnore"] = "Smart ignore",
				["Settings.Ignore.UseGitIgnore"] = "Use .gitignore",
				["Settings.Ignore.HiddenFolders"] = "Hidden folders",
				["Settings.Ignore.HiddenFiles"] = "Hidden files",
				["Settings.Ignore.DotFolders"] = "Dot folders",
				["Settings.Ignore.DotFiles"] = "Dot files",
				["Settings.Ignore.EmptyFolders"] = "Empty folders",
				["Settings.Ignore.EmptyFiles"] = "Empty files",
				["Settings.Ignore.ExtensionlessFiles"] = "Files without extension"
			};
	}

	private sealed class TestIconMapper : IIconMapper
	{
		public string GetIconKey(FileSystemNode node) => node.IsDirectory ? "folder" : "file";
	}
}
