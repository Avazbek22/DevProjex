namespace DevProjex.Tests.Integration;

public sealed class ProjectWorkspaceScanSnapshotIntegrationTests
{
	[Fact]
	public void WorkspaceSnapshot_ProjectsSameTreeInventoryAsSeparateTreeScanner()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(".gitignore", "logs/\n*.tmp\n");
		temp.CreateFile("root-a.txt", "root");
		temp.CreateFile("root-z.tmp", "ignored by projection");
		temp.CreateFile("src/App.cs", "class App {}");
		temp.CreateFile("src/Models/User.cs", "class User {}");
		temp.CreateFile("docs/readme.md", "# docs");
		temp.CreateFile("bin/Debug/App.dll", "binary");
		temp.CreateFile(".idea/workspace.xml", "<project />");
		temp.CreateFile("logs/runtime.log", "log");
		temp.CreateFile("logs/keep.tmp", "tmp");

		var rules = CreateRules(temp.Path);
		var selectedRoots = new HashSet<string>(["src", "docs", "bin", ".idea", "logs"], PathComparer.Default);
		var options = new TreeFilterOptions(
			AllowedExtensions: new HashSet<string>([".cs", ".md", ".txt", ".tmp", ".log", ".xml"], StringComparer.OrdinalIgnoreCase),
			AllowedRootFolders: selectedRoots,
			IgnoreRules: rules);
		var builder = new TreeBuilder();
		var scanOptions = new ScanOptionsUseCase(new FileSystemScanner());

		var workspace = scanOptions.GetProjectWorkspaceSnapshotForRootFolders(
			temp.Path,
			selectedRoots,
			extensionDiscoveryRules: rules,
			effectiveRules: rules,
			effectiveExtensionPolicy: null,
			cancellationToken: TestContext.Current.CancellationToken);
		var separateInventory = builder.ReadInventory(temp.Path, options, TestContext.Current.CancellationToken);

		Assert.NotNull(workspace.Value.TreeInventory);
		Assert.Equal(
			FlattenInventory(separateInventory),
			FlattenInventory(workspace.Value.TreeInventory));

		var directTree = builder.Build(separateInventory, options, TestContext.Current.CancellationToken);
		var workspaceTree = builder.Build(workspace.Value.TreeInventory, options, TestContext.Current.CancellationToken);
		Assert.Equal(FlattenTree(directTree.Root), FlattenTree(workspaceTree.Root));
		Assert.Contains(workspaceTree.Root.Children, child => child.Name == "src");
		Assert.Contains(workspaceTree.Root.Children, child => child.Name == "docs");
		Assert.DoesNotContain(workspaceTree.Root.Children, child => child.Name == "bin");
		Assert.DoesNotContain(workspaceTree.Root.Children, child => child.Name == ".idea");
		Assert.DoesNotContain(workspaceTree.Root.Children, child => child.Name == "logs");
	}

	[Fact]
	public void WorkspaceSnapshot_WithNameFilter_KeepsProjectionEquivalentToDirectBuild()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("api/src/OrderHandler.cs", "class OrderHandler {}");
		temp.CreateFile("api/src/UserHandler.cs", "class UserHandler {}");
		temp.CreateFile("client/src/order-view.ts", "export {}");
		temp.CreateFile("client/src/user-view.ts", "export {}");
		temp.CreateFile("node_modules/pkg/order-noise.ts", "export {}");

		var rules = CreateRules(temp.Path);
		var selectedRoots = new HashSet<string>(["api", "client", "node_modules"], PathComparer.Default);
		var options = new TreeFilterOptions(
			AllowedExtensions: new HashSet<string>([".cs", ".ts"], StringComparer.OrdinalIgnoreCase),
			AllowedRootFolders: selectedRoots,
			IgnoreRules: rules,
			NameFilter: "order");
		var builder = new TreeBuilder();
		var workspace = new ScanOptionsUseCase(new FileSystemScanner()).GetProjectWorkspaceSnapshotForRootFolders(
			temp.Path,
			selectedRoots,
			extensionDiscoveryRules: rules,
			effectiveRules: rules,
			effectiveExtensionPolicy: null,
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.NotNull(workspace.Value.TreeInventory);
		var directTree = builder.Build(temp.Path, options, TestContext.Current.CancellationToken);
		var workspaceTree = builder.Build(workspace.Value.TreeInventory, options, TestContext.Current.CancellationToken);

		Assert.Equal(FlattenTree(directTree.Root), FlattenTree(workspaceTree.Root));
		Assert.DoesNotContain(FlattenTree(workspaceTree.Root), path => path.Contains("node_modules", StringComparison.OrdinalIgnoreCase));
		Assert.Contains(FlattenTree(workspaceTree.Root), path => path.Contains("OrderHandler.cs", StringComparison.OrdinalIgnoreCase));
		Assert.DoesNotContain(FlattenTree(workspaceTree.Root), path => path.Contains("UserHandler.cs", StringComparison.OrdinalIgnoreCase));
	}

	private static IgnoreRules CreateRules(string rootPath)
	{
		return new IgnoreRules(
			IgnoreHiddenFolders: false,
			IgnoreHiddenFiles: false,
			IgnoreDotFolders: true,
			IgnoreDotFiles: false,
			SmartIgnoredFolders: new HashSet<string>(["bin", "obj", "node_modules"], StringComparer.OrdinalIgnoreCase),
			SmartIgnoredFiles: new HashSet<string>(StringComparer.OrdinalIgnoreCase))
		{
			UseSmartIgnore = true,
			UseGitIgnore = true,
			GitIgnoreMatcher = GitIgnoreMatcher.Build(rootPath, File.Exists(Path.Combine(rootPath, ".gitignore"))
				? File.ReadLines(Path.Combine(rootPath, ".gitignore"))
				: []),
			SmartIgnoreScopeRoots = [rootPath]
		};
	}

	private static List<string> FlattenInventory(ProjectTreeInventorySnapshot snapshot)
	{
		var result = new List<string>(snapshot.Entries.Count);
		for (var index = 0; index < snapshot.Entries.Count; index++)
		{
			ref readonly var entry = ref snapshot.GetEntryRef(index);
			result.Add(string.Join(
				"|",
				index,
				entry.Name,
				entry.RelativePath,
				entry.ParentIndex,
				entry.IsDirectory,
				entry.IsHidden,
				entry.Length,
				entry.FirstChildIndex,
				entry.ChildCount,
				entry.IsAccessDenied));
		}

		return result;
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
			for (var index = node.Children.Count - 1; index >= 0; index--)
				pending.Push(node.Children[index]);
		}

		return paths;
	}
}
