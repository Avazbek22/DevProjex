using DevProjex.Application.Models;
using DevProjex.Tests.Shared.ProjectLoadWorkflow;

namespace DevProjex.Tests.Integration;

public sealed class SelectionRefreshNewEntriesCheckedIntegrationTests
{
	[Fact]
	public void FullRefresh_NewRootExtensionAndIgnoreOptionAreCheckedWhileKnownUncheckedEntriesStayUnchecked()
	{
		using var temp = new TemporaryDirectory();
		SeedInitialWorkspace(temp);

		var services = ProjectLoadWorkflowRefreshHarness.CreateServices();
		var baseline = services.Engine.ComputeFullRefreshSnapshot(
			ProjectLoadWorkflowRefreshHarness.CreateDefaultContext(temp.Path),
			CancellationToken.None);
		var manualContext = CreateManualSubsetContext(temp.Path, baseline);

		MutateWorkspaceBeforeRefresh(temp);

		var snapshot = services.Engine.ComputeFullRefreshSnapshot(manualContext, CancellationToken.None);

		AssertSelection(snapshot.RootOptions!, "src", expectedChecked: true);
		AssertSelection(snapshot.RootOptions!, "docs", expectedChecked: false);
		AssertSelection(snapshot.RootOptions!, "generated", expectedChecked: true);

		AssertSelection(snapshot.ExtensionOptions, ".cs", expectedChecked: true);
		AssertSelection(snapshot.ExtensionOptions, ".csv", expectedChecked: false);
		AssertSelection(snapshot.ExtensionOptions, ".md", expectedChecked: true);
		AssertSelection(snapshot.ExtensionOptions, ".log", expectedChecked: true);

		AssertIgnoreOption(snapshot, IgnoreOptionId.EmptyFiles, expectedChecked: false);
		AssertIgnoreOption(snapshot, IgnoreOptionId.DotFiles, expectedChecked: true);

		var converged = services.Engine.ComputeFullRefreshSnapshot(
			ProjectLoadWorkflowRefreshHarness.CreateContextFromSnapshot(temp.Path, snapshot),
			CancellationToken.None);
		ProjectLoadWorkflowRefreshHarness.AssertEquivalentSnapshots(snapshot, converged);
	}

	[Fact]
	public void FullRefresh_NewEntriesAreCheckedEvenWhenAllTogglesWerePreviouslyCleared()
	{
		using var temp = new TemporaryDirectory();
		SeedInitialWorkspace(temp);

		var services = ProjectLoadWorkflowRefreshHarness.CreateServices();
		var baseline = services.Engine.ComputeFullRefreshSnapshot(
			ProjectLoadWorkflowRefreshHarness.CreateDefaultContext(temp.Path),
			CancellationToken.None);
		var allKnownOffContext = CreateAllKnownEntriesOffContext(temp.Path, baseline);

		MutateWorkspaceBeforeRefresh(temp);

		var snapshot = services.Engine.ComputeFullRefreshSnapshot(allKnownOffContext, CancellationToken.None);

		AssertSelection(snapshot.RootOptions!, "src", expectedChecked: false);
		AssertSelection(snapshot.RootOptions!, "docs", expectedChecked: false);
		AssertSelection(snapshot.RootOptions!, "generated", expectedChecked: true);

		AssertSelection(snapshot.ExtensionOptions, ".cs", expectedChecked: false);
		AssertSelection(snapshot.ExtensionOptions, ".csv", expectedChecked: false);
		AssertSelection(snapshot.ExtensionOptions, ".md", expectedChecked: true);
		AssertSelection(snapshot.ExtensionOptions, ".log", expectedChecked: true);

		AssertIgnoreOption(snapshot, IgnoreOptionId.EmptyFiles, expectedChecked: false);
		AssertIgnoreOption(snapshot, IgnoreOptionId.DotFiles, expectedChecked: true);
	}

	[Fact]
	public void FullRefresh_FirstEntriesAreCheckedAfterPreviouslyEmptySections()
	{
		using var temp = new TemporaryDirectory();

		var services = ProjectLoadWorkflowRefreshHarness.CreateServices();
		var baseline = services.Engine.ComputeFullRefreshSnapshot(
			ProjectLoadWorkflowRefreshHarness.CreateDefaultContext(temp.Path),
			CancellationToken.None);
		var emptyInitializedContext = CreateEmptyInitializedContext(temp.Path, baseline);

		temp.CreateFile("src/App.cs", "class App {}");
		temp.CreateFile(".env", "APP_ENV=test");
		temp.CreateFile("empty.txt", string.Empty);

		var snapshot = services.Engine.ComputeFullRefreshSnapshot(emptyInitializedContext, CancellationToken.None);

		AssertSelection(snapshot.RootOptions!, "src", expectedChecked: true);
		AssertSelection(snapshot.ExtensionOptions, ".cs", expectedChecked: true);
		AssertSelection(snapshot.ExtensionOptions, ".txt", expectedChecked: true);
		AssertIgnoreOption(snapshot, IgnoreOptionId.DotFiles, expectedChecked: true);
		AssertIgnoreOption(snapshot, IgnoreOptionId.EmptyFiles, expectedChecked: true);
	}

	[Fact]
	public void FullRefresh_DeepPolyglotMutation_PreservesKnownStateAndChecksNewEntriesAcrossAllSections()
	{
		using var temp = new TemporaryDirectory();
		SeedDeepPolyglotWorkspace(temp);

		var services = ProjectLoadWorkflowRefreshHarness.CreateServices();
		var baseline = services.Engine.ComputeFullRefreshSnapshot(
			ProjectLoadWorkflowRefreshHarness.CreateDefaultContext(temp.Path),
			CancellationToken.None);
		var manualContext = CreateDeepPolyglotManualContext(temp.Path, baseline);

		MutateDeepPolyglotWorkspace(temp);

		var snapshot = services.Engine.ComputeFullRefreshSnapshot(manualContext, CancellationToken.None);

		AssertSelection(snapshot.RootOptions!, "api", expectedChecked: true);
		AssertSelection(snapshot.RootOptions!, "web", expectedChecked: true);
		AssertSelection(snapshot.RootOptions!, "docs", expectedChecked: false);
		AssertSelection(snapshot.RootOptions!, "generated", expectedChecked: true);
		AssertSelection(snapshot.RootOptions!, "worker", expectedChecked: true);

		AssertSelection(snapshot.ExtensionOptions, ".cs", expectedChecked: true);
		AssertSelection(snapshot.ExtensionOptions, ".ts", expectedChecked: true);
		AssertSelection(snapshot.ExtensionOptions, ".md", expectedChecked: false);
		AssertSelection(snapshot.ExtensionOptions, ".csv", expectedChecked: false);
		AssertSelection(snapshot.ExtensionOptions, ".log", expectedChecked: true);
		AssertSelection(snapshot.ExtensionOptions, ".py", expectedChecked: true);

		AssertIgnoreOption(snapshot, IgnoreOptionId.EmptyFiles, expectedChecked: false);
		AssertIgnoreOption(snapshot, IgnoreOptionId.DotFiles, expectedChecked: false);
		AssertIgnoreStateCache(snapshot, IgnoreOptionId.DotFolders, expectedChecked: true);
		AssertIgnoreOption(snapshot, IgnoreOptionId.SmartIgnore, expectedChecked: true);
		AssertIgnoreOption(snapshot, IgnoreOptionId.UseGitIgnore, expectedChecked: true);

		var tree = BuildTreeFromSnapshot(temp.Path, snapshot);
		AssertPathVisible(tree, "api/src/Program.cs");
		AssertPathVisible(tree, "web/src/app.ts");
		AssertPathVisible(tree, "generated/reports/new-empty.log");
		AssertPathVisible(tree, "worker/app.py");
		AssertPathHidden(tree, "docs/guide.md");
		AssertPathHidden(tree, "api/bin/Debug/App.dll");
		AssertPathHidden(tree, "web/node_modules/pkg/index.js");
		AssertPathHidden(tree, "worker/__pycache__/app.pyc");
		AssertPathHidden(tree, ".idea/workspace.xml");
		AssertPathHidden(tree, "ignored-root/ignored.log");

		var converged = services.Engine.ComputeFullRefreshSnapshot(
			ProjectLoadWorkflowRefreshHarness.CreateContextFromSnapshot(temp.Path, snapshot),
			CancellationToken.None);
		ProjectLoadWorkflowRefreshHarness.AssertEquivalentSnapshots(snapshot, converged);
	}

	[Fact]
	public void FullRefresh_ExternalControllerMutation_ChecksNewControllersAndKeepsKnownUncheckedEntries()
	{
		using var temp = new TemporaryDirectory();
		SeedExternalControllerInitialWorkspace(temp);

		var services = ProjectLoadWorkflowRefreshHarness.CreateServices();
		var baseline = services.Engine.ComputeFullRefreshSnapshot(
			ProjectLoadWorkflowRefreshHarness.CreateDefaultContext(temp.Path),
			CancellationToken.None);
		var manualContext = CreateExternalControllerManualContext(temp.Path, baseline);

		MutateExternalControllersAndDynamicEntries(temp);

		var snapshot = services.Engine.ComputeFullRefreshSnapshot(manualContext, CancellationToken.None);

		AssertSelection(snapshot.RootOptions!, "src", expectedChecked: true);
		AssertSelection(snapshot.RootOptions!, "docs", expectedChecked: false);
		AssertSelection(snapshot.RootOptions!, "api", expectedChecked: true);
		AssertSelection(snapshot.RootOptions!, "web", expectedChecked: true);
		AssertSelection(snapshot.RootOptions!, "generated", expectedChecked: true);

		AssertSelection(snapshot.ExtensionOptions, ".cs", expectedChecked: true);
		AssertSelection(snapshot.ExtensionOptions, ".csv", expectedChecked: false);
		AssertSelection(snapshot.ExtensionOptions, ".ts", expectedChecked: true);
		AssertSelection(snapshot.ExtensionOptions, ".log", expectedChecked: true);

		AssertIgnoreOption(snapshot, IgnoreOptionId.EmptyFiles, expectedChecked: false);
		AssertIgnoreOption(snapshot, IgnoreOptionId.UseGitIgnore, expectedChecked: true);
		AssertIgnoreOption(snapshot, IgnoreOptionId.SmartIgnore, expectedChecked: true);
		AssertIgnoreOption(snapshot, IgnoreOptionId.DotFolders, expectedChecked: true);
		AssertIgnoreOption(snapshot, IgnoreOptionId.DotFiles, expectedChecked: true);
		AssertIgnoreOption(snapshot, IgnoreOptionId.EmptyFolders, expectedChecked: true);

		var tree = BuildTreeFromSnapshot(temp.Path, snapshot);
		AssertPathVisible(tree, "src/App.cs");
		AssertPathVisible(tree, "api/src/Program.cs");
		AssertPathVisible(tree, "web/src/app.ts");
		AssertPathVisible(tree, "generated/report.log");
		AssertPathHidden(tree, "docs/notes.md");
		AssertPathHidden(tree, "data.csv");
		AssertPathHidden(tree, "api/logs/runtime.log");
		AssertPathHidden(tree, "web/node_modules/pkg/index.js");
		AssertPathHidden(tree, ".idea/workspace.xml");
		AssertPathHidden(tree, ".env");
		AssertPathHidden(tree, "empty-root");

		var converged = services.Engine.ComputeFullRefreshSnapshot(
			ProjectLoadWorkflowRefreshHarness.CreateContextFromSnapshot(temp.Path, snapshot),
			CancellationToken.None);
		ProjectLoadWorkflowRefreshHarness.AssertEquivalentSnapshots(snapshot, converged);
	}

	[Fact]
	public void FullRefresh_ProfileWithUnavailableExtensions_RescansCountsAfterFallback()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("src/App.cs", "class App {}");
		temp.CreateFile("src/empty.cs", string.Empty);
		temp.CreateFile("docs/readme.md", "# Readme");

		var services = ProjectLoadWorkflowRefreshHarness.CreateServices();
		var baseline = services.Engine.ComputeFullRefreshSnapshot(
			ProjectLoadWorkflowRefreshHarness.CreateDefaultContext(temp.Path),
			CancellationToken.None);
		var context = CreateProfileWithUnavailableExtensionsContext(temp.Path, baseline);

		var snapshot = services.Engine.ComputeFullRefreshSnapshot(context, CancellationToken.None);

		AssertSelection(snapshot.ExtensionOptions, ".cs", expectedChecked: true);
		AssertSelection(snapshot.ExtensionOptions, ".md", expectedChecked: true);
		AssertIgnoreOption(snapshot, IgnoreOptionId.EmptyFiles, expectedChecked: true);
		Assert.True(snapshot.IgnoreOptionCounts.EmptyFiles > 0);

		var tree = BuildTreeFromSnapshot(temp.Path, snapshot);
		AssertPathVisible(tree, "src/App.cs");
		AssertPathHidden(tree, "src/empty.cs");

		var converged = services.Engine.ComputeFullRefreshSnapshot(
			ProjectLoadWorkflowRefreshHarness.CreateContextFromSnapshot(temp.Path, snapshot),
			CancellationToken.None);
		ProjectLoadWorkflowRefreshHarness.AssertEquivalentSnapshots(snapshot, converged);
	}

	private static void SeedInitialWorkspace(TemporaryDirectory temp)
	{
		temp.CreateFile("src/App.cs", "class App {}");
		temp.CreateFile("docs/notes.txt", "notes");
		temp.CreateFile("Program.cs", "class Program {}");
		temp.CreateFile("data.csv", "id,value");
		temp.CreateFile("empty.txt", string.Empty);
	}

	private static void SeedExternalControllerInitialWorkspace(TemporaryDirectory temp)
	{
		temp.CreateFile("src/App.cs", "class App {}");
		temp.CreateFile("docs/notes.md", "# Notes");
		temp.CreateFile("data.csv", "id,value");
		temp.CreateFile("empty.txt", string.Empty);
	}

	private static void MutateExternalControllersAndDynamicEntries(TemporaryDirectory temp)
	{
		temp.CreateFile("api/.gitignore", "logs/\n");
		temp.CreateFile("api/App.csproj", "<Project />");
		temp.CreateFile("api/src/Program.cs", "class Program {}");
		temp.CreateFile("api/logs/runtime.log", "ignored by nested gitignore");
		temp.CreateFile("web/package.json", "{}");
		temp.CreateFile("web/src/app.ts", "export const app = true;");
		temp.CreateFile("web/node_modules/pkg/index.js", "module.exports = {};");
		temp.CreateFile("generated/report.log", "new visible log");
		temp.CreateFile("new-data.csv", "2,updated");
		temp.CreateFile(".idea/workspace.xml", "<project />");
		temp.CreateFile(".env", "APP_ENV=test");
		temp.CreateDirectory("empty-root");
	}

	private static void MutateWorkspaceBeforeRefresh(TemporaryDirectory temp)
	{
		temp.CreateFile("generated/readme.md", "# Generated");
		temp.CreateFile("new-data.csv", "2,updated");
		temp.CreateFile("new-empty.log", string.Empty);
		temp.CreateFile(".env", "APP_ENV=test");
	}

	private static void SeedDeepPolyglotWorkspace(TemporaryDirectory temp)
	{
		temp.CreateFile(".gitignore", "ignored-root/\n*.tmp\n");
		temp.CreateFile("api/App.csproj", "<Project />");
		temp.CreateFile("api/src/Program.cs", "class Program {}");
		temp.CreateFile("api/docs/design.md", "# Design");
		temp.CreateFile("api/.config/settings.cs", "class Settings {}");
		temp.CreateFile("api/bin/Debug/App.dll", "binary");
		temp.CreateFile("api/obj/cache.tmp", "cache");
		temp.CreateFile("web/package.json", "{}");
		temp.CreateFile("web/src/app.ts", "export const app = 1;");
		temp.CreateFile("web/node_modules/pkg/index.js", "module.exports = {};");
		temp.CreateFile("docs/guide.md", "# Guide");
		temp.CreateFile("data.csv", "id,value");
		temp.CreateFile("empty.txt", string.Empty);
		temp.CreateFile(".env", "APP_ENV=dev");
		temp.CreateFile(".idea/workspace.xml", "<project />");
		temp.CreateFile("ignored-root/ignored.log", "ignored");
	}

	private static void MutateDeepPolyglotWorkspace(TemporaryDirectory temp)
	{
		temp.CreateFile("generated/reports/new-empty.log", string.Empty);
		temp.CreateFile("generated/reports/summary.log", "summary");
		temp.CreateFile("worker/requirements.txt", "pytest\n");
		temp.CreateFile("worker/app.py", "print('ok')");
		temp.CreateFile("worker/__pycache__/app.pyc", "binary");
		temp.CreateFile("docs/new-guide.md", "# New guide");
		temp.CreateFile("new-data.csv", "2,updated");
		temp.CreateFile(".idea/tasks.xml", "<tasks />");
	}

	private static SelectionRefreshContext CreateManualSubsetContext(
		string rootPath,
		SelectionRefreshSnapshot baseline)
	{
		var rootStates = ProjectLoadWorkflowRefreshHarness.BuildRootOptionStateCache(baseline);
		rootStates["src"] = true;
		rootStates["docs"] = false;

		var extensionStates = ProjectLoadWorkflowRefreshHarness.BuildExtensionOptionStateCache(baseline);
		extensionStates[".cs"] = true;
		extensionStates[".csv"] = false;
		extensionStates[".txt"] = true;

		var ignoreStates = new Dictionary<IgnoreOptionId, bool>(baseline.IgnoreOptionStateCache)
		{
			[IgnoreOptionId.EmptyFiles] = false
		};

		return ProjectLoadWorkflowRefreshHarness.CreateContextFromSnapshot(rootPath, baseline) with
		{
			AllRootFoldersChecked = false,
			RootSelectionInitialized = true,
			RootSelectionCache = new HashSet<string>(PathComparer.Default) { "src" },
			RootOptionStateCache = rootStates,
			AllExtensionsChecked = false,
			ExtensionsSelectionInitialized = true,
			ExtensionsSelectionCache = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs", ".txt" },
			ExtensionOptionStateCache = extensionStates,
			IgnoreSelectionInitialized = true,
			IgnoreSelectionCache = ignoreStates
				.Where(static pair => pair.Value)
				.Select(static pair => pair.Key)
				.ToHashSet(),
			IgnoreOptionStateCache = ignoreStates,
			IgnoreAllPreference = null
		};
	}

	private static SelectionRefreshContext CreateExternalControllerManualContext(
		string rootPath,
		SelectionRefreshSnapshot baseline)
	{
		var rootStates = ProjectLoadWorkflowRefreshHarness.BuildRootOptionStateCache(baseline);
		rootStates["src"] = true;
		rootStates["docs"] = false;

		var extensionStates = ProjectLoadWorkflowRefreshHarness.BuildExtensionOptionStateCache(baseline);
		extensionStates[".cs"] = true;
		extensionStates[".md"] = true;
		extensionStates[".csv"] = false;

		var ignoreStates = new Dictionary<IgnoreOptionId, bool>(baseline.IgnoreOptionStateCache)
		{
			[IgnoreOptionId.EmptyFiles] = false
		};

		return ProjectLoadWorkflowRefreshHarness.CreateContextFromSnapshot(rootPath, baseline) with
		{
			AllRootFoldersChecked = false,
			RootSelectionInitialized = true,
			RootSelectionCache = new HashSet<string>(PathComparer.Default) { "src" },
			RootOptionStateCache = rootStates,
			AllExtensionsChecked = false,
			ExtensionsSelectionInitialized = true,
			ExtensionsSelectionCache = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs", ".md" },
			ExtensionOptionStateCache = extensionStates,
			IgnoreSelectionInitialized = true,
			IgnoreSelectionCache = ignoreStates
				.Where(static pair => pair.Value)
				.Select(static pair => pair.Key)
				.ToHashSet(),
			IgnoreOptionStateCache = ignoreStates,
			IgnoreAllPreference = null
		};
	}

	private static SelectionRefreshContext CreateAllKnownEntriesOffContext(
		string rootPath,
		SelectionRefreshSnapshot baseline)
	{
		var rootStates = ProjectLoadWorkflowRefreshHarness.BuildRootOptionStateCache(baseline);
		foreach (var name in rootStates.Keys.ToArray())
			rootStates[name] = false;

		var extensionStates = ProjectLoadWorkflowRefreshHarness.BuildExtensionOptionStateCache(baseline);
		foreach (var name in extensionStates.Keys.ToArray())
			extensionStates[name] = false;

		var ignoreStates = new Dictionary<IgnoreOptionId, bool>(baseline.IgnoreOptionStateCache);
		foreach (var id in ignoreStates.Keys.ToArray())
			ignoreStates[id] = false;

		return ProjectLoadWorkflowRefreshHarness.CreateContextFromSnapshot(rootPath, baseline) with
		{
			AllRootFoldersChecked = false,
			RootSelectionInitialized = true,
			RootSelectionCache = new HashSet<string>(PathComparer.Default),
			RootOptionStateCache = rootStates,
			AllExtensionsChecked = false,
			ExtensionsSelectionInitialized = true,
			ExtensionsSelectionCache = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
			ExtensionOptionStateCache = extensionStates,
			IgnoreSelectionInitialized = true,
			IgnoreSelectionCache = new HashSet<IgnoreOptionId>(),
			IgnoreOptionStateCache = ignoreStates,
			IgnoreAllPreference = false
		};
	}

	private static SelectionRefreshContext CreateEmptyInitializedContext(
		string rootPath,
		SelectionRefreshSnapshot baseline)
	{
		return ProjectLoadWorkflowRefreshHarness.CreateContextFromSnapshot(rootPath, baseline) with
		{
			AllRootFoldersChecked = false,
			RootSelectionInitialized = true,
			RootSelectionCache = new HashSet<string>(PathComparer.Default),
			RootOptionStateCache = new Dictionary<string, bool>(PathComparer.Default),
			AllExtensionsChecked = false,
			ExtensionsSelectionInitialized = true,
			ExtensionsSelectionCache = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
			ExtensionOptionStateCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase),
			IgnoreSelectionInitialized = true,
			IgnoreSelectionCache = new HashSet<IgnoreOptionId>(),
			IgnoreOptionStateCache = new Dictionary<IgnoreOptionId, bool>(),
			IgnoreAllPreference = false
		};
	}

	private static SelectionRefreshContext CreateDeepPolyglotManualContext(
		string rootPath,
		SelectionRefreshSnapshot baseline)
	{
		var rootStates = ProjectLoadWorkflowRefreshHarness.BuildRootOptionStateCache(baseline);
		rootStates["api"] = true;
		rootStates["web"] = true;
		rootStates["docs"] = false;

		var extensionStates = ProjectLoadWorkflowRefreshHarness.BuildExtensionOptionStateCache(baseline);
		extensionStates[".cs"] = true;
		extensionStates[".ts"] = true;
		extensionStates[".md"] = false;
		extensionStates[".csv"] = false;
		extensionStates[".txt"] = true;

		var ignoreStates = new Dictionary<IgnoreOptionId, bool>(baseline.IgnoreOptionStateCache)
		{
			[IgnoreOptionId.UseGitIgnore] = true,
			[IgnoreOptionId.SmartIgnore] = true,
			[IgnoreOptionId.DotFolders] = true,
			[IgnoreOptionId.DotFiles] = false,
			[IgnoreOptionId.EmptyFiles] = false
		};

		return ProjectLoadWorkflowRefreshHarness.CreateContextFromSnapshot(rootPath, baseline) with
		{
			AllRootFoldersChecked = false,
			RootSelectionInitialized = true,
			RootSelectionCache = new HashSet<string>(PathComparer.Default) { "api", "web" },
			RootOptionStateCache = rootStates,
			AllExtensionsChecked = false,
			ExtensionsSelectionInitialized = true,
			ExtensionsSelectionCache = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs", ".ts", ".txt" },
			ExtensionOptionStateCache = extensionStates,
			IgnoreSelectionInitialized = true,
			IgnoreSelectionCache = ignoreStates
				.Where(static pair => pair.Value)
				.Select(static pair => pair.Key)
				.ToHashSet(),
			IgnoreOptionStateCache = ignoreStates,
			IgnoreAllPreference = null
		};
	}

	private static SelectionRefreshContext CreateProfileWithUnavailableExtensionsContext(
		string rootPath,
		SelectionRefreshSnapshot baseline)
	{
		var ignoreStates = new Dictionary<IgnoreOptionId, bool>(baseline.IgnoreOptionStateCache)
		{
			[IgnoreOptionId.EmptyFiles] = true
		};

		return ProjectLoadWorkflowRefreshHarness.CreateContextFromSnapshot(rootPath, baseline) with
		{
			PreparedSelectionMode = PreparedSelectionMode.Profile,
			AllExtensionsChecked = false,
			ExtensionsSelectionInitialized = true,
			ExtensionsSelectionCache = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".legacy" },
			ExtensionOptionStateCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase),
			IgnoreSelectionInitialized = true,
			IgnoreSelectionCache = ignoreStates
				.Where(static pair => pair.Value)
				.Select(static pair => pair.Key)
				.ToHashSet(),
			IgnoreOptionStateCache = ignoreStates
		};
	}

	private static void AssertSelection(
		IReadOnlyList<SelectionOption> options,
		string name,
		bool expectedChecked)
	{
		var option = Assert.Single(options, option =>
			string.Equals(option.Name, name, StringComparison.OrdinalIgnoreCase));
		Assert.Equal(expectedChecked, option.IsChecked);
	}

	private static void AssertIgnoreOption(
		SelectionRefreshSnapshot snapshot,
		IgnoreOptionId optionId,
		bool expectedChecked)
	{
		var option = Assert.Single(snapshot.IgnoreOptions, option => option.Id == optionId);
		Assert.Equal(expectedChecked, option.IsChecked);
		Assert.True(snapshot.IgnoreOptionStateCache.TryGetValue(optionId, out var cachedState));
		Assert.Equal(expectedChecked, cachedState);
	}

	private static void AssertIgnoreStateCache(
		SelectionRefreshSnapshot snapshot,
		IgnoreOptionId optionId,
		bool expectedChecked)
	{
		Assert.True(snapshot.IgnoreOptionStateCache.TryGetValue(optionId, out var cachedState));
		Assert.Equal(expectedChecked, cachedState);
	}

	private static TreeBuildResult BuildTreeFromSnapshot(string rootPath, SelectionRefreshSnapshot snapshot)
	{
		var rules = ProjectLoadWorkflowRuntime.CreateIgnoreRulesService()
			.Build(
				rootPath,
				ProjectLoadWorkflowRefreshHarness.CollectCheckedIgnoreOptionIds(snapshot),
				ProjectLoadWorkflowRefreshHarness.CollectCheckedRootNames(snapshot));

		return new TreeBuilder().Build(rootPath, new TreeFilterOptions(
			AllowedExtensions: ProjectLoadWorkflowRefreshHarness.CollectCheckedExtensionNames(snapshot),
			AllowedRootFolders: ProjectLoadWorkflowRefreshHarness.CollectCheckedRootNames(snapshot),
			IgnoreRules: rules));
	}

	private static void AssertPathVisible(TreeBuildResult tree, string relativePath)
	{
		Assert.True(ContainsPath(tree, relativePath), $"Expected path '{relativePath}' to be visible.");
	}

	private static void AssertPathHidden(TreeBuildResult tree, string relativePath)
	{
		Assert.False(ContainsPath(tree, relativePath), $"Expected path '{relativePath}' to be hidden.");
	}

	private static bool ContainsPath(TreeBuildResult tree, string relativePath)
	{
		var segments = relativePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
		IReadOnlyList<FileSystemNode> current = tree.Root.Children;

		foreach (var segment in segments)
		{
			var match = current.FirstOrDefault(node => string.Equals(node.Name, segment, StringComparison.OrdinalIgnoreCase));
			if (match is null)
				return false;

			current = match.Children;
		}

		return true;
	}
}
