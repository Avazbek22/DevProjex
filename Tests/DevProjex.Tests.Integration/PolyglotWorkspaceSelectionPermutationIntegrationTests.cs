namespace DevProjex.Tests.Integration;

public sealed class PolyglotWorkspaceSelectionPermutationIntegrationTests
{
	public static IEnumerable<object[]> Cases()
	{
		var selectionModes = new[]
		{
			new[] { "rust-core" },
			new[] { "web-app" },
			new[] { "service-dotnet" },
			new[] { "python-worker" },
			new[] { "go-tool" },
			new[] { "rust-core", "web-app", "service-dotnet" },
			new[] { "web-app", "python-worker", "go-tool" },
			new[] { "rust-core", "web-app", "service-dotnet", "python-worker", "go-tool", "monolith" }
		};

		foreach (var selectedRoots in selectionModes)
		{
			foreach (var useGitIgnore in new[] { false, true })
			{
				foreach (var useSmartIgnore in new[] { false, true })
				{
					yield return [ selectedRoots, useGitIgnore, useSmartIgnore ];
				}
			}
		}
	}

	[Theory]
	[MemberData(nameof(Cases))]
	public void IgnorePipeline_PolyglotWorkspaceSelection_MatrixIsStable(
		string[] selectedRoots,
		bool useGitIgnore,
		bool useSmartIgnore)
	{
		using var temp = new TemporaryDirectory();
		SeedWorkspace(temp);

		var smartService = new SmartIgnoreService([
			new FixedSmartIgnoreRule(
				folders: ["target", "node_modules", "bin", "obj", "__pycache__", ".venv", "dist", "coverage"],
				files: [".DS_Store", "Thumbs.db"])
		]);
		var rulesService = new IgnoreRulesService(smartService);
		var selectedOptions = BuildOptions(useGitIgnore, useSmartIgnore);
		var openedRoot = Path.Combine(temp.Path, "workspace");
		var rules = rulesService.Build(openedRoot, selectedOptions, selectedRoots);

		var tree = new TreeBuilder().Build(openedRoot, new TreeFilterOptions(
			AllowedExtensions: new HashSet<string>(StringComparer.OrdinalIgnoreCase)
			{
				".rs", ".tsx", ".ts", ".cs", ".py", ".go", ".json", ".toml", ".dll", ".pyc", ".js", ".cache"
			},
			AllowedRootFolders: new HashSet<string>(selectedRoots, StringComparer.OrdinalIgnoreCase),
			IgnoreRules: rules), cancellationToken: TestContext.Current.CancellationToken);

		AssertOnlySelectedRootsArePresent(tree.Root, selectedRoots);
		AssertSourceFileVisibility(tree.Root, selectedRoots);

		var hideStandardArtifacts = rules.UseGitIgnore || rules.UseSmartIgnore;
		AssertDirectoryState(tree.Root, selectedRoots, "rust-core", "target", shouldExist: !hideStandardArtifacts);
		AssertDirectoryState(tree.Root, selectedRoots, "web-app", "node_modules", shouldExist: !hideStandardArtifacts);
		AssertDirectoryState(tree.Root, selectedRoots, "web-app", "coverage", shouldExist: !hideStandardArtifacts);
		AssertDirectoryState(tree.Root, selectedRoots, "service-dotnet", "bin", shouldExist: !hideStandardArtifacts);
		AssertDirectoryState(tree.Root, selectedRoots, "service-dotnet", "obj", shouldExist: !hideStandardArtifacts);
		AssertDirectoryState(tree.Root, selectedRoots, "python-worker", "__pycache__", shouldExist: !hideStandardArtifacts);
		AssertDirectoryState(tree.Root, selectedRoots, "python-worker", ".venv", shouldExist: !hideStandardArtifacts);
		AssertDirectoryState(tree.Root, selectedRoots, "go-tool", "bin", shouldExist: !hideStandardArtifacts);
		AssertDirectoryState(tree.Root, selectedRoots, "monolith", "dist", shouldExist: !hideStandardArtifacts);

		// web-app generated is controlled by scoped gitignore only.
		AssertDirectoryState(tree.Root, selectedRoots, "web-app", "generated", shouldExist: !rules.UseGitIgnore);
		AssertFileState(tree.Root, selectedRoots, "web-app", "keep.cache", shouldExist: true);
		AssertFileState(tree.Root, selectedRoots, "service-dotnet", "hot.cache", shouldExist: !rules.UseGitIgnore);

		var extensions = new ScanOptionsUseCase(new FileSystemScanner())
			.GetExtensionsForRootFolders(openedRoot, selectedRoots, rules, cancellationToken: TestContext.Current.CancellationToken)
			.Value;
		if (hideStandardArtifacts)
		{
			Assert.DoesNotContain(".dll", extensions);
			Assert.DoesNotContain(".pyc", extensions);
		}
	}

	private static IReadOnlyCollection<IgnoreOptionId> BuildOptions(bool useGitIgnore, bool useSmartIgnore)
	{
		var selected = new List<IgnoreOptionId>(capacity: 2);
		if (useGitIgnore)
			selected.Add(IgnoreOptionId.UseGitIgnore);
		if (useSmartIgnore)
			selected.Add(IgnoreOptionId.SmartIgnore);
		return selected;
	}

	private static void AssertOnlySelectedRootsArePresent(FileSystemNode root, IReadOnlyCollection<string> selectedRoots)
	{
		var actual = root.Children.Select(node => node.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
		foreach (var selected in selectedRoots)
			Assert.Contains(selected, actual);

		foreach (var node in actual)
			Assert.Contains(node, selectedRoots, StringComparer.OrdinalIgnoreCase);
	}

	private static void AssertSourceFileVisibility(FileSystemNode root, IReadOnlyCollection<string> selectedRoots)
	{
		AssertFileState(root, selectedRoots, "rust-core", "main.rs", shouldExist: true);
		AssertFileState(root, selectedRoots, "web-app", "main.tsx", shouldExist: true);
		AssertFileState(root, selectedRoots, "service-dotnet", "Program.cs", shouldExist: true);
		AssertFileState(root, selectedRoots, "python-worker", "app.py", shouldExist: true);
		AssertFileState(root, selectedRoots, "go-tool", "main.go", shouldExist: true);
		AssertFileState(root, selectedRoots, "monolith", "index.ts", shouldExist: true);
	}

	private static void AssertDirectoryState(
		FileSystemNode root,
		IReadOnlyCollection<string> selectedRoots,
		string projectName,
		string directoryName,
		bool shouldExist)
	{
		var node = FindProjectNode(root, selectedRoots, projectName);
		if (node is null)
			return;

		var exists = node.Children.Any(child =>
			child.IsDirectory &&
			string.Equals(child.Name, directoryName, StringComparison.OrdinalIgnoreCase));
		Assert.Equal(shouldExist, exists);
	}

	private static void AssertFileState(
		FileSystemNode root,
		IReadOnlyCollection<string> selectedRoots,
		string projectName,
		string fileName,
		bool shouldExist)
	{
		var node = FindProjectNode(root, selectedRoots, projectName);
		if (node is null)
			return;

		var exists = ContainsFileRecursively(node, fileName);
		Assert.Equal(shouldExist, exists);
	}

	private static FileSystemNode? FindProjectNode(
		FileSystemNode root,
		IReadOnlyCollection<string> selectedRoots,
		string projectName)
	{
		foreach (var child in root.Children)
		{
			if (!selectedRoots.Contains(child.Name, StringComparer.OrdinalIgnoreCase))
				continue;

			if (string.Equals(child.Name, projectName, StringComparison.OrdinalIgnoreCase))
				return child;
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

	private static void SeedWorkspace(TemporaryDirectory temp)
	{
		temp.CreateFile("workspace/.gitignore", string.Join('\n', new[]
		{
			"**/target/",
			"**/node_modules/",
			"**/coverage/",
			"**/bin/",
			"**/obj/",
			"**/__pycache__/",
			"**/.venv/",
			"**/dist/",
			"*.cache"
		}));

		temp.CreateFile("workspace/rust-core/.gitignore", "target/");
		temp.CreateFile("workspace/rust-core/Cargo.toml", "[package]");
		temp.CreateFile("workspace/rust-core/src/main.rs", "fn main() {}");
		temp.CreateFile("workspace/rust-core/target/debug/app.dll", "binary");

		temp.CreateFile("workspace/web-app/.gitignore", string.Join('\n', new[]
		{
			"generated/",
			"!src/keep.cache"
		}));
		temp.CreateFile("workspace/web-app/package.json", "{}");
		temp.CreateFile("workspace/web-app/src/main.tsx", "export const app = 1;");
		temp.CreateFile("workspace/web-app/src/keep.cache", "keep");
		temp.CreateFile("workspace/web-app/generated/file.ts", "blocked");
		temp.CreateFile("workspace/web-app/node_modules/pkg/index.js", "blocked");
		temp.CreateFile("workspace/web-app/coverage/report.json", "blocked");

		temp.CreateFile("workspace/service-dotnet/App.csproj", "<Project />");
		temp.CreateFile("workspace/service-dotnet/src/Program.cs", "class Program {}");
		temp.CreateFile("workspace/service-dotnet/src/hot.cache", "blocked");
		temp.CreateFile("workspace/service-dotnet/bin/Release/app.dll", "blocked");
		temp.CreateFile("workspace/service-dotnet/obj/Release/cache.dll", "blocked");

		temp.CreateFile("workspace/python-worker/pyproject.toml", "[project]");
		temp.CreateFile("workspace/python-worker/app.py", "print('ok')");
		temp.CreateFile("workspace/python-worker/__pycache__/app.pyc", "blocked");
		temp.CreateFile("workspace/python-worker/.venv/bin/python", "blocked");

		temp.CreateFile("workspace/go-tool/go.mod", "module sample");
		temp.CreateFile("workspace/go-tool/cmd/main.go", "package main");
		temp.CreateFile("workspace/go-tool/bin/tool.exe", "blocked");

		temp.CreateFile("workspace/monolith/index.ts", "export {}");
		temp.CreateFile("workspace/monolith/dist/main.js", "blocked");
	}

	private sealed class FixedSmartIgnoreRule(IReadOnlyCollection<string> folders, IReadOnlyCollection<string> files)
		: ISmartIgnoreRule
	{
		public SmartIgnoreResult Evaluate(string rootPath)
		{
			return new SmartIgnoreResult(
				new HashSet<string>(folders, StringComparer.OrdinalIgnoreCase),
				new HashSet<string>(files, StringComparer.OrdinalIgnoreCase));
		}
	}
}
