namespace DevProjex.Tests.Integration;

public sealed class MultiStackIgnoreLeakMatrixIntegrationTests
{
	public enum OpenMode
	{
		OpenWorkspaceFromRoot = 0,
		OpenWorkspaceDirectly = 1
	}

	public enum RootSelectionMode
	{
		All = 0,
		BackendOnly = 1,
		FrontendOnly = 2
	}

	public static IEnumerable<object[]> Cases()
	{
		foreach (OpenMode openMode in Enum.GetValues(typeof(OpenMode)))
		{
			foreach (RootSelectionMode rootSelectionMode in Enum.GetValues(typeof(RootSelectionMode)))
			{
				foreach (var useGitIgnore in new[] { false, true })
				{
					foreach (var useSmartIgnore in new[] { false, true })
					{
						yield return [ openMode, rootSelectionMode, useGitIgnore, useSmartIgnore ];
					}
				}
			}
		}
	}

	[Theory]
	[MemberData(nameof(Cases))]
	public void IgnorePipeline_MixedStacks_PreventsArtifactLeaks(
		OpenMode openMode,
		RootSelectionMode rootSelectionMode,
		bool useGitIgnore,
		bool useSmartIgnore)
	{
		using var temp = new TemporaryDirectory();
		CreateWorkspace(temp);

		var (openedRootPath, selectedRootFolders) = ResolveOpenContext(temp.Path, openMode, rootSelectionMode);
		var selectedOptions = BuildOptions(useGitIgnore, useSmartIgnore);

		var smartService = new SmartIgnoreService([
			new DotNetArtifactsIgnoreRule(),
			new FrontendArtifactsIgnoreRule(),
			new PythonArtifactsIgnoreRule(),
			new RustArtifactsIgnoreRule()
		]);
		var rulesService = new IgnoreRulesService(smartService);
		var rules = rulesService.Build(openedRootPath, selectedOptions, selectedRootFolders);

		var expectedArtifactsHidden = useGitIgnore || useSmartIgnore;
		var treeBuilder = new TreeBuilder();
		var tree = treeBuilder.Build(openedRootPath, new TreeFilterOptions(
			AllowedExtensions: new HashSet<string>(StringComparer.OrdinalIgnoreCase)
			{
				".rs", ".ts", ".tsx", ".json", ".toml", ".cs", ".py", ".js", ".dll", ".pyc", ".md"
			},
			AllowedRootFolders: new HashSet<string>(selectedRootFolders, StringComparer.OrdinalIgnoreCase),
			IgnoreRules: rules), cancellationToken: TestContext.Current.CancellationToken);

		var scanner = new FileSystemScanner();
		var extensionScan = new ScanOptionsUseCase(scanner).GetExtensionsForRootFolders(
			openedRootPath,
			selectedRootFolders,
			rules, cancellationToken: TestContext.Current.CancellationToken);
		var scannedExtensions = extensionScan.Value;

		AssertProjectState(
			tree.Root,
			selectedRootFolders,
			rootName: "sendme",
			sourceFileName: "main.rs",
			artifactFolderName: "target",
			expectedArtifactsHidden);

		AssertProjectState(
			tree.Root,
			selectedRootFolders,
			rootName: "src-tauri",
			sourceFileName: "main.rs",
			artifactFolderName: "target",
			expectedArtifactsHidden);

		AssertProjectState(
			tree.Root,
			selectedRootFolders,
			rootName: "web-app",
			sourceFileName: "main.tsx",
			artifactFolderName: "node_modules",
			expectedArtifactsHidden);
		AssertProjectState(
			tree.Root,
			selectedRootFolders,
			rootName: "web-app",
			sourceFileName: "main.tsx",
			artifactFolderName: "dist",
			expectedArtifactsHidden);

		AssertProjectState(
			tree.Root,
			selectedRootFolders,
			rootName: "service-dotnet",
			sourceFileName: "Program.cs",
			artifactFolderName: "bin",
			expectedArtifactsHidden);
		AssertProjectState(
			tree.Root,
			selectedRootFolders,
			rootName: "service-dotnet",
			sourceFileName: "Program.cs",
			artifactFolderName: "obj",
			expectedArtifactsHidden);

		AssertProjectState(
			tree.Root,
			selectedRootFolders,
			rootName: "python-worker",
			sourceFileName: "app.py",
			artifactFolderName: "__pycache__",
			expectedArtifactsHidden);
		AssertProjectState(
			tree.Root,
			selectedRootFolders,
			rootName: "python-worker",
			sourceFileName: "app.py",
			artifactFolderName: ".venv",
			expectedArtifactsHidden);

		if (expectedArtifactsHidden)
		{
			Assert.DoesNotContain(".dll", scannedExtensions);
			Assert.DoesNotContain(".pyc", scannedExtensions);
		}
	}

	private static void AssertProjectState(
		FileSystemNode root,
		IReadOnlyCollection<string> selectedRootFolders,
		string rootName,
		string sourceFileName,
		string artifactFolderName,
		bool expectedArtifactsHidden)
	{
		var projectNode = TryFindSelectedProjectNode(root, selectedRootFolders, rootName);
		if (projectNode is null)
			return;

		// Source files must always stay visible.
		Assert.True(ContainsFileRecursively(projectNode, sourceFileName), $"Expected source file {sourceFileName} to stay visible.");

		var hasArtifactFolder = projectNode.Children.Any(child => child.Name == artifactFolderName);
		Assert.Equal(!expectedArtifactsHidden, hasArtifactFolder);
	}

	private static FileSystemNode? TryFindSelectedProjectNode(
		FileSystemNode root,
		IReadOnlyCollection<string> selectedRootFolders,
		string projectName)
	{
		foreach (var top in root.Children)
		{
			if (string.Equals(top.Name, projectName, StringComparison.OrdinalIgnoreCase))
				return top;

			if (!selectedRootFolders.Contains(top.Name, StringComparer.OrdinalIgnoreCase))
				continue;

			var nested = top.Children.FirstOrDefault(child =>
				string.Equals(child.Name, projectName, StringComparison.OrdinalIgnoreCase));
			if (nested is not null)
				return nested;
		}

		return null;
	}

	private static bool ContainsFileRecursively(FileSystemNode node, string fileName)
	{
		foreach (var child in node.Children)
		{
			if (!child.IsDirectory && string.Equals(child.Name, fileName, StringComparison.OrdinalIgnoreCase))
				return true;

			if (child.IsDirectory && ContainsFileRecursively(child, fileName))
				return true;
		}

		return false;
	}

	private static IReadOnlyCollection<IgnoreOptionId> BuildOptions(bool useGitIgnore, bool useSmartIgnore)
	{
		var selected = new List<IgnoreOptionId>();
		if (useGitIgnore)
			selected.Add(IgnoreOptionId.UseGitIgnore);
		if (useSmartIgnore)
			selected.Add(IgnoreOptionId.SmartIgnore);
		return selected;
	}

	private static (string OpenedRootPath, IReadOnlyCollection<string> SelectedRootFolders) ResolveOpenContext(
		string tempRoot,
		OpenMode openMode,
		RootSelectionMode rootSelectionMode)
	{
		var roots = rootSelectionMode switch
		{
			RootSelectionMode.All => new[] { "sendme", "src-tauri", "web-app", "service-dotnet", "python-worker" },
			RootSelectionMode.BackendOnly => new[] { "sendme", "src-tauri", "service-dotnet", "python-worker" },
			_ => new[] { "web-app" }
		};

		return openMode switch
		{
			OpenMode.OpenWorkspaceFromRoot => (tempRoot, new[] { "workspace" }),
			_ => (Path.Combine(tempRoot, "workspace"), roots)
		};
	}

	private static void CreateWorkspace(TemporaryDirectory temp)
	{
		temp.CreateFile("workspace/.gitignore", string.Join('\n', new[]
		{
			"**/target/",
			"**/node_modules/",
			"**/dist/",
			"**/bin/",
			"**/obj/",
			"**/__pycache__/",
			"**/.venv/"
		}));

		temp.CreateFile("workspace/sendme/Cargo.toml", "[package]");
		temp.CreateFile("workspace/sendme/src/main.rs", "fn main() {}");
		temp.CreateFile("workspace/sendme/target/debug/app.dll", "binary");

		temp.CreateFile("workspace/src-tauri/Cargo.toml", "[package]");
		temp.CreateFile("workspace/src-tauri/src/main.rs", "fn main() {}");
		temp.CreateFile("workspace/src-tauri/target/debug/deps/mod.dll", "binary");

		temp.CreateFile("workspace/web-app/package.json", "{}");
		temp.CreateFile("workspace/web-app/src/main.tsx", "export const app = 1;");
		temp.CreateFile("workspace/web-app/node_modules/lib/index.js", "module.exports = {};");
		temp.CreateFile("workspace/web-app/dist/app.js", "console.log('dist');");

		temp.CreateFile("workspace/service-dotnet/App.csproj", "<Project />");
		temp.CreateFile("workspace/service-dotnet/Program.cs", "class Program {}");
		temp.CreateFile("workspace/service-dotnet/bin/Release/app.dll", "binary");
		temp.CreateFile("workspace/service-dotnet/obj/Release/cache.dll", "binary");

		temp.CreateFile("workspace/python-worker/pyproject.toml", "[project]");
		temp.CreateFile("workspace/python-worker/app.py", "print('ok')");
		temp.CreateFile("workspace/python-worker/__pycache__/app.pyc", "binary");
		temp.CreateFile("workspace/python-worker/.venv/bin/python", "binary");
	}
}
