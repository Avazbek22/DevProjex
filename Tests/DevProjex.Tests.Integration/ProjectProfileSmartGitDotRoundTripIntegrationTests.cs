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

	[Theory]
	[MemberData(nameof(SupportedStackProfileCases))]
	public void SupportedStackProfile_SmartOffDotOn_RestoresStableIgnoreState(
		string markerPath,
		string markerContent,
		string sourcePath,
		string artifactPath,
		string artifactExtension)
	{
		using var project = new TemporaryDirectory();
		project.CreateFile(markerPath, markerContent);
		project.CreateFile(sourcePath, "visible");
		project.CreateFile(artifactPath, "smart ignored when enabled");
		project.CreateFile(".idea/workspace.xml", "<project />\n");

		var snapshot = ComputeSnapshotWithPersistedIgnoreProfile(project.Path, [IgnoreOptionId.DotFolders]);

		AssertIgnoreOption(snapshot, IgnoreOptionId.SmartIgnore, expectedVisible: true, expectedChecked: false);
		AssertIgnoreOption(snapshot, IgnoreOptionId.DotFolders, expectedVisible: true, expectedChecked: true);
		AssertRootOptionVisible(snapshot, ".idea", expectedVisible: false);
		AssertExtensionVisible(snapshot, artifactExtension, expectedVisible: true);
	}

	public static IEnumerable<object[]> SupportedStackProfileCases()
	{
		yield return ["package.json", "{}", "src/app.ts", "node_modules/pkg/generated.noise", ".noise"];
		yield return ["App.csproj", "<Project />", "Program.cs", "bin/Debug/net10.0/App.dll", ".dll"];
		yield return ["pyproject.toml", "[project]\nname = \"matrix\"\n", "src/app.py", "src/__pycache__/app.pyc", ".pyc"];
		yield return ["pom.xml", "<project />", "src/main/java/App.java", "target/classes/App.class", ".class"];
		yield return ["Cargo.toml", "[package]\nname = \"matrix\"\n", "src/lib.rs", "target/debug/libmatrix.rlib", ".rlib"];
		yield return ["go.mod", "module matrix\n", "main.go", "vendor/mod/generated.sum", ".sum"];
		yield return ["composer.json", "{}", "src/App.php", "vendor/pkg/generated.cache", ".cache"];
		yield return ["Gemfile", "source 'https://rubygems.org'\n", "app/models/user.rb", "tmp/cache.dump", ".dump"];
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

		Assert.True(options.Length == 1, DescribeSnapshot(snapshot));
		var option = options[0];
		if (expectedChecked.HasValue)
			Assert.Equal(expectedChecked.Value, option.IsChecked);
	}

	private static string DescribeSnapshot(SelectionRefreshSnapshot snapshot)
	{
		var roots = snapshot.RootOptions is null
			? "<null>"
			: string.Join(", ", snapshot.RootOptions.Select(static option => $"{option.Name}:{option.IsChecked}"));
		var extensions = string.Join(", ", snapshot.ExtensionOptions.Select(static option => $"{option.Name}:{option.IsChecked}"));
		var ignore = string.Join(", ", snapshot.IgnoreOptions.Select(static option => $"{option.Id}:{option.IsChecked}"));
		var cache = string.Join(", ", snapshot.IgnoreOptionStateCache.OrderBy(static pair => pair.Key).Select(static pair => $"{pair.Key}:{pair.Value}"));

		return $"Ignore=[{ignore}], Cache=[{cache}], Roots=[{roots}], Extensions=[{extensions}], Counts={snapshot.IgnoreOptionCounts}";
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
