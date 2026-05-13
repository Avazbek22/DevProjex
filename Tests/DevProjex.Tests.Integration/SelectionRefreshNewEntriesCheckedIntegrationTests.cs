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

	private static void SeedInitialWorkspace(TemporaryDirectory temp)
	{
		temp.CreateFile("src/App.cs", "class App {}");
		temp.CreateFile("docs/notes.txt", "notes");
		temp.CreateFile("Program.cs", "class Program {}");
		temp.CreateFile("data.csv", "id,value");
		temp.CreateFile("empty.txt", string.Empty);
	}

	private static void MutateWorkspaceBeforeRefresh(TemporaryDirectory temp)
	{
		temp.CreateFile("generated/readme.md", "# Generated");
		temp.CreateFile("new-data.csv", "2,updated");
		temp.CreateFile("new-empty.log", string.Empty);
		temp.CreateFile(".env", "APP_ENV=test");
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

	private static void AssertSelection(
		IReadOnlyList<SelectionOption> options,
		string name,
		bool expectedChecked)
	{
		var option = Assert.Single(options.Where(option =>
			string.Equals(option.Name, name, StringComparison.OrdinalIgnoreCase)));
		Assert.Equal(expectedChecked, option.IsChecked);
	}

	private static void AssertIgnoreOption(
		SelectionRefreshSnapshot snapshot,
		IgnoreOptionId optionId,
		bool expectedChecked)
	{
		var option = Assert.Single(snapshot.IgnoreOptions.Where(option => option.Id == optionId));
		Assert.Equal(expectedChecked, option.IsChecked);
		Assert.True(snapshot.IgnoreOptionStateCache.TryGetValue(optionId, out var cachedState));
		Assert.Equal(expectedChecked, cachedState);
	}
}
