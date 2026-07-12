using DevProjex.Avalonia.Services;
using DevProjex.Tests.Shared.ProjectLoadWorkflow;

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

	[Fact]
	public async Task CommandLineAutomationRunner_SmartIgnoreExportHidesSignatureArtifactsWithoutMarkers()
	{
		using var temp = new TemporaryDirectory();
		SeedMixedArtifactWorkspace(temp);
		using var output = new StringWriter();
		using var error = new StringWriter();
		var parseResult = CommandLineOptions.Parse(
		[
			CommandLineOptionTokens.Path, temp.Path,
			CommandLineOptionTokens.Export, "tree",
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreSmartIgnore
		]);

		var exitCode = await CommandLineAutomationRunner.RunUtilityOrHeadlessAsync(
			parseResult,
			CreateContext(output, error),
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Equal(string.Empty, error.ToString());
		var stdout = output.ToString();
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
	public async Task CommandLineAutomationRunner_OtherIgnoreTogglesDoNotActivateSmartArtifacts()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("src/App.cs", "class App {}\n");
		temp.CreateFile("obj/project.assets.json", "{}\n");
		temp.CreateFile(".dot/payload.txt", "dot folder\n");
		using var output = new StringWriter();
		using var error = new StringWriter();
		var parseResult = CommandLineOptions.Parse(
		[
			CommandLineOptionTokens.Path, temp.Path,
			CommandLineOptionTokens.Export, "tree",
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreDotFolders,
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreEmptyFiles,
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreExtensionlessFiles
		]);

		var exitCode = await CommandLineAutomationRunner.RunUtilityOrHeadlessAsync(
			parseResult,
			CreateContext(output, error),
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Equal(string.Empty, error.ToString());
		var stdout = output.ToString();
		Assert.Contains("App.cs", stdout, StringComparison.Ordinal);
		Assert.Contains("project.assets.json", stdout, StringComparison.Ordinal);
		Assert.DoesNotContain(".dot", stdout, StringComparison.Ordinal);
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
				".py",
				".pyc",
				".mod",
				".tag",
				".ts",
				string.Empty
			},
			AllowedRootFolders: new HashSet<string>(PathComparer.Default)
			{
				"bin",
				"build",
				"cache",
				"Library",
				"obj",
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

	private static CommandLineAutomationContext CreateContext(TextWriter output, TextWriter error) =>
		new(
			Output: output,
			Error: error,
			ServicesFactory: options => AvaloniaCompositionRoot.CreateDefault(options),
			HelpContentProvider: new CommandLineHelpContentProvider(),
			VersionProvider: () => "test-version");
}
