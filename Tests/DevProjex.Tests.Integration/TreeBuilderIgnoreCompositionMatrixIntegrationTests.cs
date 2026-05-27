namespace DevProjex.Tests.Integration;

public sealed class TreeBuilderIgnoreCompositionMatrixIntegrationTests
{
	public static IEnumerable<object[]> CompositionCases()
	{
		foreach (var useGitIgnore in new[] { false, true })
		{
			foreach (var useSmartIgnore in new[] { false, true })
			{
				foreach (var ignoreEmptyFolders in new[] { false, true })
					yield return [useGitIgnore, useSmartIgnore, ignoreEmptyFolders];
			}
		}
	}

	[Theory]
	[MemberData(nameof(CompositionCases))]
	public void Build_GitSmartAndEmptyFolderComposition_RemainsConsistent(
		bool useGitIgnore,
		bool useSmartIgnore,
		bool ignoreEmptyFolders)
	{
		using var temp = new TemporaryDirectory();
		SeedWorkspace(temp);

		var smartService = new SmartIgnoreService([
			new FixedSmartIgnoreRule(["node_modules"], [])
		]);
		var rulesService = new IgnoreRulesService(smartService);
		var selected = new List<IgnoreOptionId>();
		if (useGitIgnore)
			selected.Add(IgnoreOptionId.UseGitIgnore);
		if (useSmartIgnore)
			selected.Add(IgnoreOptionId.SmartIgnore);
		if (ignoreEmptyFolders)
			selected.Add(IgnoreOptionId.EmptyFolders);

		var rules = rulesService.Build(temp.Path, selected, ["proj-git", "proj-no-git"]);

		var builder = new TreeBuilder();
		var tree = builder.Build(temp.Path, new TreeFilterOptions(
			AllowedExtensions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs", ".txt", ".json", ".js" },
			AllowedRootFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "proj-git", "proj-no-git" },
			IgnoreRules: rules), cancellationToken: TestContext.Current.CancellationToken);

		var gitProject = tree.Root.Children.Single(node => node.Name == "proj-git");
		var noGitProject = tree.Root.Children.Single(node => node.Name == "proj-no-git");

		Assert.Equal(!useGitIgnore, gitProject.Children.Any(node => node.Name == "generated"));
		Assert.Equal(!useSmartIgnore, noGitProject.Children.Any(node => node.Name == "node_modules"));

		var gitMixedParentVisible = gitProject.Children.Any(node => node.Name == "mixed-parent");
		var noGitMixedParentVisible = noGitProject.Children.Any(node => node.Name == "mixed-parent");
		var expectedGitMixedParentVisible = !ignoreEmptyFolders || !useGitIgnore;
		var expectedNoGitMixedParentVisible = !ignoreEmptyFolders || !useSmartIgnore;

		Assert.Equal(expectedGitMixedParentVisible, gitMixedParentVisible);
		Assert.Equal(expectedNoGitMixedParentVisible, noGitMixedParentVisible);
	}

	[Fact]
	public void Build_GitIgnoreNegationAndEmptyFolderIgnore_KeepExplicitlyUnignoredBranch()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("proj-git/.gitignore", "generated/\n!generated/keep/\n");
		temp.CreateFile("proj-git/generated/drop/file.txt", "drop");
		temp.CreateFile("proj-git/generated/keep/file.txt", "keep");
		temp.CreateFile("proj-git/src/main.cs", "class App {}");

		var rulesService = new IgnoreRulesService(new SmartIgnoreService([]));
		var rules = rulesService.Build(
			temp.Path,
			[IgnoreOptionId.UseGitIgnore, IgnoreOptionId.EmptyFolders],
			["proj-git"]);

		var builder = new TreeBuilder();
		var tree = builder.Build(temp.Path, new TreeFilterOptions(
			AllowedExtensions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs", ".txt" },
			AllowedRootFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "proj-git" },
			IgnoreRules: rules), cancellationToken: TestContext.Current.CancellationToken);

		var gitProject = tree.Root.Children.Single(node => node.Name == "proj-git");
		var generated = gitProject.Children.Single(node => node.Name == "generated");
		Assert.DoesNotContain(generated.Children, node => node.Name == "drop");
		Assert.Contains(generated.Children, node => node.Name == "keep");
	}

	private static void SeedWorkspace(TemporaryDirectory temp)
	{
		temp.CreateFile("proj-git/.gitignore", "**/generated/");
		temp.CreateFile("proj-git/src/main.cs", "class App {}");
		temp.CreateFile("proj-git/generated/app.txt", "generated");
		temp.CreateFile("proj-git/mixed-parent/generated/child.txt", "x");

		temp.CreateFile("proj-no-git/package.json", "{}");
		temp.CreateFile("proj-no-git/src/main.js", "export {}");
		temp.CreateFile("proj-no-git/node_modules/pkg/index.js", "x");
		temp.CreateFile("proj-no-git/mixed-parent/node_modules/pkg/index.js", "x");
	}

	private sealed class FixedSmartIgnoreRule(
		IReadOnlyCollection<string> folders,
		IReadOnlyCollection<string> files)
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
