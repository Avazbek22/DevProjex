namespace DevProjex.Tests.Integration;

public sealed class ScopedIgnoreRulesIntegrationTests
{
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void SingleProjectWithGitIgnore_SmartIgnoreRemainsIndependentFromGitIgnore(bool useGitIgnore)
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(".gitignore", "# marker");
		temp.CreateFile("App.csproj", "<Project />");
		temp.CreateFile("src/app.cs", "class App {}");
		temp.CreateFile("bin/output.txt", "artifact");

		var smartService = new SmartIgnoreService([
			new FixedSmartIgnoreRule(["bin"], [])
		]);
		var ignoreRulesService = new IgnoreRulesService(smartService);
		var selected = new List<IgnoreOptionId> { IgnoreOptionId.SmartIgnore };
		if (useGitIgnore)
			selected.Add(IgnoreOptionId.UseGitIgnore);

		var rules = ignoreRulesService.Build(temp.Path, selected);
		var treeBuilder = new TreeBuilder();
		var result = treeBuilder.Build(temp.Path, new TreeFilterOptions(
			AllowedExtensions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs", ".txt", ".csproj" },
			AllowedRootFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "src", "bin" },
			IgnoreRules: rules), cancellationToken: TestContext.Current.CancellationToken);

		Assert.Equal(useGitIgnore, rules.UseGitIgnore);
		Assert.True(rules.UseSmartIgnore);
		Assert.DoesNotContain(result.Root.Children, child => child.Name == "bin");
	}

	[Fact]
	public void SelectedNestedFolder_WithSingleNestedDotNetProject_EnablesSmartIgnoreAndHidesBinObj()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("Visual Studio 2019/America/America.sln", "");
		temp.CreateFile("Visual Studio 2019/America/America/America.csproj", "<Project />");
		temp.CreateFile("Visual Studio 2019/America/America/Program.cs", "class Program {}");
		temp.CreateFile("Visual Studio 2019/America/America/bin/Debug/America.exe", "binary");
		temp.CreateFile("Visual Studio 2019/America/America/obj/Debug/cache.txt", "cache");

		var smartService = new SmartIgnoreService([
			new DotNetArtifactsIgnoreRule()
		]);
		var rulesService = new IgnoreRulesService(smartService);

		var availability = rulesService.GetIgnoreOptionsAvailability(temp.Path, ["Visual Studio 2019"]);
		Assert.True(availability.IncludeSmartIgnore);

		var rules = rulesService.Build(
			temp.Path,
			[IgnoreOptionId.SmartIgnore],
			selectedRootFolders: ["Visual Studio 2019"]);

		Assert.True(rules.UseSmartIgnore);
		Assert.Contains("bin", rules.SmartIgnoredFolders);
		Assert.Contains("obj", rules.SmartIgnoredFolders);

		var treeBuilder = new TreeBuilder();
		var result = treeBuilder.Build(temp.Path, new TreeFilterOptions(
			AllowedExtensions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs", ".txt", ".exe", ".csproj", ".sln" },
			AllowedRootFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Visual Studio 2019" },
			IgnoreRules: rules), cancellationToken: TestContext.Current.CancellationToken);

		var vsFolder = result.Root.Children.Single(child => child.Name == "Visual Studio 2019");
		var americaContainer = vsFolder.Children.Single(child => child.Name == "America");
		var projectFolder = americaContainer.Children.Single(child => child.Name == "America");

		Assert.DoesNotContain(projectFolder.Children, child => child.Name == "bin");
		Assert.DoesNotContain(projectFolder.Children, child => child.Name == "obj");
		Assert.Contains(projectFolder.Children, child => child.Name == "Program.cs");
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void SelectedParentFolderDepthTwo_DotNetProject_SmartIgnoreToggleControlsBinObjVisibility(bool useSmartIgnore)
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("Documents/Visual Studio 2019/America/America.sln", "");
		temp.CreateFile("Documents/Visual Studio 2019/America/America/America.csproj", "<Project />");
		temp.CreateFile("Documents/Visual Studio 2019/America/America/Program.cs", "class Program {}");
		temp.CreateFile("Documents/Visual Studio 2019/America/America/bin/Debug/America.exe", "binary");
		temp.CreateFile("Documents/Visual Studio 2019/America/America/obj/Debug/cache.txt", "cache");

		var smartService = new SmartIgnoreService([
			new DotNetArtifactsIgnoreRule()
		]);
		var rulesService = new IgnoreRulesService(smartService);
		var selectedOptions = useSmartIgnore
			? new[] { IgnoreOptionId.SmartIgnore }
			: Array.Empty<IgnoreOptionId>();

		var rules = rulesService.Build(
			temp.Path,
			selectedOptions,
			selectedRootFolders: ["Documents"]);

		Assert.Equal(useSmartIgnore, rules.UseSmartIgnore);

		var treeBuilder = new TreeBuilder();
		var result = treeBuilder.Build(temp.Path, new TreeFilterOptions(
			AllowedExtensions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs", ".txt", ".exe", ".csproj", ".sln" },
			AllowedRootFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Documents" },
			IgnoreRules: rules), cancellationToken: TestContext.Current.CancellationToken);

		var documents = result.Root.Children.Single(child => child.Name == "Documents");
		var vsFolder = documents.Children.Single(child => child.Name == "Visual Studio 2019");
		var americaContainer = vsFolder.Children.Single(child => child.Name == "America");
		var projectFolder = americaContainer.Children.Single(child => child.Name == "America");

		Assert.Equal(!useSmartIgnore, projectFolder.Children.Any(child => child.Name == "bin"));
		Assert.Equal(!useSmartIgnore, projectFolder.Children.Any(child => child.Name == "obj"));
		Assert.Contains(projectFolder.Children, child => child.Name == "Program.cs");
	}

	[Theory]
	[InlineData(0, false)]
	[InlineData(0, true)]
	[InlineData(1, false)]
	[InlineData(1, true)]
	[InlineData(2, false)]
	[InlineData(2, true)]
	public void NestedDotNetProject_SmartIgnoreToggle_WorksForDifferentOpenedRootLevels(int rootMode, bool useSmartIgnore)
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("Documents/Visual Studio 2019/America/America.sln", "");
		temp.CreateFile("Documents/Visual Studio 2019/America/America/America.csproj", "<Project />");
		temp.CreateFile("Documents/Visual Studio 2019/America/America/Program.cs", "class Program {}");
		temp.CreateFile("Documents/Visual Studio 2019/America/America/bin/Debug/America.exe", "binary");
		temp.CreateFile("Documents/Visual Studio 2019/America/America/obj/Debug/cache.txt", "cache");

		var (openedRootPath, selectedRootFolders, pathChain) = ResolveRootMode(temp.Path, rootMode);
		var smartService = new SmartIgnoreService([
			new DotNetArtifactsIgnoreRule()
		]);
		var rulesService = new IgnoreRulesService(smartService);
		var selectedOptions = useSmartIgnore
			? new[] { IgnoreOptionId.SmartIgnore }
			: Array.Empty<IgnoreOptionId>();
		var rules = rulesService.Build(openedRootPath, selectedOptions, selectedRootFolders);

		var treeBuilder = new TreeBuilder();
		var result = treeBuilder.Build(openedRootPath, new TreeFilterOptions(
			AllowedExtensions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs", ".txt", ".exe", ".csproj", ".sln" },
			AllowedRootFolders: new HashSet<string>(selectedRootFolders, StringComparer.OrdinalIgnoreCase),
			IgnoreRules: rules), cancellationToken: TestContext.Current.CancellationToken);

		var projectFolder = WalkChain(result.Root, pathChain);
		Assert.Equal(!useSmartIgnore, projectFolder.Children.Any(child => child.Name == "bin"));
		Assert.Equal(!useSmartIgnore, projectFolder.Children.Any(child => child.Name == "obj"));
		Assert.Contains(projectFolder.Children, child => child.Name == "Program.cs");
	}

	[Fact]
	public void SelectedParentFolderDepthTwo_WithNestedGitIgnore_HidesBinObjViaGitIgnore()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("Documents/Visual Studio 2019/America/.gitignore", "bin/\nobj/\n");
		temp.CreateFile("Documents/Visual Studio 2019/America/America/America.csproj", "<Project />");
		temp.CreateFile("Documents/Visual Studio 2019/America/America/Program.cs", "class Program {}");
		temp.CreateFile("Documents/Visual Studio 2019/America/America/bin/Debug/America.exe", "binary");
		temp.CreateFile("Documents/Visual Studio 2019/America/America/obj/Debug/cache.txt", "cache");

		var smartService = new SmartIgnoreService([
			new DotNetArtifactsIgnoreRule()
		]);
		var rulesService = new IgnoreRulesService(smartService);
		var rules = rulesService.Build(
			temp.Path,
			[IgnoreOptionId.UseGitIgnore],
			selectedRootFolders: ["Documents"]);

		Assert.True(rules.UseGitIgnore);
		Assert.False(rules.UseSmartIgnore);

		var treeBuilder = new TreeBuilder();
		var result = treeBuilder.Build(temp.Path, new TreeFilterOptions(
			AllowedExtensions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs", ".txt", ".exe", ".csproj" },
			AllowedRootFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Documents" },
			IgnoreRules: rules), cancellationToken: TestContext.Current.CancellationToken);

		var documents = result.Root.Children.Single(child => child.Name == "Documents");
		var vsFolder = documents.Children.Single(child => child.Name == "Visual Studio 2019");
		var americaContainer = vsFolder.Children.Single(child => child.Name == "America");
		var projectFolder = americaContainer.Children.Single(child => child.Name == "America");

		Assert.DoesNotContain(projectFolder.Children, child => child.Name == "bin");
		Assert.DoesNotContain(projectFolder.Children, child => child.Name == "obj");
		Assert.Contains(projectFolder.Children, child => child.Name == "Program.cs");
	}

	[Theory]
	[InlineData(0)]
	[InlineData(1)]
	[InlineData(2)]
	public void NestedGitIgnoreProject_UseGitIgnore_WorksForDifferentOpenedRootLevels(int rootMode)
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("Documents/Visual Studio 2019/America/.gitignore", "bin/\nobj/\n");
		temp.CreateFile("Documents/Visual Studio 2019/America/America/America.csproj", "<Project />");
		temp.CreateFile("Documents/Visual Studio 2019/America/America/Program.cs", "class Program {}");
		temp.CreateFile("Documents/Visual Studio 2019/America/America/bin/Debug/America.exe", "binary");
		temp.CreateFile("Documents/Visual Studio 2019/America/America/obj/Debug/cache.txt", "cache");

		var (openedRootPath, selectedRootFolders, pathChain) = ResolveRootMode(temp.Path, rootMode);
		var smartService = new SmartIgnoreService([
			new DotNetArtifactsIgnoreRule()
		]);
		var rulesService = new IgnoreRulesService(smartService);
		var rules = rulesService.Build(
			openedRootPath,
			[IgnoreOptionId.UseGitIgnore],
			selectedRootFolders: selectedRootFolders);

		Assert.True(rules.UseGitIgnore);

		var treeBuilder = new TreeBuilder();
		var result = treeBuilder.Build(openedRootPath, new TreeFilterOptions(
			AllowedExtensions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs", ".txt", ".exe", ".csproj", ".sln" },
			AllowedRootFolders: new HashSet<string>(selectedRootFolders, StringComparer.OrdinalIgnoreCase),
			IgnoreRules: rules), cancellationToken: TestContext.Current.CancellationToken);

		var projectFolder = WalkChain(result.Root, pathChain);
		Assert.DoesNotContain(projectFolder.Children, child => child.Name == "bin");
		Assert.DoesNotContain(projectFolder.Children, child => child.Name == "obj");
		Assert.Contains(projectFolder.Children, child => child.Name == "Program.cs");
	}

	[Theory]
	[InlineData(false, false)]
	[InlineData(true, false)]
	[InlineData(false, true)]
	[InlineData(true, true)]
	public void MixedWorkspace_AppliesGitIgnoreAndSmartIgnorePerSelectedOptions(bool useGitIgnore, bool useSmartIgnore)
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("proj-git/.gitignore", "git_only/");
		temp.CreateFile("proj-git/App.csproj", "<Project />");
		temp.CreateFile("proj-git/git_only/data.txt", "git ignored");
		temp.CreateFile("proj-git/keep/data.txt", "keep");

		temp.CreateFile("proj-no-git/package.json", "{}");
		temp.CreateFile("proj-no-git/smart_only/data.txt", "smart ignored");
		temp.CreateFile("proj-no-git/keep/data.txt", "keep");

		var smartService = new SmartIgnoreService([
			new FixedSmartIgnoreRule(["smart_only"], [])
		]);
		var ignoreRulesService = new IgnoreRulesService(smartService);
		var selected = new List<IgnoreOptionId>();
		if (useGitIgnore)
			selected.Add(IgnoreOptionId.UseGitIgnore);
		if (useSmartIgnore)
			selected.Add(IgnoreOptionId.SmartIgnore);

		var rules = ignoreRulesService.Build(temp.Path, selected);
		var treeBuilder = new TreeBuilder();
		var result = treeBuilder.Build(temp.Path, new TreeFilterOptions(
			AllowedExtensions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".txt", ".json", ".csproj" },
			AllowedRootFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "proj-git", "proj-no-git" },
			IgnoreRules: rules), cancellationToken: TestContext.Current.CancellationToken);

		var projGit = result.Root.Children.Single(child => child.Name == "proj-git");
		var projNoGit = result.Root.Children.Single(child => child.Name == "proj-no-git");

		Assert.Equal(useGitIgnore, rules.UseGitIgnore);
		Assert.Equal(useSmartIgnore, rules.UseSmartIgnore);

		if (useGitIgnore)
			Assert.DoesNotContain(projGit.Children, child => child.Name == "git_only");
		else
			Assert.Contains(projGit.Children, child => child.Name == "git_only");

		if (useSmartIgnore)
			Assert.DoesNotContain(projNoGit.Children, child => child.Name == "smart_only");
		else
			Assert.Contains(projNoGit.Children, child => child.Name == "smart_only");
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

	private static (string OpenedRootPath, IReadOnlyCollection<string> SelectedRootFolders, IReadOnlyList<string> ProjectPathChain)
		ResolveRootMode(string tempPath, int rootMode)
	{
		return rootMode switch
		{
			0 => (
				OpenedRootPath: tempPath,
				SelectedRootFolders: ["Documents"],
				ProjectPathChain: ["Documents", "Visual Studio 2019", "America", "America"]),
			1 => (
				OpenedRootPath: Path.Combine(tempPath, "Documents"),
				SelectedRootFolders: ["Visual Studio 2019"],
				ProjectPathChain: ["Visual Studio 2019", "America", "America"]),
			2 => (
				OpenedRootPath: Path.Combine(tempPath, "Documents", "Visual Studio 2019"),
				SelectedRootFolders: new[] { "America" },
				ProjectPathChain: new[] { "America", "America" }),
			_ => throw new ArgumentOutOfRangeException(nameof(rootMode), rootMode, "Unsupported root mode.")
		};
	}

	private static FileSystemNode WalkChain(FileSystemNode root, IReadOnlyList<string> chain)
	{
		var current = root;
		foreach (var segment in chain)
			current = current.Children.Single(child => child.Name == segment);

		return current;
	}
}
