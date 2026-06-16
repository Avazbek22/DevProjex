namespace DevProjex.Tests.Integration;

public sealed class ProjectAnalysisServiceIntegrationTests
{
	[Fact]
	public async Task AnalyzeAsync_WithRootExtensionAndIgnoreOverrides_ReportsFilteredProject()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(Path.Combine("src", "App.cs"), "Console.WriteLine(\"Hello\");\n");
		temp.CreateFile(Path.Combine("src", "appsettings.json"), "{}\n");
		temp.CreateFile(Path.Combine("tests", "AppTests.cs"), "class AppTests {}\n");
		temp.CreateFile(Path.Combine(".cache", "ignored.txt"), "cache\n");
		var service = CreateService();

		var report = await service.AnalyzeAsync(new ProjectAnalysisRequest(
			RootPath: temp.Path,
			SelectedRootFolders: ["src"],
			SelectedExtensions: [".cs"],
			SelectedIgnoreOptions: []), TestContext.Current.CancellationToken);

		Assert.Equal(temp.Path, report.RootPath);
		Assert.Equal(["src"], report.Selection.SelectedRootFolders);
		Assert.Equal([".cs"], report.Selection.SelectedExtensions);
		Assert.Empty(report.Selection.SelectedIgnoreOptions);
		Assert.Contains("src", report.Inventory.AvailableRootFolders);
		Assert.Contains("tests", report.Inventory.AvailableRootFolders);
		Assert.Contains(".json", report.Inventory.AvailableExtensions);
		Assert.Equal(2, report.Inventory.Tree.DirectoryCount);
		Assert.Equal(1, report.Inventory.Tree.FileCount);
		Assert.True(report.Metrics.Tree.Chars > 0);
		Assert.True(report.Metrics.Content.Chars > 0);
		Assert.False(report.Diagnostics.RootAccessDenied);
		Assert.Empty(report.Diagnostics.Warnings);
	}

	[Fact]
	public async Task AnalyzeAsync_UnknownRequestedRootAndExtension_AreReportedAsWarnings()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(Path.Combine("src", "App.cs"), "class App {}\n");
		var service = CreateService();

		var report = await service.AnalyzeAsync(new ProjectAnalysisRequest(
			RootPath: temp.Path,
			SelectedRootFolders: ["missing"],
			SelectedExtensions: [".missing"],
			SelectedIgnoreOptions: []), TestContext.Current.CancellationToken);

		Assert.Contains(report.Diagnostics.Warnings, warning => warning.Contains("missing", StringComparison.OrdinalIgnoreCase));
		Assert.Equal(0, report.Inventory.Tree.FileCount);
		Assert.Equal(ProjectOutputMetricsReport.Empty, report.Metrics.Content);
	}

	[Fact]
	public async Task BuildReportFromTreeAsync_UsesLoadedTreeWithoutRescanningRootFolders()
	{
		using var temp = new TemporaryDirectory();
		var file = temp.CreateFile(Path.Combine("src", "App.cs"), "class App {}\n");
		var root = new TreeNodeDescriptor(
			DisplayName: "workspace",
			FullPath: temp.Path,
			IsDirectory: true,
			IsAccessDenied: false,
			IconKey: "folder",
			Children:
			[
				new TreeNodeDescriptor(
					DisplayName: "App.cs",
					FullPath: file,
					IsDirectory: false,
					IsAccessDenied: false,
					IconKey: "file",
					Children: [])
			]);
		var service = CreateService();

		var report = await service.BuildReportFromTreeAsync(new LoadedProjectAnalysisRequest(
			RootPath: temp.Path,
			Tree: new BuildTreeResult(root, RootAccessDenied: false, HadAccessDenied: false, OrderedFilePaths: [file]),
			AvailableRootFolders: ["src"],
			AvailableExtensions: [".cs"],
			SelectedRootFolders: ["src"],
			SelectedExtensions: [".cs"],
			SelectedIgnoreOptions: [IgnoreOptionId.DotFolders],
			RootAccessDenied: false,
			HadAccessDenied: false,
			KnownLoadingElapsed: TimeSpan.FromMilliseconds(12.345)), TestContext.Current.CancellationToken);

		Assert.Equal(12.345, report.Timing.LoadingMilliseconds);
		Assert.True(report.Timing.AnalysisMilliseconds >= 0);
		Assert.Equal(1, report.Inventory.Tree.DirectoryCount);
		Assert.Equal(1, report.Inventory.Tree.FileCount);
		Assert.Equal([IgnoreOptionId.DotFolders], report.Selection.SelectedIgnoreOptions);
	}

	private static ProjectAnalysisService CreateService()
	{
		var localization = new LocalizationService(new TestLocalizationCatalog(), AppLanguage.En);
		var scanner = new FileSystemScanner();
		var treeBuilder = new TreeBuilder();
		var treePresenter = new TreeNodePresentationService(localization, new TestIconMapper());
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
		var ignoreRules = new IgnoreRulesService(smartIgnore);

		return new ProjectAnalysisService(
			new ScanOptionsUseCase(scanner),
			new BuildTreeUseCase(treeBuilder, treePresenter),
			new IgnoreOptionsService(localization),
			ignoreRules,
			new TreeExportService(),
			new FileContentAnalyzer(),
			utcNowProvider: () => new DateTimeOffset(2026, 6, 16, 10, 11, 12, TimeSpan.Zero));
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
