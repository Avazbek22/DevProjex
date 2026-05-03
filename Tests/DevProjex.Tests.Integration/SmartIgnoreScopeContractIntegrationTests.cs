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
			IgnoreRules: rules));

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
			IgnoreRules: rules));

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
			IgnoreRules: rules));

		Assert.True(rules.UseSmartIgnore);
		Assert.Contains(temp.Path, rules.SmartIgnoreScopeRoots, PathComparer.Default);
		Assert.Contains(Path.Combine(temp.Path, "frontend"), rules.SmartIgnoreScopeRoots, PathComparer.Default);
		Assert.False(ContainsPath(tree.Root, ".venv"));
		Assert.False(ContainsPath(tree.Root, "frontend/node_modules"));
		Assert.True(ContainsPath(tree.Root, "plain-data/node_modules/pkg/index.js"));
	}

	[Fact]
	public void IgnoreRulesService_AllSupportedStackMarkersExposeSmartOptionAndScopedArtifacts()
	{
		var cases = new[]
		{
			new StackCase("frontend", "package.json", "node_modules", "index.js", new FrontendArtifactsIgnoreRule()),
			new StackCase("dotnet", "App.csproj", "bin", "app.dll", new DotNetArtifactsIgnoreRule()),
			new StackCase("python", "requirements.txt", "__pycache__", "app.pyc", new PythonArtifactsIgnoreRule()),
			new StackCase("jvm", "settings.gradle", "build", "classes.bin", new JvmArtifactsIgnoreRule()),
			new StackCase("rust", "Cargo.toml", "target", "app.bin", new RustArtifactsIgnoreRule()),
			new StackCase("go", "go.work", "vendor", "module.go", new GoArtifactsIgnoreRule()),
			new StackCase("php", "composer.json", "vendor", "autoload.php", new PhpArtifactsIgnoreRule()),
			new StackCase("ruby", "Gemfile.lock", "tmp", "cache.txt", new RubyArtifactsIgnoreRule())
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
			new StackCase("frontend", "package.json", "node_modules", "index.js", new FrontendArtifactsIgnoreRule()),
			new StackCase("dotnet", "App.csproj", "bin", "app.dll", new DotNetArtifactsIgnoreRule()),
			new StackCase("python", "requirements.txt", "__pycache__", "app.pyc", new PythonArtifactsIgnoreRule()),
			new StackCase("jvm", "settings.gradle", "build", "classes.bin", new JvmArtifactsIgnoreRule()),
			new StackCase("rust", "Cargo.toml", "target", "app.bin", new RustArtifactsIgnoreRule()),
			new StackCase("go", "go.work", "vendor", "module.go", new GoArtifactsIgnoreRule()),
			new StackCase("php", "composer.json", "vendor", "autoload.php", new PhpArtifactsIgnoreRule()),
			new StackCase("ruby", "Gemfile.lock", "tmp", "cache.txt", new RubyArtifactsIgnoreRule())
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
