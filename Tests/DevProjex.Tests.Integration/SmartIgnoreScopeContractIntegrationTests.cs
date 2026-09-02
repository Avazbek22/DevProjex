namespace DevProjex.Tests.Integration;

public sealed class SmartIgnoreScopeContractIntegrationTests
{
	[Fact]
	public void TreeBuilder_PythonRootMarkerWithExplicitRootSelection_HidesPythonArtifactsAndKeepsSmartOptionAvailable()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("pyproject.toml", "[project]\nname = \"api\"\n");
		temp.CreateFile("src/app.py", "print('ok')\n");
		temp.CreateFile("src/__pycache__/app.pyc", "binary");
		temp.CreateFile("src/.venv/pyvenv.cfg", "home = python\n");
		temp.CreateFile("src/.venv/bin/python", "binary");

		var rulesService = new IgnoreRulesService(new SmartIgnoreService([
			new PythonArtifactsIgnoreRule()
		]));
		var availability = rulesService.GetIgnoreOptionsAvailability(temp.Path, ["src"]);
		var rules = rulesService.Build(
			temp.Path,
			[IgnoreOptionId.SmartIgnore],
			selectedRootFolders: ["src"]);

		var tree = new TreeBuilder().Build(temp.Path, new TreeFilterOptions(
			AllowedExtensions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".py", ".pyc" },
			AllowedRootFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "src" },
			IgnoreRules: rules), cancellationToken: TestContext.Current.CancellationToken);

		Assert.True(availability.IncludeSmartIgnore);
		Assert.True(rules.UseSmartIgnore);
		Assert.True(ContainsPath(tree.Root, "src/app.py"));
		Assert.False(ContainsPath(tree.Root, "src/__pycache__"));
		Assert.False(ContainsPath(tree.Root, "src/.venv"));
	}

	[Fact]
	public void TreeBuilder_SmartIgnoreArtifactsStayScopedToMatchingProjectMarkers()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("python-worker/pyproject.toml", "[project]\nname = \"worker\"\n");
		temp.CreateFile("python-worker/app.py", "print('ok')\n");
		temp.CreateFile("python-worker/__pycache__/app.pyc", "binary");
		temp.CreateFile("plain-data/notes.py", "print('not a python project marker')\n");
		temp.CreateFile("plain-data/__pycache__/snapshot.pyc", "must stay visible because plain-data is not a Python project scope");

		var rulesService = new IgnoreRulesService(new SmartIgnoreService([
			new PythonArtifactsIgnoreRule()
		]));
		var rules = rulesService.Build(
			temp.Path,
			[IgnoreOptionId.SmartIgnore],
			selectedRootFolders: ["python-worker", "plain-data"]);

		var tree = new TreeBuilder().Build(temp.Path, new TreeFilterOptions(
			AllowedExtensions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".py", ".pyc" },
			AllowedRootFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "python-worker", "plain-data" },
			IgnoreRules: rules), cancellationToken: TestContext.Current.CancellationToken);

		Assert.True(rules.UseSmartIgnore);
		Assert.True(ContainsPath(tree.Root, "python-worker/app.py"));
		Assert.False(ContainsPath(tree.Root, "python-worker/__pycache__"));
		Assert.True(ContainsPath(tree.Root, "plain-data/__pycache__/snapshot.pyc"));
	}

	[Fact]
	public void TreeBuilder_RootProjectMarkerWithNestedProjects_KeepsRootAndNestedSmartScopes()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("pyproject.toml", "[project]\nname = \"workspace\"\n");
		temp.CreateFile("src/app.py", "print('ok')\n");
		temp.CreateFile(".venv/pyvenv.cfg", "home = python\n");
		temp.CreateFile("frontend/package.json", "{}");
		temp.CreateFile("frontend/node_modules/.package-lock.json", "{}");
		temp.CreateFile("frontend/node_modules/pkg/index.js", "module.exports = {};");
		temp.CreateFile("plain-data/node_modules/pkg/index.js", "must stay visible");

		var rulesService = new IgnoreRulesService(new SmartIgnoreService([
			new PythonArtifactsIgnoreRule(),
			new FrontendArtifactsIgnoreRule()
		]));
		var rules = rulesService.Build(temp.Path, [IgnoreOptionId.SmartIgnore], selectedRootFolders: []);

		var tree = new TreeBuilder().Build(temp.Path, new TreeFilterOptions(
			AllowedExtensions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".py", ".cfg", ".json", ".js" },
			AllowedRootFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase)
			{
				".venv",
				"frontend",
				"plain-data",
				"src"
			},
			IgnoreRules: rules), cancellationToken: TestContext.Current.CancellationToken);

		Assert.True(rules.UseSmartIgnore);
		Assert.Contains(temp.Path, rules.SmartIgnoreScopeRoots, PathComparer.Default);
		Assert.Contains(Path.Combine(temp.Path, "frontend"), rules.SmartIgnoreScopeRoots, PathComparer.Default);
		Assert.False(ContainsPath(tree.Root, ".venv"));
		Assert.False(ContainsPath(tree.Root, "frontend/node_modules"));
		Assert.True(ContainsPath(tree.Root, "plain-data/node_modules/pkg/index.js"));
	}

	[Fact]
	public void TreeBuilder_DeepMonorepoSmartScope_HidesArtifactsWithoutBleedingIntoSibling()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("pnpm-workspace.yaml", "packages:\n  - apps/**\n");
		temp.CreateFile("apps/domain/team/python-worker/pyproject.toml", "[project]\nname = \"worker\"\n");
		temp.CreateFile("apps/domain/team/python-worker/app.py", "print('ok')\n");
		temp.CreateFile("apps/domain/team/python-worker/__pycache__/app.pyc", "binary");
		temp.CreateFile("apps/domain/team/plain-data/notes.py", "print('visible')\n");
		temp.CreateFile("apps/domain/team/plain-data/__pycache__/snapshot.pyc", "must stay visible");

		var rulesService = new IgnoreRulesService(new SmartIgnoreService([
			new PythonArtifactsIgnoreRule()
		]));
		var availability = rulesService.GetIgnoreOptionsAvailability(temp.Path, []);
		var rules = rulesService.Build(temp.Path, [IgnoreOptionId.SmartIgnore], selectedRootFolders: []);

		var tree = new TreeBuilder().Build(temp.Path, new TreeFilterOptions(
			AllowedExtensions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".yaml", ".toml", ".py", ".pyc" },
			AllowedRootFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "apps" },
			IgnoreRules: rules), cancellationToken: TestContext.Current.CancellationToken);

		Assert.True(availability.IncludeSmartIgnore);
		Assert.True(rules.UseSmartIgnore);
		Assert.Contains(rules.SmartIgnoreScopeRoots, scope =>
			scope.EndsWith(
				NormalizeRelativePath("apps/domain/team/python-worker"),
				StringComparison.OrdinalIgnoreCase));
		Assert.True(ContainsPath(tree.Root, "apps/domain/team/python-worker/app.py"));
		Assert.False(ContainsPath(tree.Root, "apps/domain/team/python-worker/__pycache__"));
		Assert.True(ContainsPath(tree.Root, "apps/domain/team/plain-data/__pycache__/snapshot.pyc"));
	}

	[Fact]
	public void TreeBuilder_SwiftAndDartArtifactScopesDoNotBleedIntoUnmarkedSiblings()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("apps/apple/Package.swift", "// swift-tools-version: 6.0\n");
		temp.CreateFile("apps/apple/Pods/Manifest.lock", "generated\n");
		temp.CreateFile("apps/apple/Pods/Sources/Generated.swift", "generated\n");
		temp.CreateFile("apps/flutter/pubspec.yaml", "name: fixture\n");
		temp.CreateFile("apps/flutter/build/flutter_assets/AssetManifest.bin", "generated\n");
		temp.CreateFile("apps/flutter/build/generated.dart", "generated\n");
		temp.CreateFile("apps/plain/Pods/Manifest.lock", "hand-written fixture\n");
		temp.CreateFile("apps/plain/Pods/Sources/Visible.swift", "visible\n");
		temp.CreateFile("apps/plain/build/flutter_assets/AssetManifest.bin", "hand-written fixture\n");
		temp.CreateFile("apps/plain/build/visible.dart", "visible\n");

		var rulesService = new IgnoreRulesService(new SmartIgnoreService([
			new SwiftArtifactsIgnoreRule(),
			new DartArtifactsIgnoreRule()
		]));
		var rules = rulesService.Build(temp.Path, [IgnoreOptionId.SmartIgnore], selectedRootFolders: []);
		var tree = new TreeBuilder().Build(temp.Path, new TreeFilterOptions(
			AllowedExtensions: new HashSet<string>(StringComparer.OrdinalIgnoreCase)
			{
				".bin", ".dart", ".lock", ".swift", ".yaml"
			},
			AllowedRootFolders: new HashSet<string>(PathComparer.Default) { "apps" },
			IgnoreRules: rules), TestContext.Current.CancellationToken);

		Assert.False(ContainsPath(tree.Root, "apps/apple/Pods"));
		Assert.False(ContainsPath(tree.Root, "apps/flutter/build"));
		Assert.True(ContainsPath(tree.Root, "apps/plain/Pods/Sources/Visible.swift"));
		Assert.True(ContainsPath(tree.Root, "apps/plain/build/visible.dart"));
	}

	[Fact]
	public void TreeBuilder_DeepMonorepoGitAndSmartOptions_ComposeWithoutChangingSiblingVisibility()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("pnpm-workspace.yaml", "packages:\n  - apps/**\n");
		temp.CreateFile("apps/domain/team/api/.gitignore", "generated/\n");
		temp.CreateFile("apps/domain/team/api/src/app.cs", "class App {}");
		temp.CreateFile("apps/domain/team/api/generated/drop.cs", "class Drop {}");
		temp.CreateFile("apps/domain/team/python-worker/pyproject.toml", "[project]\nname = \"worker\"\n");
		temp.CreateFile("apps/domain/team/python-worker/app.py", "print('ok')\n");
		temp.CreateFile("apps/domain/team/python-worker/__pycache__/app.pyc", "binary");
		temp.CreateFile("apps/domain/team/plain-data/generated/drop.cs", "class Visible {}");
		temp.CreateFile("apps/domain/team/plain-data/__pycache__/snapshot.pyc", "visible");

		var rulesService = new IgnoreRulesService(new SmartIgnoreService([
			new PythonArtifactsIgnoreRule()
		]));
		var availability = rulesService.GetIgnoreOptionsAvailability(temp.Path, []);
		var rules = rulesService.Build(
			temp.Path,
			[IgnoreOptionId.UseGitIgnore, IgnoreOptionId.SmartIgnore],
			selectedRootFolders: []);

		var tree = new TreeBuilder().Build(temp.Path, new TreeFilterOptions(
			AllowedExtensions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".yaml", ".toml", ".cs", ".py", ".pyc" },
			AllowedRootFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "apps" },
			IgnoreRules: rules), cancellationToken: TestContext.Current.CancellationToken);

		Assert.True(availability.IncludeGitIgnore);
		Assert.True(availability.IncludeSmartIgnore);
		Assert.True(rules.UseGitIgnore);
		Assert.True(rules.UseSmartIgnore);
		Assert.True(ContainsPath(tree.Root, "apps/domain/team/api/src/app.cs"));
		Assert.False(ContainsPath(tree.Root, "apps/domain/team/api/generated"));
		Assert.False(ContainsPath(tree.Root, "apps/domain/team/python-worker/__pycache__"));
		Assert.True(ContainsPath(tree.Root, "apps/domain/team/plain-data/generated/drop.cs"));
		Assert.True(ContainsPath(tree.Root, "apps/domain/team/plain-data/__pycache__/snapshot.pyc"));
	}

	[Fact]
	public void ScanOptions_DeepMonorepoScopes_ReportGitAndSmartControllerImpact()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("pnpm-workspace.yaml", "packages:\n  - apps/**\n");
		temp.CreateFile("apps/domain/team/api/.gitignore", "generated/\n");
		temp.CreateFile("apps/domain/team/api/src/app.cs", "class App {}");
		temp.CreateFile("apps/domain/team/api/generated/drop.cs", "class Drop {}");
		temp.CreateFile("apps/domain/team/python-worker/pyproject.toml", "[project]\nname = \"worker\"\n");
		temp.CreateFile("apps/domain/team/python-worker/app.py", "print('ok')\n");
		temp.CreateFile("apps/domain/team/python-worker/__pycache__/app.pyc", "binary");

		var selectedRoots = new HashSet<string>(PathComparer.Default) { "apps" };
		var selectedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
		{
			".cs",
			".py",
			".pyc",
			".toml",
			".yaml"
		};
		var rulesService = new IgnoreRulesService(new SmartIgnoreService([
			new PythonArtifactsIgnoreRule()
		]));
		var rules = rulesService.Build(
			temp.Path,
			[IgnoreOptionId.UseGitIgnore, IgnoreOptionId.SmartIgnore],
			selectedRoots);
		var scanner = new ScanOptionsUseCase(new FileSystemScanner());

		var snapshot = scanner.GetProjectWorkspaceSnapshotForRootFolders(
			temp.Path,
			selectedRoots,
			extensionDiscoveryRules: rules,
			effectiveRules: rules,
			effectiveExtensionPolicy: new ExtensionSetInclusionPolicy(selectedExtensions),
			includeDirectoryToggleProbeRoots: true,
			cancellationToken: TestContext.Current.CancellationToken,
			includeControllerImpactProbeRoots: true);

		Assert.NotNull(snapshot.Value.TreeInventory);
		Assert.True(snapshot.Value.IgnoreSection.ControllerImpactCounts.GitIgnore > 0);
		Assert.True(snapshot.Value.IgnoreSection.ControllerImpactCounts.SmartIgnore > 0);
		Assert.Contains(".cs", snapshot.Value.IgnoreSection.Extensions);
		Assert.Contains(".py", snapshot.Value.IgnoreSection.Extensions);
	}

	[Fact]
	public void IgnoreRulesService_AllSupportedStackMarkersExposeSmartOptionAndScopedArtifacts()
	{
		var cases = new[]
		{
			new StackCase("frontend", "package.json", "node_modules", ".package-lock.json", new FrontendArtifactsIgnoreRule()),
			new StackCase("dotnet", "App.csproj", "bin", "app.dll", new DotNetArtifactsIgnoreRule()),
			new StackCase("python", "requirements.txt", "__pycache__", "app.pyc", new PythonArtifactsIgnoreRule()),
			new StackCase("jvm", "settings.gradle", "build", "classes/App.class", new JvmArtifactsIgnoreRule()),
			new StackCase("rust", "Cargo.toml", "target", "debug/app", new RustArtifactsIgnoreRule()),
			new StackCase("go", "go.work", "vendor", "modules.txt", new GoArtifactsIgnoreRule()),
			new StackCase("php", "composer.json", "vendor", "autoload.php", new PhpArtifactsIgnoreRule()),
			new StackCase("ruby", "Gemfile.lock", "tmp", "CACHEDIR.TAG", new RubyArtifactsIgnoreRule()),
			new StackCase("swift", "Package.swift", ".build", "workspace-state.json", new SwiftArtifactsIgnoreRule()),
			new StackCase("dart", "pubspec.yaml", ".dart_tool", "package_config.json", new DartArtifactsIgnoreRule())
		};

		foreach (var testCase in cases)
		{
			using var temp = new TemporaryDirectory();
			temp.CreateFile(testCase.MarkerFile, string.Empty);
			temp.CreateFile(Path.Combine(testCase.ArtifactFolder, testCase.ArtifactFile), "artifact");

			var rulesService = new IgnoreRulesService(new SmartIgnoreService([testCase.Rule]));
			var availability = rulesService.GetIgnoreOptionsAvailability(temp.Path, []);
			var rules = rulesService.Build(temp.Path, [IgnoreOptionId.SmartIgnore], selectedRootFolders: []);

			Assert.True(availability.IncludeSmartIgnore, $"{testCase.Name} marker should expose Smart Ignore.");
			Assert.True(rules.IsSmartIgnoredDirectory(
				Path.Combine(temp.Path, testCase.ArtifactFolder),
				testCase.ArtifactFolder));
		}
	}

	[Fact]
	public void TreeBuilder_AllSupportedStackSmartIgnoreCycles_KeepArtifactsAndDotFoldersIndependent()
	{
		var cases = new[]
		{
			new StackCase("frontend", "package.json", "node_modules", ".package-lock.json", new FrontendArtifactsIgnoreRule()),
			new StackCase("dotnet", "App.csproj", "bin", "app.dll", new DotNetArtifactsIgnoreRule()),
			new StackCase("python", "requirements.txt", "__pycache__", "app.pyc", new PythonArtifactsIgnoreRule()),
			new StackCase("jvm", "settings.gradle", "build", "classes/App.class", new JvmArtifactsIgnoreRule()),
			new StackCase("rust", "Cargo.toml", "target", "debug/app", new RustArtifactsIgnoreRule()),
			new StackCase("go", "go.work", "vendor", "modules.txt", new GoArtifactsIgnoreRule()),
			new StackCase("php", "composer.json", "vendor", "autoload.php", new PhpArtifactsIgnoreRule()),
			new StackCase("ruby", "Gemfile.lock", "tmp", "CACHEDIR.TAG", new RubyArtifactsIgnoreRule()),
			new StackCase("swift", "Package.swift", ".build", "workspace-state.json", new SwiftArtifactsIgnoreRule()),
			new StackCase("dart", "pubspec.yaml", ".dart_tool", "package_config.json", new DartArtifactsIgnoreRule())
		};

		foreach (var testCase in cases)
		{
			using var temp = new TemporaryDirectory();
			temp.CreateFile(testCase.MarkerFile, string.Empty);
			temp.CreateFile(Path.Combine(testCase.ArtifactFolder, testCase.ArtifactFile), "artifact");
			temp.CreateFile(".idea/workspace.xml", "<project />");
			temp.CreateFile("src/app.txt", "visible");

			var rulesService = new IgnoreRulesService(new SmartIgnoreService([testCase.Rule]));
			var availability = rulesService.GetIgnoreOptionsAvailability(temp.Path, []);
			Assert.True(availability.IncludeSmartIgnore, $"{testCase.Name} marker should expose Smart Ignore.");

			var smartAndDotTree = BuildStackCycleTree(
				temp.Path,
				rulesService,
				testCase,
				[IgnoreOptionId.SmartIgnore, IgnoreOptionId.DotFolders]);
			Assert.False(ContainsPath(smartAndDotTree.Root, $"{testCase.ArtifactFolder}/{testCase.ArtifactFile}"));
			Assert.False(ContainsPath(smartAndDotTree.Root, ".idea/workspace.xml"));
			Assert.True(ContainsPath(smartAndDotTree.Root, "src/app.txt"));

			var smartOnlyTree = BuildStackCycleTree(
				temp.Path,
				rulesService,
				testCase,
				[IgnoreOptionId.SmartIgnore]);
			Assert.False(ContainsPath(smartOnlyTree.Root, $"{testCase.ArtifactFolder}/{testCase.ArtifactFile}"));
			Assert.True(ContainsPath(smartOnlyTree.Root, ".idea/workspace.xml"));

			var rawTree = BuildStackCycleTree(
				temp.Path,
				rulesService,
				testCase,
				[]);
			Assert.True(ContainsPath(rawTree.Root, $"{testCase.ArtifactFolder}/{testCase.ArtifactFile}"));
			Assert.True(ContainsPath(rawTree.Root, ".idea/workspace.xml"));
		}
	}

	private static bool ContainsPath(FileSystemNode root, string relativePath)
	{
		var segments = relativePath.Split(
			['/', '\\'],
			StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		IReadOnlyList<FileSystemNode> current = root.Children;

		foreach (var segment in segments)
		{
			var next = current.FirstOrDefault(child =>
				string.Equals(child.Name, segment, StringComparison.OrdinalIgnoreCase));
			if (next is null)
				return false;

			current = next.Children;
		}

		return true;
	}

	private static string NormalizeRelativePath(string relativePath) =>
		relativePath
			.Replace('/', Path.DirectorySeparatorChar)
			.Replace('\\', Path.DirectorySeparatorChar);

	private static TreeBuildResult BuildStackCycleTree(
		string rootPath,
		IgnoreRulesService rulesService,
		StackCase testCase,
		IReadOnlyCollection<IgnoreOptionId> selectedOptions)
	{
		var allowedRootFolders = new HashSet<string>(PathComparer.Default)
		{
			".idea",
			testCase.ArtifactFolder,
			"src"
		};
		var allowedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
		{
			".txt",
			".xml"
		};
		var artifactExtension = Path.GetExtension(testCase.ArtifactFile);
		if (!string.IsNullOrWhiteSpace(artifactExtension))
			allowedExtensions.Add(artifactExtension);

		var rules = rulesService.Build(rootPath, selectedOptions, allowedRootFolders);
		return new TreeBuilder().Build(rootPath, new TreeFilterOptions(
			AllowedExtensions: allowedExtensions,
			AllowedRootFolders: allowedRootFolders,
			IgnoreRules: rules));
	}

	private sealed record StackCase(
		string Name,
		string MarkerFile,
		string ArtifactFolder,
		string ArtifactFile,
		ISmartIgnoreRule Rule);
}
