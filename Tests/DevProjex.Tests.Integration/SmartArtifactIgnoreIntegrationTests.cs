namespace DevProjex.Tests.Integration;

public sealed class SmartArtifactIgnoreIntegrationTests
{
	[Fact]
	public void TreeBuilder_SmartArtifactIgnore_HidesNestedMultiStackArtifactsWithoutProjectMarkers()
	{
		using var temp = new TemporaryDirectory();
		SeedMixedArtifactWorkspace(temp);
		var rules = CreateArtifactRules(useSmartIgnore: true);

		var tree = BuildTree(temp.Path, rules);

		AssertPathVisible(tree, "workspace/app/src/Program.cs");
		AssertPathVisible(tree, "workspace/web/src/index.ts");
		AssertPathVisible(tree, "workspace/python/main.py");
		AssertPathVisible(tree, "workspace/cpp/main.cpp");
		AssertPathVisible(tree, "workspace/unity/Assets/Game.cs");
		AssertPathVisible(tree, "workspace/go/main.go");
		AssertPathVisible(tree, "workspace/build/README.md");
		AssertPathHidden(tree, "workspace/app/obj/project.assets.json");
		AssertPathHidden(tree, "workspace/app/bin/Debug/App.dll");
		AssertPathHidden(tree, "workspace/web/node_modules/.bin/vite");
		AssertPathHidden(tree, "workspace/python/__pycache__/main.cpython-313.pyc");
		AssertPathHidden(tree, "workspace/cpp/cmake-build-debug/CMakeCache.txt");
		AssertPathHidden(tree, "workspace/unity/Library/ArtifactDB");
		AssertPathHidden(tree, "workspace/go/pkg/mod/cache/download/github.com/acme/lib/@v/v1.0.0.mod");
	}

	[Fact]
	public void TreeBuilder_SmartArtifactIgnoreDisabled_KeepsSignatureArtifactDirectoriesVisible()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("src/App.cs", "class App {}\n");
		temp.CreateFile("obj/project.assets.json", "{}\n");
		temp.CreateFile("bin/Debug/App.dll", "binary\n");
		var rules = CreateArtifactRules(useSmartIgnore: false);

		var tree = BuildTree(temp.Path, rules);

		AssertPathVisible(tree, "src/App.cs");
		AssertPathVisible(tree, "obj/project.assets.json");
		AssertPathVisible(tree, "bin/Debug/App.dll");
	}

	[Fact]
	public void IgnoreRulesService_Build_SelectedSmartIgnoreEnablesArtifactMatcherWithoutProjectMarkers()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("src/App.cs", "class App {}\n");
		temp.CreateFile("obj/project.assets.json", "{}\n");
		var service = ProjectLoadWorkflowRuntime.CreateIgnoreRulesService();

		var rules = service.Build(
			temp.Path,
			[IgnoreOptionId.SmartIgnore],
			selectedRootFolders: ["src", "obj"]);
		var tree = BuildTree(temp.Path, rules);

		Assert.True(rules.UseSmartIgnore);
		Assert.True(rules.SmartArtifactIgnoreMatcher.HasRules);
		AssertPathVisible(tree, "src/App.cs");
		AssertPathHidden(tree, "obj/project.assets.json");
	}

	[Fact]
	public void IgnoreRulesService_UiAvailability_UsesTopLevelArtifactCandidateEvidence()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("src/App.cs", "class App {}\n");
		temp.CreateFile("obj/project.assets.json", "{}\n");
		var service = ProjectLoadWorkflowRuntime.CreateIgnoreRulesService();

		var availability = service.GetIgnoreOptionsAvailability(temp.Path, selectedRootFolders: []);

		Assert.True(availability.IncludeSmartIgnore);
	}

	[Fact]
	public void TreeBuilder_SmartArtifactIgnore_DoesNotHideSourceFoldersWithSuspiciousNames()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("build/README.md", "docs\n");
		temp.CreateFile("Library/Book.cs", "class Book {}\n");
		temp.CreateFile("vendor/Domain.cs", "class Domain {}\n");
		temp.CreateFile("cache/CachePolicy.cs", "class CachePolicy {}\n");
		temp.CreateFile("obj/PlainSource.cs", "class PlainSource {}\n");
		temp.CreateFile("pkg/domain.go", "package pkg\n");
		var rules = CreateArtifactRules(useSmartIgnore: true);

		var tree = BuildTree(temp.Path, rules);

		AssertPathVisible(tree, "build/README.md");
		AssertPathVisible(tree, "Library/Book.cs");
		AssertPathVisible(tree, "vendor/Domain.cs");
		AssertPathVisible(tree, "cache/CachePolicy.cs");
		AssertPathVisible(tree, "obj/PlainSource.cs");
		AssertPathVisible(tree, "pkg/domain.go");
	}

	[Fact]
	public void TreeBuilder_SmartArtifactIgnore_HidesStandardCacheTagDirectoriesButKeepsPlainTempFolders()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("tmp/CACHEDIR.TAG", "Signature: 8a477f597d28d172789f06886806bc55\n");
		temp.CreateFile("temp/notes.md", "real temp notes\n");
		temp.CreateFile("src/App.cs", "class App {}\n");
		var rules = CreateArtifactRules(useSmartIgnore: true);

		var tree = BuildTree(temp.Path, rules);

		AssertPathVisible(tree, "src/App.cs");
		AssertPathVisible(tree, "temp/notes.md");
		AssertPathHidden(tree, "tmp/CACHEDIR.TAG");
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void TreeBuilder_LegacyDependencyStoreAndUserState_FollowOnlySmartIgnoreToggle(
		bool useSmartIgnore)
	{
		using var temp = new TemporaryDirectory();
		SeedLegacyNuGetWorkspace(temp);
		var rules = CreateArtifactRules(useSmartIgnore);

		var tree = BuildTree(temp.Path, rules);

		AssertPathVisible(tree, "src/App.cs");
		AssertPathVisible(tree, "packages.config");
		AssertPathVisible(tree, "App.sln.DotSettings");
		Assert.Equal(!useSmartIgnore, ContainsPath(tree.Root, "App.sln.DotSettings.user"));
		Assert.Equal(!useSmartIgnore, ContainsPath(
			tree.Root,
			"packages/Alpha.1.0.0/Alpha.1.0.0.nupkg"));
		Assert.Equal(!useSmartIgnore, ContainsPath(
			tree.Root,
			"packages/Beta.2.0.0/lib/Beta.dll"));
	}

	[Fact]
	public void TreeBuilder_SourcePackagesMonorepo_IsNotHiddenByGenericArtifactMatcher()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("packages/api/package.json", "{}");
		temp.CreateFile("packages/api/src/index.ts", "export {};");
		temp.CreateFile("packages/domain/Domain.csproj", "<Project />");
		temp.CreateFile("packages/domain/Order.cs", "class Order {}");
		var rules = CreateArtifactRules(useSmartIgnore: true);

		var tree = BuildTree(temp.Path, rules);

		AssertPathVisible(tree, "packages/api/src/index.ts");
		AssertPathVisible(tree, "packages/domain/Order.cs");
	}

	[Fact]
	public void TreeBuilder_ReusedRulesHideDirectoryAfterArtifactSignatureAppears()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("src/App.cs", "class App {}\n");
		temp.CreateFile("packages/README.md", "source packages\n");
		var rules = CreateArtifactRules(useSmartIgnore: true);

		AssertPathVisible(BuildTree(temp.Path, rules), "packages/README.md");

		temp.CreateFile("packages/repositories.config", "<repositories />\n");

		var refreshedTree = BuildTree(temp.Path, rules);
		AssertPathVisible(refreshedTree, "src/App.cs");
		AssertPathHidden(refreshedTree, "packages/README.md");
	}

	[Fact]
	public void TreeBuilder_ReusedRulesRevealSourceDirectoryAfterArtifactSignatureDisappears()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("src/App.cs", "class App {}\n");
		temp.CreateFile("packages/README.md", "source packages\n");
		var markerPath = temp.CreateFile("packages/repositories.config", "<repositories />\n");
		var rules = CreateArtifactRules(useSmartIgnore: true);

		AssertPathHidden(BuildTree(temp.Path, rules), "packages/README.md");

		File.Delete(markerPath);

		var refreshedTree = BuildTree(temp.Path, rules);
		AssertPathVisible(refreshedTree, "src/App.cs");
		AssertPathVisible(refreshedTree, "packages/README.md");
	}

	[Fact]
	public void IgnoreRulesService_SingleGitIgnoreControllerDoesNotControlGenericArtifacts()
	{
		using var temp = new TemporaryDirectory();
		SeedLegacyNuGetWorkspace(temp);
		temp.CreateFile(".gitignore", "*.log\n");
		temp.CreateFile("App.csproj", "<Project />\n");
		var service = ProjectLoadWorkflowRuntime.CreateIgnoreRulesService();

		var gitOnlyRules = service.Build(
			temp.Path,
			[IgnoreOptionId.UseGitIgnore],
			selectedRootFolders: []);
		var smartOnlyRules = service.Build(
			temp.Path,
			[IgnoreOptionId.SmartIgnore],
			selectedRootFolders: []);

		Assert.False(gitOnlyRules.UseSmartIgnore);
		Assert.True(smartOnlyRules.UseSmartIgnore);
		AssertPathVisible(
			BuildTree(temp.Path, gitOnlyRules),
			"packages/Alpha.1.0.0/Alpha.1.0.0.nupkg");
		AssertPathHidden(
			BuildTree(temp.Path, smartOnlyRules),
			"packages/Alpha.1.0.0/Alpha.1.0.0.nupkg");
	}

	[Fact]
	public async Task TerminalExport_SmartIgnoreHidesSignatureArtifactsWithoutMarkers()
	{
		using var temp = new TemporaryDirectory();
		SeedMixedArtifactWorkspace(temp);
		var terminal = new TerminalTestHost();

		var exitCode = await terminal.RunAsync(
			[
				"export", "context", temp.Path,
				"--view", "tree",
				"--format", "text",
				"--git-mode", "none",
				"--exclude", "smart-ignore",
				"-o", "-"
			],
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Equal(string.Empty, terminal.StandardError);
		var stdout = terminal.StandardOutput;
		Assert.Contains("Program.cs", stdout, StringComparison.Ordinal);
		Assert.Contains("index.ts", stdout, StringComparison.Ordinal);
		Assert.Contains("main.py", stdout, StringComparison.Ordinal);
		Assert.Contains("README.md", stdout, StringComparison.Ordinal);
		Assert.DoesNotContain("project.assets.json", stdout, StringComparison.Ordinal);
		Assert.DoesNotContain("node_modules", stdout, StringComparison.Ordinal);
		Assert.DoesNotContain("__pycache__", stdout, StringComparison.Ordinal);
		Assert.DoesNotContain("cmake-build-debug", stdout, StringComparison.Ordinal);
		Assert.DoesNotContain("ArtifactDB", stdout, StringComparison.Ordinal);
	}

	[Fact]
	public async Task TerminalExport_OtherExclusionsDoNotActivateSmartArtifacts()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("src/App.cs", "class App {}\n");
		temp.CreateFile("obj/project.assets.json", "{}\n");
		temp.CreateFile(".dot/payload.txt", "dot folder\n");
		var terminal = new TerminalTestHost();
		var exitCode = await terminal.RunAsync(
			[
				"export", "context", temp.Path,
				"--view", "tree",
				"--format", "text",
				"--git-mode", "none",
				"--exclude", "dot-folders",
				"--exclude", "empty-files",
				"--exclude", "extensionless-files",
				"-o", "-"
			],
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Equal(string.Empty, terminal.StandardError);
		var stdout = terminal.StandardOutput;
		Assert.Contains("App.cs", stdout, StringComparison.Ordinal);
		Assert.Contains("project.assets.json", stdout, StringComparison.Ordinal);
		Assert.DoesNotContain(".dot", stdout, StringComparison.Ordinal);
	}

	[Fact]
	public async Task TerminalExport_SmartIgnoreHidesLegacyPackagesAndLocalProjectState()
	{
		using var temp = new TemporaryDirectory();
		SeedLegacyNuGetWorkspace(temp);
		var terminal = new TerminalTestHost();
		var exitCode = await terminal.RunAsync(
			[
				"export", "context", temp.Path,
				"--view", "tree",
				"--format", "text",
				"--git-mode", "none",
				"--exclude", "smart-ignore",
				"-o", "-"
			],
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Equal(string.Empty, terminal.StandardError);
		var stdout = terminal.StandardOutput;
		Assert.Contains("App.cs", stdout, StringComparison.Ordinal);
		Assert.Contains("packages.config", stdout, StringComparison.Ordinal);
		Assert.Contains("App.sln.DotSettings", stdout, StringComparison.Ordinal);
		Assert.DoesNotContain("Alpha.1.0.0", stdout, StringComparison.Ordinal);
		Assert.DoesNotContain("Beta.2.0.0", stdout, StringComparison.Ordinal);
		Assert.DoesNotContain("DotSettings.user", stdout, StringComparison.Ordinal);
	}

	private static void SeedMixedArtifactWorkspace(TemporaryDirectory temp)
	{
		temp.CreateFile("workspace/app/src/Program.cs", "class Program {}\n");
		temp.CreateFile("workspace/app/obj/project.assets.json", "{}\n");
		temp.CreateFile("workspace/app/bin/Debug/App.dll", "binary\n");
		temp.CreateFile("workspace/web/src/index.ts", "export const ok = true;\n");
		temp.CreateFile("workspace/web/node_modules/.bin/vite", "binary\n");
		temp.CreateFile("workspace/python/main.py", "print('ok')\n");
		temp.CreateFile("workspace/python/__pycache__/main.cpython-313.pyc", "cache\n");
		temp.CreateFile("workspace/cpp/main.cpp", "int main() { return 0; }\n");
		temp.CreateFile("workspace/cpp/cmake-build-debug/CMakeCache.txt", "cache\n");
		temp.CreateFile("workspace/unity/Assets/Game.cs", "class Game {}\n");
		temp.CreateFile("workspace/unity/Library/ArtifactDB", "artifact db\n");
		temp.CreateFile("workspace/go/main.go", "package main\n");
		temp.CreateFile("workspace/go/pkg/mod/cache/download/github.com/acme/lib/@v/v1.0.0.mod", "module github.com/acme/lib\n");
		temp.CreateFile("workspace/build/README.md", "source folder with suspicious name\n");
	}

	private static void SeedLegacyNuGetWorkspace(TemporaryDirectory temp)
	{
		temp.CreateFile("src/App.cs", "class App {}\n");
		temp.CreateFile("packages.config", "<packages />\n");
		temp.CreateFile("App.sln.DotSettings", "shared settings\n");
		temp.CreateFile("App.sln.DotSettings.user", "local settings\n");
		temp.CreateFile("packages/Alpha.1.0.0/Alpha.1.0.0.nupkg", "package\n");
		temp.CreateFile("packages/Alpha.1.0.0/ref/Alpha.dll", "binary\n");
		temp.CreateFile("packages/Beta.2.0.0/Beta.2.0.0.nupkg", "package\n");
		temp.CreateFile("packages/Beta.2.0.0/lib/Beta.dll", "binary\n");
	}

	private static IgnoreRules CreateArtifactRules(bool useSmartIgnore) => new(
		IgnoreHiddenFolders: false,
		IgnoreHiddenFiles: false,
		IgnoreDotFolders: false,
		IgnoreDotFiles: false,
		SmartIgnoredFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
		SmartIgnoredFiles: new HashSet<string>(StringComparer.OrdinalIgnoreCase))
	{
		UseSmartIgnore = useSmartIgnore,
		SmartArtifactIgnoreMatcher = useSmartIgnore
			? SmartArtifactIgnoreMatcher.Default
			: SmartArtifactIgnoreMatcher.Empty,
		SmartArtifactIgnoreCandidateMatcher = SmartArtifactIgnoreMatcher.Default
	};

	private static TreeBuildResult BuildTree(string rootPath, IgnoreRules rules)
	{
		return new TreeBuilder().Build(rootPath, new TreeFilterOptions(
			AllowedExtensions: new HashSet<string>(StringComparer.OrdinalIgnoreCase)
			{
				".cs",
				".cpp",
				".dll",
				".go",
				".json",
				".md",
				".nupkg",
				".py",
				".pyc",
				".mod",
				".tag",
				".ts",
				".config",
				".DotSettings",
				".user",
				string.Empty
			},
			AllowedRootFolders: new HashSet<string>(PathComparer.Default)
			{
				"bin",
				"build",
				"cache",
				"Library",
				"obj",
				"packages",
				"pkg",
				"src",
				"temp",
				"tmp",
				"vendor",
				"workspace"
			},
			IgnoreRules: rules), cancellationToken: TestContext.Current.CancellationToken);
	}

	private static void AssertPathVisible(TreeBuildResult tree, string relativePath)
	{
		Assert.True(ContainsPath(tree.Root, relativePath), $"Expected path '{relativePath}' to be visible.");
	}

	private static void AssertPathHidden(TreeBuildResult tree, string relativePath)
	{
		Assert.False(ContainsPath(tree.Root, relativePath), $"Expected path '{relativePath}' to be hidden.");
	}

	private static bool ContainsPath(FileSystemNode root, string relativePath)
	{
		var current = root;
		foreach (var segment in relativePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries))
		{
			var next = current.Children.FirstOrDefault(child =>
				string.Equals(child.Name, segment, StringComparison.OrdinalIgnoreCase));
			if (next is null)
				return false;

			current = next;
		}

		return true;
	}

}
