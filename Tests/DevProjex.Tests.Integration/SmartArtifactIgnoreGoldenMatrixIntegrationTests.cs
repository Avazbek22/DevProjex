using static DevProjex.Tests.Shared.ProjectLoadWorkflow.ProjectLoadWorkflowRefreshHarness;

namespace DevProjex.Tests.Integration;

public sealed class SmartArtifactIgnoreGoldenMatrixIntegrationTests
{
	private static readonly string[] AllWorkspaceFiles =
	[
		".env",
		".gitignore",
		".local/local.txt",
		".m2/repository/acme/module.pom",
		"LICENSE",
		"artifact-store/packages/Alpha/Alpha.nupkg",
		"artifact-store/packages/Beta/Beta.nupkg",
		"artifact-store/packages/repositories.config",
		"emptied-by-file/zero.bin",
		"empty/zero.dat",
		"git-owned/secret.log",
		"keep/README.md",
		"packages/domain/Order.cs",
		"project/App.csproj",
		"project/App.csproj.user",
		"project/obj/project.assets.json",
		"src/App.cs"
	];

	[Theory]
	[MemberData(nameof(IgnoreGoldenCases))]
	public void TreeBuilder_IgnoreCombinationMatchesExactGoldenFileSet(IgnoreGoldenCase testCase)
	{
		using var temp = new TemporaryDirectory();
		SeedGoldenWorkspace(temp);
		var rules = CreateRules(temp.Path, testCase.EnabledOptions);
		var options = CreateTreeOptions(rules);
		var treeBuilder = new TreeBuilder();

		var first = treeBuilder.Build(temp.Path, options, TestContext.Current.CancellationToken);
		var second = treeBuilder.Build(temp.Path, options, TestContext.Current.CancellationToken);
		var expectedFiles = AllWorkspaceFiles
			.Except(testCase.HiddenFiles, StringComparer.Ordinal)
			.Order(StringComparer.Ordinal)
			.ToArray();

		Assert.Equal(expectedFiles, CollectRelativeFiles(first.Root, temp.Path));
		Assert.Equal(expectedFiles, CollectRelativeFiles(second.Root, temp.Path));
		Assert.Equal(testCase.EmptyFolderVisible, ContainsPath(first.Root, "empty-folder"));
		Assert.Equal(testCase.EmptiedByFileFolderVisible, ContainsPath(first.Root, "emptied-by-file"));
		Assert.False(first.RootAccessDenied);
		Assert.False(first.HadAccessDenied);
	}

	[Fact]
	public void SelectionRefresh_SmartToggleCycleConvergesWithoutLosingControllerOrExtensionState()
	{
		using var temp = new TemporaryDirectory();
		SeedControllerWorkspace(temp);
		var services = CreateServices();

		var enabled = services.Engine.ComputeFullRefreshSnapshot(
			CreateDefaultContext(temp.Path),
			TestContext.Current.CancellationToken);
		var disabled = services.Engine.ComputeFullRefreshSnapshot(
			CreateSmartToggleContext(temp.Path, enabled, isEnabled: false),
			TestContext.Current.CancellationToken);
		var reEnabled = services.Engine.ComputeFullRefreshSnapshot(
			CreateSmartToggleContext(temp.Path, disabled, isEnabled: true),
			TestContext.Current.CancellationToken);
		var disabledAgain = services.Engine.ComputeFullRefreshSnapshot(
			CreateSmartToggleContext(temp.Path, reEnabled, isEnabled: false),
			TestContext.Current.CancellationToken);

		AssertSmartState(enabled, expectedChecked: true, expectedArtifactExtensions: false);
		AssertSmartState(disabled, expectedChecked: false, expectedArtifactExtensions: true);
		AssertSmartState(reEnabled, expectedChecked: true, expectedArtifactExtensions: false);
		AssertSmartState(disabledAgain, expectedChecked: false, expectedArtifactExtensions: true);
		AssertEquivalentVisibleSnapshots(enabled, reEnabled);
		AssertEquivalentVisibleSnapshots(disabled, disabledAgain);
	}

	public static TheoryData<IgnoreGoldenCase> IgnoreGoldenCases() => new()
	{
		new IgnoreGoldenCase(
			"all ignore options off",
			[],
			[],
			EmptyFolderVisible: true,
			EmptiedByFileFolderVisible: true),
		new IgnoreGoldenCase(
			"smart only",
			[IgnoreOptionId.SmartIgnore],
			[
				".m2/repository/acme/module.pom",
				"artifact-store/packages/Alpha/Alpha.nupkg",
				"artifact-store/packages/Beta/Beta.nupkg",
				"artifact-store/packages/repositories.config",
				"project/App.csproj.user",
				"project/obj/project.assets.json"
			],
			EmptyFolderVisible: true,
			EmptiedByFileFolderVisible: true),
		new IgnoreGoldenCase(
			"gitignore only",
			[IgnoreOptionId.UseGitIgnore],
			[
				"artifact-store/packages/Alpha/Alpha.nupkg",
				"artifact-store/packages/Beta/Beta.nupkg",
				"artifact-store/packages/repositories.config",
				"git-owned/secret.log",
				"project/obj/project.assets.json"
			],
			EmptyFolderVisible: true,
			EmptiedByFileFolderVisible: true),
		new IgnoreGoldenCase(
			"dot folders and files",
			[IgnoreOptionId.DotFolders, IgnoreOptionId.DotFiles],
			[
				".env",
				".gitignore",
				".local/local.txt",
				".m2/repository/acme/module.pom"
			],
			EmptyFolderVisible: true,
			EmptiedByFileFolderVisible: true),
		new IgnoreGoldenCase(
			"empty files only",
			[IgnoreOptionId.EmptyFiles],
			["empty/zero.dat", "emptied-by-file/zero.bin"],
			EmptyFolderVisible: true,
			EmptiedByFileFolderVisible: true),
		new IgnoreGoldenCase(
			"empty folders only",
			[IgnoreOptionId.EmptyFolders],
			[],
			EmptyFolderVisible: false,
			EmptiedByFileFolderVisible: true),
		new IgnoreGoldenCase(
			"empty files and folders",
			[IgnoreOptionId.EmptyFiles, IgnoreOptionId.EmptyFolders],
			["empty/zero.dat", "emptied-by-file/zero.bin"],
			EmptyFolderVisible: false,
			EmptiedByFileFolderVisible: false),
		new IgnoreGoldenCase(
			"extensionless only",
			[IgnoreOptionId.ExtensionlessFiles],
			["LICENSE"],
			EmptyFolderVisible: true,
			EmptiedByFileFolderVisible: true),
		new IgnoreGoldenCase(
			"gitignore and smart overlap",
			[IgnoreOptionId.UseGitIgnore, IgnoreOptionId.SmartIgnore],
			[
				".m2/repository/acme/module.pom",
				"artifact-store/packages/Alpha/Alpha.nupkg",
				"artifact-store/packages/Beta/Beta.nupkg",
				"artifact-store/packages/repositories.config",
				"git-owned/secret.log",
				"project/App.csproj.user",
				"project/obj/project.assets.json"
			],
			EmptyFolderVisible: true,
			EmptiedByFileFolderVisible: true),
		new IgnoreGoldenCase(
			"all portable dynamic options",
			[
				IgnoreOptionId.UseGitIgnore,
				IgnoreOptionId.SmartIgnore,
				IgnoreOptionId.DotFolders,
				IgnoreOptionId.DotFiles,
				IgnoreOptionId.EmptyFolders,
				IgnoreOptionId.EmptyFiles,
				IgnoreOptionId.ExtensionlessFiles
			],
			[
				".env",
				".gitignore",
				".local/local.txt",
				".m2/repository/acme/module.pom",
				"LICENSE",
				"artifact-store/packages/Alpha/Alpha.nupkg",
				"artifact-store/packages/Beta/Beta.nupkg",
				"artifact-store/packages/repositories.config",
				"empty/zero.dat",
				"emptied-by-file/zero.bin",
				"git-owned/secret.log",
				"project/App.csproj.user",
				"project/obj/project.assets.json"
			],
			EmptyFolderVisible: false,
			EmptiedByFileFolderVisible: false)
	};

	private static void SeedGoldenWorkspace(TemporaryDirectory temp)
	{
		temp.CreateFile(".gitignore", "artifact-store/packages/\nproject/obj/\ngit-owned/\n");
		temp.CreateFile(".env", "ENV=value\n");
		temp.CreateFile("LICENSE", "license\n");
		temp.CreateFile("src/App.cs", "class App {}\n");
		temp.CreateFile("packages/domain/Order.cs", "class Order {}\n");
		temp.CreateFile("artifact-store/packages/repositories.config", "<repositories />\n");
		temp.CreateFile("artifact-store/packages/Alpha/Alpha.nupkg", "package\n");
		temp.CreateDirectory("artifact-store/packages/Alpha/lib");
		temp.CreateFile("artifact-store/packages/Beta/Beta.nupkg", "package\n");
		temp.CreateDirectory("artifact-store/packages/Beta/ref");
		temp.CreateFile(".m2/repository/acme/module.pom", "<project />\n");
		temp.CreateFile("project/App.csproj", "<Project />\n");
		temp.CreateFile("project/App.csproj.user", "local state\n");
		temp.CreateFile("project/obj/project.assets.json", "{}\n");
		temp.CreateFile("git-owned/secret.log", "secret\n");
		temp.CreateFile(".local/local.txt", "local\n");
		temp.CreateFile("empty/zero.dat", string.Empty);
		temp.CreateFile("emptied-by-file/zero.bin", string.Empty);
		temp.CreateDirectory("empty-folder");
		temp.CreateFile("keep/README.md", "keep\n");
	}

	private static void SeedControllerWorkspace(TemporaryDirectory temp)
	{
		temp.CreateFile("App.csproj", "<Project />\n");
		temp.CreateFile("src/App.cs", "class App {}\n");
		temp.CreateFile("App.csproj.user", "local state\n");
		temp.CreateFile("packages/repositories.config", "<repositories />\n");
		temp.CreateFile("packages/Alpha/Alpha.nupkg", "package\n");
		temp.CreateDirectory("packages/Alpha/lib");
		temp.CreateFile("packages/Beta/Beta.nupkg", "package\n");
		temp.CreateDirectory("packages/Beta/ref");
	}

	private static IgnoreRules CreateRules(
		string rootPath,
		IEnumerable<IgnoreOptionId> enabledOptions)
	{
		var enabled = enabledOptions.ToHashSet();
		var gitIgnoreMatcher = GitIgnoreMatcher.Build(
			rootPath,
			["artifact-store/packages/", "project/obj/", "git-owned/"]);
		var emptyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var useSmartIgnore = enabled.Contains(IgnoreOptionId.SmartIgnore);

		return new IgnoreRules(
			IgnoreHiddenFolders: false,
			IgnoreHiddenFiles: false,
			IgnoreDotFolders: enabled.Contains(IgnoreOptionId.DotFolders),
			IgnoreDotFiles: enabled.Contains(IgnoreOptionId.DotFiles),
			SmartIgnoredFolders: emptyNames,
			SmartIgnoredFiles: emptyNames)
		{
			UseGitIgnore = enabled.Contains(IgnoreOptionId.UseGitIgnore),
			UseSmartIgnore = useSmartIgnore,
			IgnoreEmptyFolders = enabled.Contains(IgnoreOptionId.EmptyFolders),
			IgnoreEmptyFiles = enabled.Contains(IgnoreOptionId.EmptyFiles),
			IgnoreExtensionlessFiles = enabled.Contains(IgnoreOptionId.ExtensionlessFiles),
			GitIgnoreMatcher = gitIgnoreMatcher,
			GitIgnoreCandidateMatcher = gitIgnoreMatcher,
			SmartArtifactIgnoreMatcher = useSmartIgnore
				? SmartArtifactIgnoreMatcher.Default
				: SmartArtifactIgnoreMatcher.Empty,
			SmartArtifactIgnoreCandidateMatcher = SmartArtifactIgnoreMatcher.Default
		};
	}

	private static TreeFilterOptions CreateTreeOptions(IgnoreRules rules) =>
		new(
			AllowedExtensions: new HashSet<string>(StringComparer.OrdinalIgnoreCase)
			{
				string.Empty,
				".bin",
				".cs",
				".csproj",
				".config",
				".dat",
				".env",
				".gitignore",
				".json",
				".log",
				".md",
				".nupkg",
				".pom",
				".txt",
				".user"
			},
			AllowedRootFolders: new HashSet<string>(PathComparer.Default)
			{
				".local",
				".m2",
				"artifact-store",
				"empty",
				"empty-folder",
				"emptied-by-file",
				"git-owned",
				"keep",
				"packages",
				"project",
				"src"
			},
			IgnoreRules: rules);

	private static string[] CollectRelativeFiles(FileSystemNode root, string rootPath)
	{
		var files = new List<string>();
		CollectRelativeFiles(root, rootPath, files);
		files.Sort(StringComparer.Ordinal);
		return files.ToArray();
	}

	private static void CollectRelativeFiles(
		FileSystemNode node,
		string rootPath,
		ICollection<string> files)
	{
		if (!node.IsDirectory)
		{
			files.Add(Path.GetRelativePath(rootPath, node.FullPath).Replace('\\', '/'));
			return;
		}

		foreach (var child in node.Children)
			CollectRelativeFiles(child, rootPath, files);
	}

	private static bool ContainsPath(FileSystemNode root, string relativePath)
	{
		var current = root;
		foreach (var segment in relativePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries))
		{
			var child = current.Children.FirstOrDefault(candidate =>
				string.Equals(candidate.Name, segment, StringComparison.OrdinalIgnoreCase));
			if (child is null)
				return false;

			current = child;
		}

		return true;
	}

	private static SelectionRefreshContext CreateSmartToggleContext(
		string rootPath,
		SelectionRefreshSnapshot snapshot,
		bool isEnabled)
	{
		var context = CreateContextFromSnapshot(rootPath, snapshot);
		var selected = new HashSet<IgnoreOptionId>(context.IgnoreSelectionCache);
		var states = new Dictionary<IgnoreOptionId, bool>(context.IgnoreOptionStateCache)
		{
			[IgnoreOptionId.SmartIgnore] = isEnabled
		};
		if (isEnabled)
			selected.Add(IgnoreOptionId.SmartIgnore);
		else
			selected.Remove(IgnoreOptionId.SmartIgnore);

		return context with
		{
			IgnoreSelectionInitialized = true,
			IgnoreSelectionCache = selected,
			IgnoreOptionStateCache = states,
			IgnoreOptionStateCacheIsComplete = true,
			IgnoreAllPreference = null
		};
	}

	private static void AssertSmartState(
		SelectionRefreshSnapshot snapshot,
		bool expectedChecked,
		bool expectedArtifactExtensions)
	{
		var smart = Assert.Single(snapshot.IgnoreOptions, option => option.Id == IgnoreOptionId.SmartIgnore);
		Assert.Equal(expectedChecked, smart.IsChecked);
		Assert.True(snapshot.ControllerImpactCounts.SmartIgnore > 0);
		Assert.Equal(
			expectedArtifactExtensions,
			snapshot.EffectiveExtensionOptions.Any(option => option.Name == ".nupkg"));
		Assert.Equal(
			expectedArtifactExtensions,
			snapshot.EffectiveExtensionOptions.Any(option => option.Name == ".user"));
		Assert.Contains(snapshot.EffectiveExtensionOptions, option => option.Name == ".cs");
	}

	public sealed record IgnoreGoldenCase(
		string Name,
		IgnoreOptionId[] EnabledOptions,
		string[] HiddenFiles,
		bool EmptyFolderVisible,
		bool EmptiedByFileFolderVisible)
	{
		public override string ToString() => Name;
	}
}
