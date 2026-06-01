using DevProjex.Tests.Shared.ProjectLoadWorkflow;
using static DevProjex.Tests.Shared.ProjectLoadWorkflow.ProjectLoadWorkflowRefreshHarness;

namespace DevProjex.Tests.Integration;

public sealed class IgnoreOptionDeepContractMatrixIntegrationTests
{
	[Fact]
	public void MultiRootWorkspace_SelectedRootScopeCountsOnlyDotFoldersThatCanAffectCheckedRoots()
	{
		using var workspace = CreateMultiRootWorkspace();
		var services = CreateServices();

		var defaults = ComputeConvergedSnapshot(services, workspace.Path, CreateDefaultContext(workspace.Path));
		var apiOnly = ComputeConvergedSnapshot(
			services,
			workspace.Path,
			CreateSingleRootContext(workspace.Path, defaults, "api"));

		var dotFolders = AssertIgnoreOption(apiOnly, IgnoreOptionId.DotFolders, expectedVisible: true, expectedChecked: true);
		Assert.Equal(3, apiOnly.IgnoreOptionCounts.DotFolders);
		Assert.Contains("(3)", dotFolders.Label);
		Assert.DoesNotContain(apiOnly.ExtensionOptions, option => string.Equals(option.Name, ".ts", StringComparison.OrdinalIgnoreCase));

		AssertTreeState(
			workspace.Path,
			apiOnly,
			visiblePaths: ["api/src/Program.cs"],
			hiddenPaths:
			[
				"api/.idea/settings.xml",
				"api/.github/workflows/build.yml",
				"api/.run/App.run.xml",
				"web/src/app.ts",
				"docs/.config/settings.json"
			]);
	}

	[Fact]
	public void GitOwnedDotFolders_AppearCheckedWhenGitIgnoreIsDisabledAndRemainStableAcrossRoundTrip()
	{
		using var project = new TemporaryDirectory();
		project.CreateFile(".gitignore", ".idea/\n.github/\nlogs/\n");
		project.CreateFile("App.csproj", "<Project />\n");
		project.CreateFile("src/Program.cs", "Console.WriteLine(\"ok\");\n");
		project.CreateFile(".idea/settings.xml", "<settings />\n");
		project.CreateFile(".github/workflows/build.yml", "name: build\n");
		project.CreateFile("logs/runtime.log", "ignored\n");

		var services = CreateServices();
		var defaults = ComputeConvergedSnapshot(services, project.Path, CreateDefaultContext(project.Path));

		AssertIgnoreOption(defaults, IgnoreOptionId.UseGitIgnore, expectedVisible: true, expectedChecked: true);
		AssertIgnoreOption(defaults, IgnoreOptionId.DotFolders, expectedVisible: false, expectedChecked: null);
		AssertTreeState(
			project.Path,
			defaults,
			visiblePaths: ["src/Program.cs"],
			hiddenPaths:
			[
				".idea/settings.xml",
				".github/workflows/build.yml",
				"logs/runtime.log"
			]);

		var gitOff = ComputeConvergedSnapshot(
			services,
			project.Path,
			CreateForcedIgnoreContext(project.Path, defaults, new Dictionary<IgnoreOptionId, bool>
			{
				[IgnoreOptionId.UseGitIgnore] = false
			}));

		AssertIgnoreOption(gitOff, IgnoreOptionId.UseGitIgnore, expectedVisible: true, expectedChecked: false);
		var dotFolders = AssertIgnoreOption(gitOff, IgnoreOptionId.DotFolders, expectedVisible: true, expectedChecked: true);
		Assert.Equal(2, gitOff.IgnoreOptionCounts.DotFolders);
		Assert.Contains("(2)", dotFolders.Label);
		AssertTreeState(
			project.Path,
			gitOff,
			visiblePaths:
			[
				"src/Program.cs",
				"logs/runtime.log"
			],
			hiddenPaths:
			[
				".idea/settings.xml",
				".github/workflows/build.yml"
			]);

		var dotOff = ComputeConvergedSnapshot(
			services,
			project.Path,
			CreateForcedIgnoreContext(project.Path, gitOff, new Dictionary<IgnoreOptionId, bool>
			{
				[IgnoreOptionId.UseGitIgnore] = false,
				[IgnoreOptionId.DotFolders] = false
			}));

		AssertIgnoreOption(dotOff, IgnoreOptionId.UseGitIgnore, expectedVisible: true, expectedChecked: false);
		AssertIgnoreOption(dotOff, IgnoreOptionId.DotFolders, expectedVisible: true, expectedChecked: false);
		AssertTreeState(
			project.Path,
			dotOff,
			visiblePaths:
			[
				"src/Program.cs",
				"logs/runtime.log",
				".idea/settings.xml",
				".github/workflows/build.yml"
			],
			hiddenPaths: []);
	}

	[Theory]
	[MemberData(nameof(ControllerPowerSet))]
	public void MixedControllerWorkspace_EveryControllerCombinationProducesPredictableTree(
		bool gitIgnoreChecked,
		bool smartIgnoreChecked,
		bool dotFoldersChecked)
	{
		using var workspace = CreateControllerPowerSetWorkspace();
		var services = CreateServices();

		var defaults = ComputeConvergedSnapshot(services, workspace.Path, CreateDefaultContext(workspace.Path));
		var snapshot = ComputeConvergedSnapshot(
			services,
			workspace.Path,
			CreateForcedIgnoreContext(workspace.Path, defaults, new Dictionary<IgnoreOptionId, bool>
			{
				[IgnoreOptionId.UseGitIgnore] = gitIgnoreChecked,
				[IgnoreOptionId.SmartIgnore] = smartIgnoreChecked,
				[IgnoreOptionId.DotFolders] = dotFoldersChecked
			}));

		AssertIgnoreOption(snapshot, IgnoreOptionId.UseGitIgnore, expectedVisible: true, expectedChecked: gitIgnoreChecked);
		AssertIgnoreOption(snapshot, IgnoreOptionId.SmartIgnore, expectedVisible: true, expectedChecked: smartIgnoreChecked);
		AssertIgnoreOption(snapshot, IgnoreOptionId.DotFolders, expectedVisible: true, expectedChecked: dotFoldersChecked);
		Assert.True(snapshot.ControllerImpactCounts.GitIgnore > 0);
		Assert.True(snapshot.ControllerImpactCounts.SmartIgnore > 0);
		Assert.Equal(1, snapshot.IgnoreOptionCounts.DotFolders);

		AssertTreeState(
			workspace.Path,
			snapshot,
			visiblePaths:
			[
				"api/src/Program.cs",
				.. ExpectedPathVisibility("api/logs/runtime.log", hidden: gitIgnoreChecked).Visible,
				.. ExpectedPathVisibility("web/node_modules/pkg/index.js", hidden: smartIgnoreChecked).Visible,
				.. ExpectedPathVisibility("api/.idea/settings.xml", hidden: dotFoldersChecked).Visible
			],
			hiddenPaths:
			[
				.. ExpectedPathVisibility("api/logs/runtime.log", hidden: gitIgnoreChecked).Hidden,
				.. ExpectedPathVisibility("web/node_modules/pkg/index.js", hidden: smartIgnoreChecked).Hidden,
				.. ExpectedPathVisibility("api/.idea/settings.xml", hidden: dotFoldersChecked).Hidden
			]);
	}

	public static IEnumerable<object[]> ControllerPowerSet()
	{
		foreach (var gitIgnoreChecked in new[] { false, true })
		foreach (var smartIgnoreChecked in new[] { false, true })
		foreach (var dotFoldersChecked in new[] { false, true })
			yield return [gitIgnoreChecked, smartIgnoreChecked, dotFoldersChecked];
	}

	private static TemporaryDirectory CreateMultiRootWorkspace()
	{
		var workspace = new TemporaryDirectory();
		workspace.CreateFile("api/.gitignore", "logs/\n");
		workspace.CreateFile("api/App.csproj", "<Project />\n");
		workspace.CreateFile("api/src/Program.cs", "Console.WriteLine(\"ok\");\n");
		workspace.CreateFile("api/bin/Debug/app.dll", "binary\n");
		workspace.CreateFile("api/logs/runtime.log", "ignored\n");
		workspace.CreateFile("api/.idea/settings.xml", "<settings />\n");
		workspace.CreateFile("api/.github/workflows/build.yml", "name: build\n");
		workspace.CreateFile("api/.run/App.run.xml", "<component />\n");
		workspace.CreateFile("web/package.json", "{}\n");
		workspace.CreateFile("web/src/app.ts", "export const ok = true;\n");
		workspace.CreateFile("web/node_modules/pkg/index.js", "module.exports = {};\n");
		workspace.CreateFile("web/.idea/settings.xml", "<settings />\n");
		workspace.CreateFile("web/.vscode/settings.json", "{}\n");
		workspace.CreateFile("docs/README.md", "# docs\n");
		workspace.CreateFile("docs/.config/settings.json", "{}\n");
		return workspace;
	}

	private static TemporaryDirectory CreateControllerPowerSetWorkspace()
	{
		var workspace = new TemporaryDirectory();
		workspace.CreateFile("api/.gitignore", "logs/\n");
		workspace.CreateFile("api/App.csproj", "<Project />\n");
		workspace.CreateFile("api/src/Program.cs", "Console.WriteLine(\"ok\");\n");
		workspace.CreateFile("api/logs/runtime.log", "ignored\n");
		workspace.CreateFile("api/.idea/settings.xml", "<settings />\n");
		workspace.CreateFile("web/package.json", "{}\n");
		workspace.CreateFile("web/src/app.ts", "export const ok = true;\n");
		workspace.CreateFile("web/node_modules/pkg/index.js", "module.exports = {};\n");
		return workspace;
	}

	private static SelectionRefreshSnapshot ComputeConvergedSnapshot(
		ProjectLoadWorkflowRefreshHarness.WorkflowServices services,
		string rootPath,
		SelectionRefreshContext context)
	{
		var first = services.Engine.ComputeFullRefreshSnapshot(context, TestContext.Current.CancellationToken);
		var second = services.Engine.ComputeFullRefreshSnapshot(
			CreateContextFromSnapshot(rootPath, first),
			TestContext.Current.CancellationToken);

		AssertEquivalentSnapshots(first, second);
		return second;
	}

	private static SelectionRefreshContext CreateSingleRootContext(
		string rootPath,
		SelectionRefreshSnapshot snapshot,
		string rootName)
	{
		var rootStates = snapshot.RootOptions?.ToDictionary(
			option => option.Name,
			option => string.Equals(option.Name, rootName, StringComparison.Ordinal),
			PathComparer.Default) ?? new Dictionary<string, bool>(PathComparer.Default);

		return CreateContextFromSnapshot(rootPath, snapshot) with
		{
			AllRootFoldersChecked = false,
			AllExtensionsChecked = true,
			RootSelectionInitialized = true,
			RootSelectionCache = new HashSet<string>(PathComparer.Default) { rootName },
			RootOptionStateCache = rootStates,
			ExtensionsSelectionInitialized = false,
			ExtensionsSelectionCache = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
			ExtensionOptionStateCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
		};
	}

	private static SelectionRefreshContext CreateForcedIgnoreContext(
		string rootPath,
		SelectionRefreshSnapshot snapshot,
		IReadOnlyDictionary<IgnoreOptionId, bool> forcedStates)
	{
		var selected = CollectCheckedIgnoreOptionIds(snapshot);
		var stateCache = new Dictionary<IgnoreOptionId, bool>(snapshot.IgnoreOptionStateCache);

		foreach (var (optionId, isChecked) in forcedStates)
		{
			stateCache[optionId] = isChecked;
			if (isChecked)
				selected.Add(optionId);
			else
				selected.Remove(optionId);
		}

		return CreateContextFromSnapshot(rootPath, snapshot) with
		{
			IgnoreSelectionInitialized = true,
			IgnoreSelectionCache = selected,
			IgnoreOptionStateCache = stateCache,
			IgnoreAllPreference = null
		};
	}

	private static TreeBuildResult BuildTreeFromSnapshot(string rootPath, SelectionRefreshSnapshot snapshot)
	{
		var rules = ProjectLoadWorkflowRuntime.CreateIgnoreRulesService()
			.Build(rootPath, CollectCheckedIgnoreOptionIds(snapshot), CollectCheckedRootNames(snapshot));

		return new TreeBuilder().Build(rootPath, new TreeFilterOptions(
			AllowedExtensions: CollectCheckedExtensionNames(snapshot),
			AllowedRootFolders: CollectCheckedRootNames(snapshot),
			IgnoreRules: rules));
	}

	private static void AssertTreeState(
		string rootPath,
		SelectionRefreshSnapshot snapshot,
		IReadOnlyCollection<string> visiblePaths,
		IReadOnlyCollection<string> hiddenPaths)
	{
		var tree = BuildTreeFromSnapshot(rootPath, snapshot);
		foreach (var visiblePath in visiblePaths)
			Assert.True(ContainsPath(tree.Root, visiblePath), $"Expected path '{visiblePath}' to be visible.");
		foreach (var hiddenPath in hiddenPaths)
			Assert.False(ContainsPath(tree.Root, hiddenPath), $"Expected path '{hiddenPath}' to be hidden.");
	}

	private static bool ContainsPath(FileSystemNode root, string relativePath)
	{
		var current = root;
		foreach (var segment in relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries))
		{
			var next = current.Children.FirstOrDefault(child => string.Equals(child.Name, segment, StringComparison.Ordinal));
			if (next is null)
				return false;

			current = next;
		}

		return true;
	}

	private static ResolvedIgnoreOptionState AssertIgnoreOption(
		SelectionRefreshSnapshot snapshot,
		IgnoreOptionId optionId,
		bool expectedVisible,
		bool? expectedChecked)
	{
		var options = snapshot.IgnoreOptions.Where(option => option.Id == optionId).ToArray();
		if (!expectedVisible)
		{
			Assert.Empty(options);
			return default;
		}

		Assert.Single(options);
		if (expectedChecked.HasValue)
			Assert.Equal(expectedChecked.Value, options[0].IsChecked);

		return options[0];
	}

	private static (string[] Visible, string[] Hidden) ExpectedPathVisibility(string path, bool hidden) =>
		hidden ? ([], [path]) : ([path], []);
}
