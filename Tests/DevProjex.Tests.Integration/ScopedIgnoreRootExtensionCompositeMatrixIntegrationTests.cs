namespace DevProjex.Tests.Integration;

public sealed class ScopedIgnoreRootExtensionCompositeMatrixIntegrationTests
{
	[Theory]
	[MemberData(nameof(CompositeScenarios))]
	public void CompositeRootExtensionAndControllerMatrix_StaysConsistentAcrossScannerAndTree(
		CompositeScenario scenario)
	{
		using var temp = CreateCompositeWorkspace();
		var rulesService = CreateRulesService();
		var scanOptions = new ScanOptionsUseCase(new FileSystemScanner());
		var selectedRoots = scenario.SelectedRoots.ToHashSet(PathComparer.Default);
		var selectedExtensions = scenario.SelectedExtensions.ToHashSet(StringComparer.OrdinalIgnoreCase);
		var rules = rulesService.Build(temp.Path, scenario.SelectedIgnoreOptions, selectedRoots);
		var extensionPolicy = new ExtensionSetInclusionPolicy(selectedExtensions);

		var ignoreOnly = scanOptions.GetIgnoreSectionSnapshotForRootFolders(
			temp.Path,
			selectedRoots,
			extensionDiscoveryRules: rules,
			effectiveRules: rules,
			effectiveExtensionPolicy: extensionPolicy,
			includeDirectoryToggleProbeRoots: true,
			cancellationToken: TestContext.Current.CancellationToken,
			includeControllerImpactProbeRoots: true);
		var workspace = scanOptions.GetProjectWorkspaceSnapshotForRootFolders(
			temp.Path,
			selectedRoots,
			extensionDiscoveryRules: rules,
			effectiveRules: rules,
			effectiveExtensionPolicy: extensionPolicy,
			includeDirectoryToggleProbeRoots: true,
			cancellationToken: TestContext.Current.CancellationToken,
			includeControllerImpactProbeRoots: true);

		AssertIgnoreSectionEqual(ignoreOnly.Value, workspace.Value.IgnoreSection);
		Assert.NotNull(workspace.Value.TreeInventory);
		AssertExpectedMinimumCounts(scenario, workspace.Value.IgnoreSection);
		AssertTreeProjectionMatchesDirectBuild(temp.Path, selectedRoots, selectedExtensions, rules, workspace.Value.TreeInventory);
		AssertPathVisibility(temp.Path, selectedRoots, selectedExtensions, rules, scenario);
	}

	public static IEnumerable<object[]> CompositeScenarios()
	{
		yield return
		[
			new CompositeScenario(
				"api-git-and-dot-on",
				["api"],
				[".cs", ".log", ".json", ".txt"],
				[
					IgnoreOptionId.UseGitIgnore,
					IgnoreOptionId.SmartIgnore,
					IgnoreOptionId.DotFolders,
					IgnoreOptionId.DotFiles,
					IgnoreOptionId.EmptyFolders,
					IgnoreOptionId.EmptyFiles,
					IgnoreOptionId.ExtensionlessFiles
				],
				VisiblePaths:
				[
					"api/src/Program.cs",
					"api/src/important.log"
				],
				HiddenPaths:
				[
					"api/src/runtime.log",
					"api/.git-owned/payload.txt",
					"api/.idea/settings.json",
					"api/.gitignore"
				],
				MinimumDotFolders: 1,
				MinimumDotFiles: 0,
				MinimumGitImpact: 1,
				MinimumSmartImpact: 0)
		];

		yield return
		[
			new CompositeScenario(
				"api-git-off-reassigns-dot-roots",
				["api"],
				[".cs", ".log", ".json", ".txt"],
				[
					IgnoreOptionId.DotFolders,
					IgnoreOptionId.DotFiles,
					IgnoreOptionId.EmptyFolders,
					IgnoreOptionId.EmptyFiles,
					IgnoreOptionId.ExtensionlessFiles
				],
				VisiblePaths:
				[
					"api/src/Program.cs",
					"api/src/runtime.log",
					"api/src/important.log"
				],
				HiddenPaths:
				[
					"api/.git-owned/payload.txt",
					"api/.idea/settings.json",
					"api/.gitignore"
				],
				MinimumDotFolders: 2,
				MinimumDotFiles: 0,
				MinimumGitImpact: 1,
				MinimumSmartImpact: 0)
		];

		yield return
		[
			new CompositeScenario(
				"web-smart-on-dot-off",
				["web"],
				[".ts", ".js", ".json"],
				[
					IgnoreOptionId.SmartIgnore,
					IgnoreOptionId.DotFiles,
					IgnoreOptionId.EmptyFolders,
					IgnoreOptionId.EmptyFiles,
					IgnoreOptionId.ExtensionlessFiles
				],
				VisiblePaths:
				[
					"web/src/app.ts"
				],
				HiddenPaths:
				[
					"web/node_modules/pkg/index.js",
					"web/.cache/cache.json"
				],
				MinimumDotFolders: 0,
				MinimumDotFiles: 0,
				MinimumGitImpact: 0,
				MinimumSmartImpact: 1)
		];

		yield return
		[
			new CompositeScenario(
				"web-smart-off-dot-on",
				["web"],
				[".ts", ".js", ".json"],
				[
					IgnoreOptionId.DotFolders,
					IgnoreOptionId.DotFiles,
					IgnoreOptionId.EmptyFolders,
					IgnoreOptionId.EmptyFiles,
					IgnoreOptionId.ExtensionlessFiles
				],
				VisiblePaths:
				[
					"web/src/app.ts",
					"web/node_modules/pkg/index.js"
				],
				HiddenPaths:
				[
					"web/.cache/cache.json"
				],
				MinimumDotFolders: 1,
				MinimumDotFiles: 0,
				MinimumGitImpact: 0,
				MinimumSmartImpact: 1)
		];

		yield return
		[
			new CompositeScenario(
				"api-and-web-controllers-together",
				["api", "web"],
				[".cs", ".ts", ".js", ".log", ".json"],
				[
					IgnoreOptionId.UseGitIgnore,
					IgnoreOptionId.SmartIgnore,
					IgnoreOptionId.DotFolders,
					IgnoreOptionId.DotFiles,
					IgnoreOptionId.EmptyFolders,
					IgnoreOptionId.EmptyFiles,
					IgnoreOptionId.ExtensionlessFiles
				],
				VisiblePaths:
				[
					"api/src/Program.cs",
					"api/src/important.log",
					"web/src/app.ts"
				],
				HiddenPaths:
				[
					"api/src/runtime.log",
					"api/.idea/settings.json",
					"web/node_modules/pkg/index.js",
					"web/.cache/cache.json"
				],
				MinimumDotFolders: 1,
				MinimumDotFiles: 0,
				MinimumGitImpact: 1,
				MinimumSmartImpact: 1)
		];

		yield return
		[
			new CompositeScenario(
				"docs-extension-filter-with-all-ignore-off",
				["docs"],
				[".md"],
				[],
				VisiblePaths:
				[
					"docs/readme.md",
					"docs/.draft/notes.md",
					"docs/README"
				],
				HiddenPaths:
				[
					"docs/empty.txt"
				],
				MinimumDotFolders: 1,
				MinimumDotFiles: 0,
				MinimumGitImpact: 0,
				MinimumSmartImpact: 0)
		];
	}

	private static TemporaryDirectory CreateCompositeWorkspace()
	{
		var temp = new TemporaryDirectory();
		temp.CreateFile("api/.gitignore", "*.log\n!important.log\n.git-owned/\n");
		temp.CreateFile("api/App.csproj", "<Project />\n");
		temp.CreateFile("api/src/Program.cs", "Console.WriteLine(\"api\");\n");
		temp.CreateFile("api/src/runtime.log", "ignored by scoped gitignore\n");
		temp.CreateFile("api/src/important.log", "explicitly unignored\n");
		temp.CreateFile("api/.git-owned/payload.txt", "git owned dot folder\n");
		temp.CreateFile("api/.idea/settings.json", "{}\n");

		temp.CreateFile("web/package.json", "{}\n");
		temp.CreateFile("web/src/app.ts", "export const ok = true;\n");
		temp.CreateFile("web/node_modules/pkg/index.js", "module.exports = {};\n");
		temp.CreateFile("web/.cache/CACHEDIR.TAG", "Signature: 8a477f597d28d172789f06886806bc55\n");
		temp.CreateFile("web/.cache/cache.json", "{}\n");

		temp.CreateFile("docs/readme.md", "# docs\n");
		temp.CreateFile("docs/.draft/notes.md", "# draft\n");
		temp.CreateFile("docs/README", "extensionless docs\n");
		temp.CreateFile("docs/empty.txt", string.Empty);
		temp.CreateDirectory("docs/empty-root");
		return temp;
	}

	private static IgnoreRulesService CreateRulesService()
	{
		return new IgnoreRulesService(new SmartIgnoreService([
			new DotNetArtifactsIgnoreRule(),
			new FrontendArtifactsIgnoreRule()
		]));
	}

	private static void AssertIgnoreSectionEqual(
		IgnoreSectionScanData expected,
		IgnoreSectionScanData actual)
	{
		Assert.Equal(expected.Extensions.Order(StringComparer.OrdinalIgnoreCase), actual.Extensions.Order(StringComparer.OrdinalIgnoreCase));
		Assert.Equal(expected.RawIgnoreOptionCounts, actual.RawIgnoreOptionCounts);
		Assert.Equal(expected.EffectiveIgnoreOptionCounts, actual.EffectiveIgnoreOptionCounts);
		Assert.Equal(expected.ControllerImpactCounts, actual.ControllerImpactCounts);
	}

	private static void AssertExpectedMinimumCounts(
		CompositeScenario scenario,
		IgnoreSectionScanData scanData)
	{
		Assert.True(
			scanData.EffectiveIgnoreOptionCounts.DotFolders >= scenario.MinimumDotFolders,
			$"{scenario.Name}: DotFolders count was {scanData.EffectiveIgnoreOptionCounts.DotFolders}.");
		Assert.True(
			scanData.EffectiveIgnoreOptionCounts.DotFiles >= scenario.MinimumDotFiles,
			$"{scenario.Name}: DotFiles count was {scanData.EffectiveIgnoreOptionCounts.DotFiles}.");
		Assert.True(
			scanData.ControllerImpactCounts.GitIgnore >= scenario.MinimumGitImpact,
			$"{scenario.Name}: Git impact was {scanData.ControllerImpactCounts.GitIgnore}.");
		Assert.True(
			scanData.ControllerImpactCounts.SmartIgnore >= scenario.MinimumSmartImpact,
			$"{scenario.Name}: Smart impact was {scanData.ControllerImpactCounts.SmartIgnore}.");
	}

	private static void AssertTreeProjectionMatchesDirectBuild(
		string rootPath,
		IReadOnlySet<string> selectedRoots,
		IReadOnlySet<string> selectedExtensions,
		IgnoreRules rules,
		ProjectTreeInventorySnapshot inventory)
	{
		var options = new TreeFilterOptions(selectedExtensions, selectedRoots, rules);
		var builder = new TreeBuilder();
		var direct = builder.Build(rootPath, options, TestContext.Current.CancellationToken);
		var projected = builder.Build(inventory, options, TestContext.Current.CancellationToken);

		Assert.Equal(FlattenTree(direct.Root), FlattenTree(projected.Root));
	}

	private static void AssertPathVisibility(
		string rootPath,
		IReadOnlySet<string> selectedRoots,
		IReadOnlySet<string> selectedExtensions,
		IgnoreRules rules,
		CompositeScenario scenario)
	{
		var tree = new TreeBuilder().Build(
			rootPath,
			new TreeFilterOptions(selectedExtensions, selectedRoots, rules),
			TestContext.Current.CancellationToken);
		var paths = FlattenTree(tree.Root)
			.Select(path => Path.GetRelativePath(rootPath, path).Replace(Path.DirectorySeparatorChar, '/'))
			.ToHashSet(StringComparer.OrdinalIgnoreCase);

		foreach (var path in scenario.VisiblePaths)
			Assert.Contains(path, paths);
		foreach (var path in scenario.HiddenPaths)
			Assert.DoesNotContain(path, paths);
	}

	private static List<string> FlattenTree(FileSystemNode root)
	{
		var paths = new List<string>();
		var pending = new Stack<FileSystemNode>();
		pending.Push(root);
		while (pending.Count > 0)
		{
			var node = pending.Pop();
			paths.Add(node.FullPath);
			for (var index = node.Children.Count - 1; index >= 0; index--)
				pending.Push(node.Children[index]);
		}

		return paths;
	}

	public sealed record CompositeScenario(
		string Name,
		IReadOnlyCollection<string> SelectedRoots,
		IReadOnlyCollection<string> SelectedExtensions,
		IReadOnlyCollection<IgnoreOptionId> SelectedIgnoreOptions,
		IReadOnlyCollection<string> VisiblePaths,
		IReadOnlyCollection<string> HiddenPaths,
		int MinimumDotFolders,
		int MinimumDotFiles,
		int MinimumGitImpact,
		int MinimumSmartImpact)
	{
		public override string ToString() => Name;
	}
}
