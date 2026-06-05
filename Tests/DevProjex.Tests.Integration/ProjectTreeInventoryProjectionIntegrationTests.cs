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
	public void TreeBuilder_ReadInventoryThenBuild_MatchesDirectBuild()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("src/app.cs", "class App {}");
		temp.CreateFile("src/.cache/generated.cs", "class Generated {}");
		temp.CreateFile("docs/readme.md", "# docs");
		temp.CreateFile("build/noise.tmp", "noise");
		var options = new TreeFilterOptions(
			AllowedExtensions: new HashSet<string>([".cs", ".md"], StringComparer.OrdinalIgnoreCase),
			AllowedRootFolders: new HashSet<string>(["src", "docs", "build"], PathComparer.Default),
			IgnoreRules: CreateRules(smartFolders: ["build"], useSmartIgnore: true) with
			{
				IgnoreDotFolders = true
			});
		var builder = new TreeBuilder();

		var direct = builder.Build(temp.Path, options, TestContext.Current.CancellationToken);
		var inventory = builder.ReadInventory(temp.Path, options, TestContext.Current.CancellationToken);
		var projected = builder.Build(inventory, options, TestContext.Current.CancellationToken);

		Assert.Equal(FlattenTree(direct.Root), FlattenTree(projected.Root));
		Assert.Equal(direct.RootAccessDenied, projected.RootAccessDenied);
		Assert.Equal(direct.HadAccessDenied, projected.HadAccessDenied);
		Assert.Contains(inventory.Entries, entry => entry.Name == "src");
		Assert.DoesNotContain(inventory.Entries, entry => entry.Name == "build");
	}

	[Fact]
	public void TreeBuilder_ReadInventoryThenBuild_MatchesDirectBuild_ForBroadRoot()
	{
		using var temp = new TemporaryDirectory();
		var rootNames = Enumerable.Range(0, 32)
			.Select(index => $"root-{index:D2}")
			.ToArray();
		foreach (var rootName in rootNames)
			temp.CreateFile($"{rootName}/src/App.cs", "class App {}");
		var options = new TreeFilterOptions(
			AllowedExtensions: new HashSet<string>([".cs"], StringComparer.OrdinalIgnoreCase),
			AllowedRootFolders: new HashSet<string>(rootNames, PathComparer.Default),
			IgnoreRules: CreateRules());
		var builder = new TreeBuilder();

		var direct = builder.Build(temp.Path, options, TestContext.Current.CancellationToken);
		var inventory = builder.ReadInventory(temp.Path, options, TestContext.Current.CancellationToken);
		var projected = builder.Build(inventory, options, TestContext.Current.CancellationToken);

		Assert.Equal(FlattenTree(direct.Root), FlattenTree(projected.Root));
		Assert.Equal(rootNames, projected.Root.Children.Select(child => child.Name).ToArray());
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

	private static List<string> FlattenTree(FileSystemNode root)
	{
		var paths = new List<string>();
		var pending = new Stack<FileSystemNode>();
		pending.Push(root);
		while (pending.Count > 0)
		{
			var node = pending.Pop();
			paths.Add($"{node.FullPath}|{node.IsDirectory}|{node.IsAccessDenied}");
			for (var i = node.Children.Count - 1; i >= 0; i--)
				pending.Push(node.Children[i]);
		}

		return paths;
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
