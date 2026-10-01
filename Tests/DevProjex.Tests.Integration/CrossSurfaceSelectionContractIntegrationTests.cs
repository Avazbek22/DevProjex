using DevProjex.Application.Context;

namespace DevProjex.Tests.Integration;

public sealed class CrossSurfaceSelectionContractIntegrationTests
{
	[Fact]
	public async Task MixedStackWorkspace_SelectedRootsExtensionsAndExclusionsProduceOneEffectiveFileSet()
	{
		using var workspace = CreateMixedStackWorkspace();
		var selectedRoots = new HashSet<string>(
			["source files", "кириллица", "文档", "-leading-root"],
			PathComparer.Default);
		var selectedExtensions = new HashSet<string>(
			[".cs", ".md", ".json"],
			StringComparer.OrdinalIgnoreCase);
		IgnoreOptionId[] selectedIgnoreOptions =
		[
			IgnoreOptionId.UseGitIgnore,
			IgnoreOptionId.SmartIgnore,
			IgnoreOptionId.DotFiles,
			IgnoreOptionId.EmptyFolders,
			IgnoreOptionId.EmptyFiles
		];
		var rulesService = ProjectLoadWorkflowRuntime.CreateIgnoreRulesService();
		var rules = rulesService.Build(workspace.Path, selectedIgnoreOptions, selectedRoots);
		var treeOptions = new TreeFilterOptions(selectedExtensions, selectedRoots, rules);
		var scanner = new ScanOptionsUseCase(new FileSystemScanner());

		var availableRoots = scanner.GetRootFolders(
			workspace.Path,
			rules,
			TestContext.Current.CancellationToken).Value;
		var availableExtensions = scanner.GetExtensionsForRootFolders(
			workspace.Path,
			selectedRoots,
			IgnoreRulesProjection.ForExtensionAvailability(rules),
			TestContext.Current.CancellationToken).Value;
		var scan = scanner.GetProjectWorkspaceSnapshotForRootFolders(
			workspace.Path,
			selectedRoots,
			IgnoreRulesProjection.ForExtensionAvailability(rules),
			rules,
			new ExtensionSetInclusionPolicy(selectedExtensions),
			includeDirectoryToggleProbeRoots: true,
			cancellationToken: TestContext.Current.CancellationToken,
			includeControllerImpactProbeRoots: true);

		Assert.False(scan.RootAccessDenied);
		Assert.False(scan.HadAccessDenied);
		Assert.All(selectedRoots, root => Assert.Contains(root, availableRoots, PathComparer.Default));
		Assert.Contains("not selected", availableRoots, PathComparer.Default);
		Assert.Contains(".cs", availableExtensions);
		Assert.Contains(".md", availableExtensions);
		Assert.Contains(".json", availableExtensions);
		Assert.Contains(".tmp", availableExtensions);

		var inventory = Assert.IsType<ProjectTreeInventorySnapshot>(scan.Value.TreeInventory);
		var treeBuilder = new TreeBuilder();
		var directTree = treeBuilder.Build(
			workspace.Path,
			treeOptions,
			TestContext.Current.CancellationToken);
		var inventoryTree = treeBuilder.Build(
			inventory,
			treeOptions,
			TestContext.Current.CancellationToken);
		var analysisService = CreateProjectAnalysisService();
		var analysis = analysisService.Load(
			new ProjectAnalysisRequest(
				workspace.Path,
				selectedRoots,
				selectedExtensions,
				selectedIgnoreOptions),
			TestContext.Current.CancellationToken);
		var contextPlan = await new ProjectContextPlanner(analysisService).BuildAsync(
			new ProjectContextRequest(
				workspace.Path,
				new ProjectSelectionSpec(
					Roots: selectedRoots,
					Extensions: selectedExtensions,
					GitMode: GitFilteringMode.RespectGitIgnore,
					Exclusions:
					[
						ProjectExclusion.SmartIgnore,
						ProjectExclusion.DotFiles,
						ProjectExclusion.EmptyFolders,
						ProjectExclusion.EmptyFiles
					])),
			TestContext.Current.CancellationToken);

		var expectedFiles = new HashSet<string>(
			[
				"-leading-root/-entry.cs",
				"source files/build/docs/handwritten.cs",
				"source files/package.json",
				"source files/services/api/dist/contracts.json",
				"source files/src/-leading.cs",
				"source files/src/Program.cs",
				// The first repository now owns its scope; workspace-level rules do not leak inside.
				"source files/src/ignored-by-git.cs",
				"source files/src/файл.cs",
				"source files/src/文档.md",
				"кириллица/src/данные.cs",
				"文档/guide.md"
			],
			StringComparer.Ordinal);

		// Every consumer must project the same immutable selection contract. A mismatch
		// here means the UI sections can promise a file set that analysis/export does not use.
		AssertFileSetEqual(expectedFiles, CollectRelativeFiles(workspace.Path, directTree.Root), "direct tree");
		AssertFileSetEqual(expectedFiles, CollectRelativeFiles(workspace.Path, inventoryTree.Root), "inventory tree");
		AssertFileSetEqual(expectedFiles, CollectRelativeFiles(workspace.Path, analysis.Tree.Root), "analysis");
		AssertFileSetEqual(expectedFiles, CollectRelativeFiles(workspace.Path, contextPlan.EffectiveTree), "context tree");
		AssertFileSetEqual(
			expectedFiles,
			NormalizeRelativeFiles(workspace.Path, contextPlan.IncludedFiles),
			"context included files");

		string[] forbiddenFiles =
		[
			"not selected/should-not-appear.cs",
			"source files/.git/secret.cs",
			"source files/.secret.cs",
			"source files/dist/manifest.json",
			"source files/services/api/obj/project.assets.json",
			"source files/src/empty.cs",
			"source files/src/notes.tmp",
			"кириллица/.venv/leak.cs",
			"文档/target/leak.md",
			"-leading-root/vendor/leak.cs"
		];
		Assert.All(
			forbiddenFiles,
			path => Assert.DoesNotContain(path, NormalizeRelativeFiles(workspace.Path, contextPlan.IncludedFiles)));
	}

	[Fact]
	public async Task DeepProjectInsidePrunedContainerOwnsSmartIgnoreAcrossEveryConsumer()
	{
		using var workspace = new TemporaryDirectory();
		workspace.CreateFile("package.json", "{}\n");
		const string childRoot = "build/level-one/level-two/level-three/service";
		workspace.CreateFile($"{childRoot}/App.csproj", "<Project />\n");
		workspace.CreateFile($"{childRoot}/src/App.cs", "public sealed class App {}\n");
		workspace.CreateFile($"{childRoot}/build/DomainModel.cs", "public sealed class DomainModel {}\n");
		workspace.CreateFile($"{childRoot}/dist/contracts.json", "{\"source\":true}\n");
		var selectedRoots = new HashSet<string>(["build"], PathComparer.Default);
		var selectedExtensions = new HashSet<string>(
			[".cs", ".csproj", ".json"],
			StringComparer.OrdinalIgnoreCase);
		IgnoreOptionId[] selectedIgnoreOptions = [IgnoreOptionId.SmartIgnore];
		var rulesService = ProjectLoadWorkflowRuntime.CreateIgnoreRulesService();
		var rules = rulesService.Build(workspace.Path, selectedIgnoreOptions, selectedRoots);
		var options = new TreeFilterOptions(selectedExtensions, selectedRoots, rules);
		var scanner = new ScanOptionsUseCase(new FileSystemScanner());
		var scan = scanner.GetProjectWorkspaceSnapshotForRootFolders(
			workspace.Path,
			selectedRoots,
			IgnoreRulesProjection.ForExtensionAvailability(rules),
			rules,
			new ExtensionSetInclusionPolicy(selectedExtensions),
			includeDirectoryToggleProbeRoots: true,
			cancellationToken: TestContext.Current.CancellationToken,
			includeControllerImpactProbeRoots: true);
		var inventory = Assert.IsType<ProjectTreeInventorySnapshot>(scan.Value.TreeInventory);
		var treeBuilder = new TreeBuilder();
		var directTree = treeBuilder.Build(
			workspace.Path,
			options,
			TestContext.Current.CancellationToken);
		var inventoryTree = treeBuilder.Build(
			inventory,
			options,
			TestContext.Current.CancellationToken);
		var analysisService = CreateProjectAnalysisService();
		var analysis = analysisService.Load(
			new ProjectAnalysisRequest(
				workspace.Path,
				selectedRoots,
				selectedExtensions,
				selectedIgnoreOptions),
			TestContext.Current.CancellationToken);
		var contextPlan = await new ProjectContextPlanner(analysisService).BuildAsync(
			new ProjectContextRequest(
				workspace.Path,
				new ProjectSelectionSpec(
					Roots: selectedRoots,
					Extensions: selectedExtensions,
					GitMode: GitFilteringMode.None,
					Exclusions: [ProjectExclusion.SmartIgnore])),
			TestContext.Current.CancellationToken);
		var expectedFiles = new HashSet<string>(
			[
				"package.json",
				$"{childRoot}/App.csproj",
				$"{childRoot}/build/DomainModel.cs",
				$"{childRoot}/dist/contracts.json",
				$"{childRoot}/src/App.cs"
			],
			StringComparer.Ordinal);

		AssertFileSetEqual(expectedFiles, CollectRelativeFiles(workspace.Path, directTree.Root), "direct tree");
		AssertFileSetEqual(expectedFiles, CollectRelativeFiles(workspace.Path, inventoryTree.Root), "inventory tree");
		AssertFileSetEqual(expectedFiles, CollectRelativeFiles(workspace.Path, analysis.Tree.Root), "analysis");
		AssertFileSetEqual(expectedFiles, CollectRelativeFiles(workspace.Path, contextPlan.EffectiveTree), "context tree");
		AssertFileSetEqual(
			expectedFiles,
			NormalizeRelativeFiles(workspace.Path, contextPlan.IncludedFiles),
			"context included files");
	}

	private static TemporaryDirectory CreateMixedStackWorkspace()
	{
		var workspace = new TemporaryDirectory();
		workspace.CreateFile(".gitignore", "ignored-by-git.cs\n");
		workspace.CreateFile("source files/package.json", "{}\n");
		workspace.CreateFile("source files/src/Program.cs", "public static class Program {}\n");
		workspace.CreateFile("source files/src/-leading.cs", "public sealed class Leading {}\n");
		workspace.CreateFile("source files/src/файл.cs", "public sealed class Cyrillic {}\n");
		workspace.CreateFile("source files/src/文档.md", "# CJK\n");
		workspace.CreateFile("source files/src/notes.tmp", "extension filtered\n");
		workspace.CreateFile("source files/src/ignored-by-git.cs", "git ignored\n");
		workspace.CreateFile("source files/src/empty.cs", string.Empty);
		workspace.CreateFile("source files/.secret.cs", "dot file\n");
		workspace.CreateFile("source files/.git/secret.cs", "administrative metadata\n");
		workspace.CreateDirectory("source files/empty-dir");
		workspace.CreateFile("source files/build/docs/handwritten.cs", "public sealed class Handwritten {}\n");
		workspace.CreateFile("source files/dist/manifest.json", "{}\n");
		workspace.CreateFile("source files/services/api/Api.csproj", "<Project />\n");
		workspace.CreateFile("source files/services/api/dist/contracts.json", "{\"source\":true}\n");
		workspace.CreateFile("source files/services/api/obj/project.assets.json", "{}\n");

		workspace.CreateFile("кириллица/pyproject.toml", "[project]\nname = \"fixture\"\n");
		workspace.CreateFile("кириллица/src/данные.cs", "public sealed class Data {}\n");
		workspace.CreateFile("кириллица/.venv/pyvenv.cfg", "home = fixture\n");
		workspace.CreateFile("кириллица/.venv/leak.cs", "generated\n");

		workspace.CreateFile("文档/Cargo.toml", "[package]\nname = \"fixture\"\n");
		workspace.CreateFile("文档/guide.md", "# Guide\n");
		workspace.CreateFile("文档/target/debug/app", "binary\n");
		workspace.CreateFile("文档/target/leak.md", "generated\n");

		workspace.CreateFile("-leading-root/go.mod", "module example.test/fixture\n");
		workspace.CreateFile("-leading-root/-entry.cs", "public sealed class Entry {}\n");
		workspace.CreateFile("-leading-root/vendor/modules.txt", "generated\n");
		workspace.CreateFile("-leading-root/vendor/leak.cs", "generated\n");
		workspace.CreateFile("not selected/should-not-appear.cs", "public sealed class Outside {}\n");
		return workspace;
	}

	private static ProjectAnalysisService CreateProjectAnalysisService() =>
		new(
			new ScanOptionsUseCase(new FileSystemScanner()),
			ProjectLoadWorkflowRuntime.CreateBuildTreeUseCase(),
			new FilterOptionSelectionService(),
			ProjectLoadWorkflowRuntime.CreateIgnoreOptionsService(),
			ProjectLoadWorkflowRuntime.CreateIgnoreRulesService(),
			new TreeExportService(),
			new FileContentAnalyzer());

	private static HashSet<string> CollectRelativeFiles(string rootPath, FileSystemNode root)
	{
		var files = new List<string>();
		var pending = new Stack<FileSystemNode>();
		pending.Push(root);
		while (pending.Count > 0)
		{
			var node = pending.Pop();
			if (!node.IsDirectory)
				files.Add(node.FullPath);
			for (var index = node.Children.Count - 1; index >= 0; index--)
				pending.Push(node.Children[index]);
		}

		return NormalizeRelativeFiles(rootPath, files);
	}

	private static HashSet<string> CollectRelativeFiles(string rootPath, TreeNodeDescriptor root)
	{
		var files = new List<string>();
		var pending = new Stack<TreeNodeDescriptor>();
		pending.Push(root);
		while (pending.Count > 0)
		{
			var node = pending.Pop();
			if (!node.IsDirectory)
				files.Add(node.FullPath);
			for (var index = node.Children.Count - 1; index >= 0; index--)
				pending.Push(node.Children[index]);
		}

		return NormalizeRelativeFiles(rootPath, files);
	}

	private static HashSet<string> NormalizeRelativeFiles(
		string rootPath,
		IEnumerable<string> paths) =>
		paths
			.Select(path => Path.GetRelativePath(rootPath, path).Replace('\\', '/'))
			.ToHashSet(StringComparer.Ordinal);

	private static void AssertFileSetEqual(
		IReadOnlySet<string> expected,
		IReadOnlySet<string> actual,
		string surface)
	{
		var missing = expected.Except(actual, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
		var unexpected = actual.Except(expected, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
		Assert.True(
			missing.Length == 0 && unexpected.Length == 0,
			$"{surface} diverged. Missing: [{string.Join(", ", missing)}]. Unexpected: [{string.Join(", ", unexpected)}].");
	}
}
