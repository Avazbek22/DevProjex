using DevProjex.Avalonia.Coordinators;
using DevProjex.Tests.Shared.ProjectLoadWorkflow;
using static DevProjex.Tests.Shared.ProjectLoadWorkflow.ProjectLoadWorkflowRefreshHarness;

namespace DevProjex.Tests.Integration;

public sealed class ProjectProfileSmartGitDotRoundTripIntegrationTests
{
	[Fact]
	public void PythonProfile_SmartOnDotOff_RestoresIndependentIgnoreState()
	{
		using var project = new TemporaryDirectory();
		project.CreateFile("pyproject.toml", "[project]\nname = \"profile-python\"\n");
		project.CreateFile("src/app.py", "print('ok')\n");
		project.CreateFile("src/__pycache__/app.pyc", "binary");
		project.CreateFile(".idea/workspace.xml", "<project />\n");

		var snapshot = ComputeSnapshotWithPersistedIgnoreProfile(project.Path, [IgnoreOptionId.SmartIgnore]);

		AssertIgnoreOption(snapshot, IgnoreOptionId.SmartIgnore, expectedVisible: true, expectedChecked: true);
		AssertIgnoreOption(snapshot, IgnoreOptionId.DotFolders, expectedVisible: true, expectedChecked: false);
		AssertRootOptionVisible(snapshot, ".idea", expectedVisible: true);
		AssertExtensionVisible(snapshot, ".pyc", expectedVisible: false);
	}

	[Fact]
	public void PythonProfile_SmartOffDotOn_RestoresIndependentIgnoreState()
	{
		using var project = new TemporaryDirectory();
		project.CreateFile("pyproject.toml", "[project]\nname = \"profile-python\"\n");
		project.CreateFile("src/app.py", "print('ok')\n");
		project.CreateFile("src/__pycache__/app.pyc", "binary");
		project.CreateFile(".idea/workspace.xml", "<project />\n");

		var snapshot = ComputeSnapshotWithPersistedIgnoreProfile(project.Path, [IgnoreOptionId.DotFolders]);

		AssertIgnoreOption(snapshot, IgnoreOptionId.SmartIgnore, expectedVisible: true, expectedChecked: false);
		AssertIgnoreOption(snapshot, IgnoreOptionId.DotFolders, expectedVisible: true, expectedChecked: true);
		AssertRootOptionVisible(snapshot, ".idea", expectedVisible: false);
		AssertExtensionVisible(snapshot, ".pyc", expectedVisible: true);
	}

	[Fact]
	public void GitProfile_GitOffDotOff_RestoresSingleGitControllerState()
	{
		using var project = new TemporaryDirectory();
		project.CreateFile(".gitignore", "logs/\n");
		project.CreateFile("pyproject.toml", "[project]\nname = \"profile-git-python\"\n");
		project.CreateFile("src/app.py", "print('ok')\n");
		project.CreateFile("logs/app.log", "git ignored\n");
		project.CreateFile(".idea/workspace.xml", "<project />\n");

		var snapshot = ComputeSnapshotWithPersistedIgnoreProfile(project.Path, []);

		AssertIgnoreOption(snapshot, IgnoreOptionId.UseGitIgnore, expectedVisible: true, expectedChecked: false);
		AssertIgnoreOption(snapshot, IgnoreOptionId.SmartIgnore, expectedVisible: false, expectedChecked: null);
		AssertIgnoreOption(snapshot, IgnoreOptionId.DotFolders, expectedVisible: true, expectedChecked: false);
		AssertRootOptionVisible(snapshot, ".idea", expectedVisible: true);
		AssertExtensionVisible(snapshot, ".log", expectedVisible: true);
	}

	[Fact]
	public void MixedWorkspaceProfile_GitOnSmartOffDotOff_RestoresControllerStatePerScope()
	{
		using var project = new TemporaryDirectory();
		project.CreateFile("api/.gitignore", "logs/\n");
		project.CreateFile("api/App.csproj", "<Project />\n");
		project.CreateFile("api/Program.cs", "Console.WriteLine(\"ok\");\n");
		project.CreateFile("api/logs/runtime.log", "git ignored\n");
		project.CreateFile("web/package.json", "{ \"name\": \"web\" }\n");
		project.CreateFile("web/src/app.ts", "export const ok = true;\n");
		project.CreateFile("web/node_modules/pkg/generated.noise", "smart ignored\n");
		project.CreateFile(".idea/workspace.xml", "<project />\n");

		var snapshot = ComputeSnapshotWithPersistedIgnoreProfile(project.Path, [IgnoreOptionId.UseGitIgnore]);

		AssertIgnoreOption(snapshot, IgnoreOptionId.UseGitIgnore, expectedVisible: true, expectedChecked: true);
		AssertIgnoreOption(snapshot, IgnoreOptionId.SmartIgnore, expectedVisible: true, expectedChecked: false);
		AssertIgnoreOption(snapshot, IgnoreOptionId.DotFolders, expectedVisible: true, expectedChecked: false);
		AssertRootOptionVisible(snapshot, ".idea", expectedVisible: true);
		AssertExtensionVisible(snapshot, ".log", expectedVisible: false);
		AssertExtensionVisible(snapshot, ".noise", expectedVisible: true);
		AssertExtensionVisible(snapshot, ".xml", expectedVisible: true);
	}

	private static SelectionRefreshSnapshot ComputeSnapshotWithPersistedIgnoreProfile(
		string projectPath,
		IReadOnlyCollection<IgnoreOptionId> selectedIgnoreOptions)
	{
		using var appData = new TemporaryDirectory();
		var store = new ProjectProfileStore(() => appData.Path);
		var services = CreateServices();
		var baseline = services.Engine.ComputeFullRefreshSnapshot(
			CreateDefaultContext(projectPath),
			CancellationToken.None);
		var sourceSnapshot = ComputeSnapshotForManualIgnoreState(
			services,
			projectPath,
			baseline,
			selectedIgnoreOptions);

		var profile = new ProjectSelectionProfile(
			SelectedRootFolders: CollectCheckedRootNames(sourceSnapshot),
			SelectedExtensions: CollectCheckedExtensionNames(sourceSnapshot),
			SelectedIgnoreOptions: selectedIgnoreOptions);
		store.SaveProfile(projectPath, profile);

		Assert.True(store.TryLoadProfile(projectPath, out var loadedProfile));
		var context = CreateContextFromSnapshot(projectPath, baseline) with
		{
			PreparedSelectionMode = PreparedSelectionMode.Profile,
			RootSelectionInitialized = true,
			RootSelectionCache = new HashSet<string>(loadedProfile.SelectedRootFolders, PathComparer.Default),
			ExtensionsSelectionInitialized = true,
			ExtensionsSelectionCache = new HashSet<string>(loadedProfile.SelectedExtensions, StringComparer.OrdinalIgnoreCase),
			IgnoreSelectionInitialized = true,
			IgnoreSelectionCache = new HashSet<IgnoreOptionId>(loadedProfile.SelectedIgnoreOptions),
			IgnoreOptionStateCache = loadedProfile.SelectedIgnoreOptions.ToDictionary(
				static optionId => optionId,
				static _ => true),
			IgnoreAllPreference = null
		};

		return services.Engine.ComputeFullRefreshSnapshot(context, CancellationToken.None);
	}

	private static SelectionRefreshSnapshot ComputeSnapshotForManualIgnoreState(
		WorkflowServices services,
		string projectPath,
		SelectionRefreshSnapshot baseline,
		IReadOnlyCollection<IgnoreOptionId> selectedIgnoreOptions)
	{
		var selected = new HashSet<IgnoreOptionId>(selectedIgnoreOptions);
		var stateCache = baseline.IgnoreOptionStateCache.ToDictionary(
			static pair => pair.Key,
			pair => selected.Contains(pair.Key));
		foreach (var optionId in selected)
			stateCache[optionId] = true;

		var context = CreateContextFromSnapshot(projectPath, baseline) with
		{
			IgnoreSelectionInitialized = true,
			IgnoreSelectionCache = selected,
			IgnoreOptionStateCache = stateCache,
			IgnoreAllPreference = null
		};

		return services.Engine.ComputeFullRefreshSnapshot(context, CancellationToken.None);
	}

	private static void AssertIgnoreOption(
		SelectionRefreshSnapshot snapshot,
		IgnoreOptionId optionId,
		bool expectedVisible,
		bool? expectedChecked)
	{
		var options = snapshot.IgnoreOptions
			.Where(candidate => candidate.Id == optionId)
			.ToArray();
		if (!expectedVisible)
		{
			Assert.Empty(options);
			return;
		}

		var option = Assert.Single(options);
		if (expectedChecked.HasValue)
			Assert.Equal(expectedChecked.Value, option.IsChecked);
	}

	private static void AssertRootOptionVisible(
		SelectionRefreshSnapshot snapshot,
		string rootName,
		bool expectedVisible)
	{
		Assert.NotNull(snapshot.RootOptions);
		var actualVisible = snapshot.RootOptions.Any(option => string.Equals(option.Name, rootName, StringComparison.Ordinal));
		Assert.Equal(expectedVisible, actualVisible);
	}

	private static void AssertExtensionVisible(
		SelectionRefreshSnapshot snapshot,
		string extension,
		bool expectedVisible)
	{
		var actualVisible = snapshot.ExtensionOptions.Any(option => string.Equals(option.Name, extension, StringComparison.OrdinalIgnoreCase));
		Assert.Equal(expectedVisible, actualVisible);
	}
}
