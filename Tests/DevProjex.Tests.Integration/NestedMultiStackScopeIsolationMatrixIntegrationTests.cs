namespace DevProjex.Tests.Integration;

public sealed class NestedMultiStackScopeIsolationMatrixIntegrationTests
{
	public enum OpenMode
	{
		FromTempRoot = 0,
		DirectMonorepo = 1
	}

	public enum SelectionMode
	{
		All = 0,
		Backend = 1,
		Frontend = 2
	}

	public static IEnumerable<object[]> Cases()
	{
		foreach (OpenMode openMode in Enum.GetValues(typeof(OpenMode)))
		{
			foreach (SelectionMode selectionMode in Enum.GetValues(typeof(SelectionMode)))
			{
				foreach (var useGitIgnore in new[] { false, true })
				{
					foreach (var useSmartIgnore in new[] { false, true })
					{
						yield return [ openMode, selectionMode, useGitIgnore, useSmartIgnore ];
					}
				}
			}
		}
	}

	[Theory]
	[MemberData(nameof(Cases))]
	public void IgnorePipeline_NestedMixedWorkspace_PreservesIsolationAndPredictability(
		OpenMode openMode,
		SelectionMode selectionMode,
		bool useGitIgnore,
		bool useSmartIgnore)
	{
		using var temp = new TemporaryDirectory();
		SeedMonorepo(temp);

		var (openedRootPath, selectedRootFolders) = ResolveContext(temp.Path, openMode, selectionMode);
		var selectedOptions = BuildSelectedOptions(useGitIgnore, useSmartIgnore);

		var smartService = new SmartIgnoreService([
			new RustArtifactsIgnoreRule(),
			new FrontendArtifactsIgnoreRule(),
			new DotNetArtifactsIgnoreRule(),
			new PythonArtifactsIgnoreRule()
		]);
		var rulesService = new IgnoreRulesService(smartService);
		var rules = rulesService.Build(openedRootPath, selectedOptions, selectedRootFolders);

		var tree = new TreeBuilder().Build(openedRootPath, new TreeFilterOptions(
			AllowedExtensions: new HashSet<string>(StringComparer.OrdinalIgnoreCase)
			{
				".rs", ".ts", ".tsx", ".json", ".toml", ".cs", ".py", ".tmp", ".cache", ".txt", ".dll", ".pyc"
			},
			AllowedRootFolders: new HashSet<string>(selectedRootFolders, StringComparer.OrdinalIgnoreCase),
			IgnoreRules: rules), cancellationToken: TestContext.Current.CancellationToken);

		// Use effective runtime flags because availability can hide options for some selections.
		var hideStandardArtifacts = rules.UseGitIgnore || rules.UseSmartIgnore;

		AssertDirectoryState(tree.Root, selectedRootFolders, "rust-core", "target", shouldExist: !hideStandardArtifacts);
		AssertDirectoryState(tree.Root, selectedRootFolders, "src-tauri", "target", shouldExist: !hideStandardArtifacts);
		AssertDirectoryState(tree.Root, selectedRootFolders, "web-app", "node_modules", shouldExist: !hideStandardArtifacts);
		AssertDirectoryState(tree.Root, selectedRootFolders, "service-dotnet", "bin", shouldExist: !hideStandardArtifacts);
		AssertDirectoryState(tree.Root, selectedRootFolders, "python-worker", "__pycache__", shouldExist: !hideStandardArtifacts);

			// Directory can stay visible due to negated child entry (generated/keep.txt).
			AssertDirectoryState(tree.Root, selectedRootFolders, "web-app", "generated", shouldExist: true);
		// Same folder name in sibling project must remain unaffected.
		AssertDirectoryState(tree.Root, selectedRootFolders, "service-dotnet", "generated", shouldExist: true);

		// Root *.cache should hide hot.cache only when gitignore is enabled.
		AssertFileState(tree.Root, selectedRootFolders, "service-dotnet", "hot.cache", shouldExist: !rules.UseGitIgnore);
		// Child negation in web-app should keep this file visible even when gitignore is enabled.
		AssertFileState(tree.Root, selectedRootFolders, "web-app", "web.cache", shouldExist: true);
		// Root negation should keep global.cache visible.
			AssertFileExistsAnywhere(
				tree.Root,
				selectedRootFolders,
				"global.cache",
				shouldExist: IsGlobalCacheVisibleInSelectedContext(selectedRootFolders));

			// Nested child override in web-app/packages.
		AssertFileState(tree.Root, selectedRootFolders, "web-app", "important.tmp", shouldExist: true);
		AssertFileState(tree.Root, selectedRootFolders, "web-app", "reignored.tmp", shouldExist: !rules.UseGitIgnore);
	}

	private static IReadOnlyCollection<IgnoreOptionId> BuildSelectedOptions(bool useGitIgnore, bool useSmartIgnore)
	{
		var selected = new List<IgnoreOptionId>();
		if (useGitIgnore)
			selected.Add(IgnoreOptionId.UseGitIgnore);
		if (useSmartIgnore)
			selected.Add(IgnoreOptionId.SmartIgnore);
		return selected;
	}

	private static (string OpenedRootPath, IReadOnlyCollection<string> SelectedRootFolders) ResolveContext(
		string tempRoot,
		OpenMode openMode,
		SelectionMode selectionMode)
	{
		var selectedInMonorepo = selectionMode switch
		{
			SelectionMode.All => new[] { "rust-core", "src-tauri", "web-app", "service-dotnet", "python-worker" },
			SelectionMode.Backend => new[] { "rust-core", "src-tauri", "service-dotnet", "python-worker" },
			_ => new[] { "web-app" }
		};

		return openMode switch
		{
			OpenMode.FromTempRoot => (tempRoot, new[] { "monorepo" }),
			_ => (Path.Combine(tempRoot, "monorepo"), selectedInMonorepo)
		};
	}

	private static void AssertDirectoryState(
		FileSystemNode root,
		IReadOnlyCollection<string> selectedRootFolders,
		string projectName,
		string folderName,
		bool shouldExist)
	{
		var projectNode = FindProjectNode(root, selectedRootFolders, projectName);
		if (projectNode is null)
			return;

		var exists = projectNode.Children.Any(child => child.IsDirectory &&
			string.Equals(child.Name, folderName, StringComparison.OrdinalIgnoreCase));
		Assert.Equal(shouldExist, exists);
	}

	private static void AssertFileState(
		FileSystemNode root,
		IReadOnlyCollection<string> selectedRootFolders,
		string projectName,
		string fileName,
		bool shouldExist)
	{
		var projectNode = FindProjectNode(root, selectedRootFolders, projectName);
		if (projectNode is null)
			return;

		var exists = ContainsFileRecursively(projectNode, fileName);
		Assert.Equal(shouldExist, exists);
	}

	private static void AssertFileExistsAnywhere(
		FileSystemNode root,
		IReadOnlyCollection<string> selectedRootFolders,
		string fileName,
		bool shouldExist)
	{
		var contextRoot = FindSelectedContextRoot(root, selectedRootFolders);
		var exists = contextRoot is not null && ContainsFileRecursively(contextRoot, fileName);
		Assert.Equal(shouldExist, exists);
	}

		private static FileSystemNode? FindSelectedContextRoot(FileSystemNode root, IReadOnlyCollection<string> selectedRootFolders)
		{
		if (selectedRootFolders.Count == 1)
		{
			var rootFolder = selectedRootFolders.First();
			var match = root.Children.FirstOrDefault(child =>
				string.Equals(child.Name, rootFolder, StringComparison.OrdinalIgnoreCase));
			if (match is not null)
				return match;
		}

			return root;
		}

		private static bool IsGlobalCacheVisibleInSelectedContext(IReadOnlyCollection<string> selectedRootFolders)
		{
			if (selectedRootFolders.Count == 1)
			{
				var selected = selectedRootFolders.First();
				return string.Equals(selected, "monorepo", StringComparison.OrdinalIgnoreCase);
			}

			return true;
		}

		private static FileSystemNode? FindProjectNode(
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

	private static void SeedMonorepo(TemporaryDirectory temp)
	{
		temp.CreateFile("monorepo/.gitignore", string.Join('\n', new[]
		{
			"**/target/",
			"**/node_modules/",
			"**/bin/",
			"**/obj/",
			"**/__pycache__/",
			"*.cache",
			"!global.cache"
		}));

		temp.CreateFile("monorepo/web-app/.gitignore", string.Join('\n', new[]
		{
			"generated/*",
			"!generated/keep.txt",
			"*.cache",
			"!web.cache"
		}));

			temp.CreateFile("monorepo/web-app/packages/.gitignore", string.Join('\n', new[]
			{
				"*.tmp",
				"!important.tmp",
				"reignored.tmp"
			}));
			temp.CreateFile("monorepo/web-app/packages/package.json", "{}");

		temp.CreateFile("monorepo/rust-core/Cargo.toml", "[package]");
		temp.CreateFile("monorepo/rust-core/src/main.rs", "fn main() {}");
		temp.CreateFile("monorepo/rust-core/target/debug/app.dll", "binary");

		temp.CreateFile("monorepo/src-tauri/Cargo.toml", "[package]");
		temp.CreateFile("monorepo/src-tauri/src/main.rs", "fn main() {}");
		temp.CreateFile("monorepo/src-tauri/target/debug/deps/app.dll", "binary");

		temp.CreateFile("monorepo/web-app/package.json", "{}");
		temp.CreateFile("monorepo/web-app/src/main.tsx", "export const x = 1;");
		temp.CreateFile("monorepo/web-app/node_modules/lib/index.js", "module.exports = {};");
		temp.CreateFile("monorepo/web-app/generated/file.ts", "export const g = 1;");
		temp.CreateFile("monorepo/web-app/generated/keep.txt", "keep");
		temp.CreateFile("monorepo/web-app/src/hot.cache", "cache");
		temp.CreateFile("monorepo/web-app/src/web.cache", "cache");
			temp.CreateFile("monorepo/web-app/packages/important.tmp", "ok");
			temp.CreateFile("monorepo/web-app/packages/reignored.tmp", "blocked");

		temp.CreateFile("monorepo/service-dotnet/App.csproj", "<Project />");
		temp.CreateFile("monorepo/service-dotnet/Program.cs", "class Program {}");
		temp.CreateFile("monorepo/service-dotnet/bin/Release/app.dll", "binary");
		temp.CreateFile("monorepo/service-dotnet/obj/Release/cache.dll", "binary");
		temp.CreateFile("monorepo/service-dotnet/generated/info.txt", "visible");
		temp.CreateFile("monorepo/service-dotnet/src/hot.cache", "cache");

		temp.CreateFile("monorepo/python-worker/pyproject.toml", "[project]");
		temp.CreateFile("monorepo/python-worker/app.py", "print('ok')");
		temp.CreateFile("monorepo/python-worker/__pycache__/app.pyc", "binary");

		temp.CreateFile("monorepo/global.cache", "keep");
	}
}
