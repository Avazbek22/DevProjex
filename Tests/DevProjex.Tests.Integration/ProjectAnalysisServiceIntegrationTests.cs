namespace DevProjex.Tests.Integration;

public sealed class ProjectAnalysisServiceIntegrationTests
{
	[Fact]
	public void Load_ExplicitRootSelectionDiscoversDeepRepositoryDefault()
	{
		using var temp = new TemporaryDirectory();
		var current = Path.Combine(temp.Path, "workspace");
		for (var index = 0; index < 16; index++)
			current = Path.Combine(current, $"level-{index:D2}");

		Directory.CreateDirectory(Path.Combine(current, ".git"));
		temp.CreateFile(
			Path.GetRelativePath(temp.Path, Path.Combine(current, "App.cs")),
			"class App {}\n");
		var service = CreateService();

		var loaded = service.Load(
			new ProjectAnalysisRequest(
				temp.Path,
				SelectedRootFolders: ["workspace"]),
			TestContext.Current.CancellationToken);

		Assert.Contains(IgnoreOptionId.UseGitIgnore, loaded.SelectedIgnoreOptions);
		Assert.DoesNotContain(IgnoreOptionId.TrackedGitFilesOnly, loaded.SelectedIgnoreOptions);
		Assert.Contains(".cs", loaded.AvailableExtensions);
	}

	[Fact]
	public void Load_ExplicitAllIgnoreOff_EmptyAndNestedRootsMatchProjectedTree()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("App.csproj", "<Project />\n");
		temp.CreateFile(Path.Combine("src", "level-1", "level-2", "App.cs"), "class App {}\n");
		Directory.CreateDirectory(Path.Combine(temp.Path, "ansel"));
		temp.CreateFile(
			Path.Combine("artifact-root", "temp-build", "obj", "Release", "net10.0", "Generated.g.cs"),
			"generated\n");
		var service = CreateService();

		var loaded = service.Load(
			new ProjectAnalysisRequest(
				RootPath: temp.Path,
				SelectedIgnoreOptions: []),
			TestContext.Current.CancellationToken);
		var treeRoots = loaded.Tree.Root.Children
			.Where(static child => child.IsDirectory)
			.Select(static child => child.DisplayName)
			.ToArray();

		Assert.Equal(["ansel", "artifact-root", "src"], loaded.AvailableRootFolders.Order(PathComparer.Default));
		Assert.Equal(loaded.AvailableRootFolders, loaded.SelectedRootFolders);
		Assert.Equal(loaded.AvailableRootFolders, treeRoots);
		Assert.Equal(treeRoots.Length, treeRoots.Distinct(PathComparer.Default).Count());
	}

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
	public async Task AnalyzeAsync_AllExtensionsWithIgnoreOff_PreservesExtensionlessOptionNames()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("LICENSE", "license\n");
		temp.CreateFile("README", "readme\n");
		temp.CreateFile("notes.readme", "notes\n");
		temp.CreateFile(Path.Combine("src", "App.cs"), "class App {}\n");
		var service = CreateService();

		var report = await service.AnalyzeAsync(
			new ProjectAnalysisRequest(
				RootPath: temp.Path,
				SelectedIgnoreOptions: []),
			TestContext.Current.CancellationToken);

		Assert.Equal(report.Inventory.AvailableExtensions, report.Selection.SelectedExtensions);
		Assert.Contains("LICENSE", report.Selection.SelectedExtensions);
		Assert.Contains("README", report.Selection.SelectedExtensions);
		Assert.Contains(".readme", report.Selection.SelectedExtensions);
		Assert.DoesNotContain(".LICENSE", report.Selection.SelectedExtensions);
		Assert.Equal(4, report.Inventory.Tree.FileCount);
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

	[Fact]
	public async Task BuildReportFromTreeAsync_PropagatesAccessDeniedDiagnosticsFromLoadedTree()
	{
		using var temp = new TemporaryDirectory();
		var file = temp.CreateFile(Path.Combine("src", "App.cs"), "class App {}\n");
		var deniedDirectoryPath = Path.Combine(temp.Path, "protected");
		var root = new TreeNodeDescriptor(
			DisplayName: "workspace",
			FullPath: temp.Path,
			IsDirectory: true,
			IsAccessDenied: false,
			IconKey: "folder",
			Children:
			[
				new TreeNodeDescriptor(
					DisplayName: "protected",
					FullPath: deniedDirectoryPath,
					IsDirectory: true,
					IsAccessDenied: true,
					IconKey: "folder",
					Children: []),
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
			Tree: new BuildTreeResult(root, RootAccessDenied: false, HadAccessDenied: true, OrderedFilePaths: [file]),
			AvailableRootFolders: ["src"],
			AvailableExtensions: [".cs"],
			SelectedRootFolders: ["src"],
			SelectedExtensions: [".cs"],
			SelectedIgnoreOptions: [],
			RootAccessDenied: false,
			HadAccessDenied: true,
			KnownLoadingElapsed: TimeSpan.Zero), TestContext.Current.CancellationToken);

		Assert.False(report.Diagnostics.RootAccessDenied);
		Assert.True(report.Diagnostics.HadAccessDenied);
		Assert.Empty(report.Diagnostics.Warnings);
		Assert.Equal(2, report.Inventory.Tree.DirectoryCount);
		Assert.Equal(1, report.Inventory.Tree.FileCount);
		Assert.Equal(1, report.Inventory.Tree.AccessDeniedDirectoryCount);
	}

	[Fact]
	public async Task BuildReportFromTreeAsync_ReadsMetricsConcurrentlyAndPreservesFileOrder()
	{
		using var temp = new TemporaryDirectory();
		var paths = Enumerable.Range(0, 24)
			.Select(index => temp.CreateFile($"src/File-{index:D2}.cs", $"class File{index} {{}}\n"))
			.ToArray();
		var root = new TreeNodeDescriptor(
			DisplayName: "workspace",
			FullPath: temp.Path,
			IsDirectory: true,
			IsAccessDenied: false,
			IconKey: "folder",
			Children: []);
		var analyzer = new ConcurrentMetricsAnalyzer();
		var service = CreateService(analyzer);

		var report = await service.BuildReportFromTreeAsync(new LoadedProjectAnalysisRequest(
			RootPath: temp.Path,
			Tree: new BuildTreeResult(root, RootAccessDenied: false, HadAccessDenied: false, OrderedFilePaths: paths),
			AvailableRootFolders: ["src"],
			AvailableExtensions: [".cs"],
			SelectedRootFolders: ["src"],
			SelectedExtensions: [".cs"],
			SelectedIgnoreOptions: [],
			RootAccessDenied: false,
			HadAccessDenied: false), TestContext.Current.CancellationToken);
		var expected = ExportOutputMetricsCalculator.FromOrderedContentFiles(paths
			.Select(ConcurrentMetricsAnalyzer.CreateExpectedMetrics)
			.ToArray());

		Assert.True(analyzer.MaximumConcurrency > 1);
		Assert.Equal(expected.Lines, report.Metrics.Content.Lines);
		Assert.Equal(expected.Chars, report.Metrics.Content.Chars);
		Assert.Equal(expected.Tokens, report.Metrics.Content.Tokens);
	}

	private static ProjectAnalysisService CreateService(IFileContentAnalyzer? fileContentAnalyzer = null)
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
			new FilterOptionSelectionService(),
			new IgnoreOptionsService(localization),
			ignoreRules,
			new TreeExportService(),
			fileContentAnalyzer ?? new FileContentAnalyzer(),
			utcNowProvider: () => new DateTimeOffset(2026, 6, 16, 10, 11, 12, TimeSpan.Zero));
	}

	private sealed class ConcurrentMetricsAnalyzer : IFileContentAnalyzer
	{
		private int _activeCalls;
		private int _maximumConcurrency;

		public int MaximumConcurrency => Volatile.Read(ref _maximumConcurrency);

		public ValueTask<bool> IsTextFileAsync(string path, CancellationToken cancellationToken = default) =>
			throw new NotSupportedException();

		public async ValueTask<TextFileMetrics?> GetTextFileMetricsAsync(
			string path,
			CancellationToken cancellationToken = default)
		{
			var concurrency = Interlocked.Increment(ref _activeCalls);
			UpdateMaximumConcurrency(concurrency);
			try
			{
				await Task.Delay(10, cancellationToken);
				return CreateMetrics(path);
			}
			finally
			{
				Interlocked.Decrement(ref _activeCalls);
			}
		}

		public ValueTask<TextFileContent?> TryReadAsTextAsync(
			string path,
			CancellationToken cancellationToken = default) =>
			throw new NotSupportedException();

		public ValueTask<TextFileContent?> TryReadAsTextAsync(
			string path,
			long maxSizeForFullRead,
			CancellationToken cancellationToken = default) =>
			throw new NotSupportedException();

		public static ContentFileMetrics CreateExpectedMetrics(string path)
		{
			var metrics = CreateMetrics(path);
			return new ContentFileMetrics(
				path,
				metrics.SizeBytes,
				metrics.LineCount,
				metrics.CharCount,
				metrics.IsEmpty,
				metrics.IsWhitespaceOnly,
				metrics.IsEstimated,
				metrics.CrLfPairCount,
				metrics.TrailingNewlineChars,
				metrics.TrailingNewlineLineBreaks);
		}

		private static TextFileMetrics CreateMetrics(string path)
		{
			var charCount = Path.GetFileName(path).Length + 5;
			return new TextFileMetrics(
				SizeBytes: charCount,
				LineCount: 2,
				CharCount: charCount,
				IsEmpty: false,
				IsWhitespaceOnly: false);
		}

		private void UpdateMaximumConcurrency(int concurrency)
		{
			var observed = Volatile.Read(ref _maximumConcurrency);
			while (concurrency > observed)
			{
				var previous = Interlocked.CompareExchange(
					ref _maximumConcurrency,
					concurrency,
					observed);
				if (previous == observed)
					return;

				observed = previous;
			}
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
