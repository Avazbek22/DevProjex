namespace DevProjex.Tests.Integration;

public sealed class ProjectTreeInventoryProjectionIntegrationTests
{
	[Fact]
	public void TreeBuilder_AllIgnoreOptionsOff_ProjectsEveryRegularEntryFromInventory()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("lab2/.idea/workspace.xml", "<project />");
		temp.CreateFile("lab2/__pycache__/main.cpython-312.pyc", "binary");
		temp.CreateFile("lab2/main.py", "print('ok')");
		temp.CreateFile("lab2/README", "extensionless");

		var tree = BuildTree(
			temp.Path,
			CreateRules(),
			allowedExtensions: [".xml", ".pyc", ".py"],
			allowedRoots: ["lab2"]);

		var lab = tree.Root.Children.Single(node => node.Name == "lab2");
		Assert.Contains(lab.Children, node => node.Name == ".idea");
		Assert.Contains(lab.Children, node => node.Name == "__pycache__");
		Assert.Contains(lab.Children, node => node.Name == "README");
	}

	[Fact]
	public void TreeBuilder_SmartIgnore_PrunesArtifactSubtreeBeforeVisibleProjection()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("src/app.ts", "export const x = 1;");
		temp.CreateFile("node_modules/pkg/index.js", "module.exports = {};");
		temp.CreateFile("node_modules/pkg/nested/generated.ts", "export const y = 2;");

		var tree = BuildTree(
			temp.Path,
			CreateRules(smartFolders: ["node_modules"], useSmartIgnore: true),
			allowedExtensions: [".ts", ".js"],
			allowedRoots: ["src", "node_modules"]);

		Assert.Contains(tree.Root.Children, node => node.Name == "src");
		Assert.DoesNotContain(tree.Root.Children, node => node.Name == "node_modules");
	}

	[Fact]
	public void TreeBuilder_GitIgnoreNegation_TraversesIgnoredDirectoryAndKeepsUnignoredChild()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(".gitignore", "logs/*\n!logs/keep.log\n");
		temp.CreateFile("logs/drop.log", "ignored");
		temp.CreateFile("logs/keep.log", "visible");

		var rules = CreateRules(useGitIgnore: true) with
		{
			GitIgnoreMatcher = GitIgnoreMatcher.Build(temp.Path, File.ReadLines(Path.Combine(temp.Path, ".gitignore")))
		};

		var tree = BuildTree(
			temp.Path,
			rules,
			allowedExtensions: [".log"],
			allowedRoots: ["logs"]);

		var logs = tree.Root.Children.Single(node => node.Name == "logs");
		Assert.Contains(logs.Children, node => node.Name == "keep.log");
		Assert.DoesNotContain(logs.Children, node => node.Name == "drop.log");
	}

	[Fact]
	public void TreeInventoryScanner_PrunesDirectoriesBeforeReadingTheirChildren()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("src/app.cs", "class App {}");
		temp.CreateFile("build/generated/deep/noise.cs", "class Noise {}");

		var snapshot = ProjectTreeInventoryScanner.Read(
			temp.Path,
			(entry, _) => !string.Equals(entry.Name, "build", StringComparison.OrdinalIgnoreCase),
			TestContext.Current.CancellationToken);

		Assert.Contains(snapshot.Entries, entry => entry.Name == "src");
		Assert.DoesNotContain(snapshot.Entries, entry => entry.Name == "build");
		Assert.DoesNotContain(snapshot.Entries, entry => entry.Name == "noise.cs");
	}

	private static TreeBuildResult BuildTree(
		string rootPath,
		IgnoreRules rules,
		IReadOnlyCollection<string> allowedExtensions,
		IReadOnlyCollection<string> allowedRoots,
		string? nameFilter = null)
	{
		return new TreeBuilder().Build(
			rootPath,
			new TreeFilterOptions(
				AllowedExtensions: new HashSet<string>(allowedExtensions, StringComparer.OrdinalIgnoreCase),
				AllowedRootFolders: new HashSet<string>(allowedRoots, PathComparer.Default),
				IgnoreRules: rules,
				NameFilter: nameFilter),
			TestContext.Current.CancellationToken);
	}

	private static IgnoreRules CreateRules(
		IReadOnlyCollection<string>? smartFolders = null,
		bool useSmartIgnore = false,
		bool useGitIgnore = false)
	{
		return new IgnoreRules(
			IgnoreHiddenFolders: false,
			IgnoreHiddenFiles: false,
			IgnoreDotFolders: false,
			IgnoreDotFiles: false,
			SmartIgnoredFolders: new HashSet<string>(smartFolders ?? [], StringComparer.OrdinalIgnoreCase),
			SmartIgnoredFiles: new HashSet<string>(StringComparer.OrdinalIgnoreCase))
		{
			UseSmartIgnore = useSmartIgnore,
			UseGitIgnore = useGitIgnore
		};
	}
}
