using DevProjex.Infrastructure.ProjectProfiles;

namespace DevProjex.Tests.Unit;

public sealed class ProjectProfileMergeWriterTests
{
	[Fact]
	public void UnchangedTerminalStateDoesNotOverwriteNewerGuiSelections()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateFolder("project");
		var appData = workspace.CreateFolder("data");
		var store = new ProjectProfileStore(() => appData);
		var baseline = CreateProfile(["A.cs", "T.cs"], hidePrivateData: false);
		Assert.True(store.TrySaveProfile(project, baseline));
		var gui = CreateProfile(["A.cs", "B.cs", "T.cs"], hidePrivateData: true);

		var guiWrite = ProjectProfileMergeWriter.TryMerge(
			store,
			project,
			gui,
			baseline,
			ProjectProfileMergeFields.AllSelections,
			TimeSpan.FromSeconds(1),
			cancellationToken: TestContext.Current.CancellationToken);
		var unchangedTerminal = ProjectProfileMergeWriter.TryMerge(
			store,
			project,
			baseline,
			baseline,
			ProjectProfileMergeFields.Extensions |
			ProjectProfileMergeFields.IgnoreOptions |
			ProjectProfileMergeFields.ExtensionStates |
			ProjectProfileMergeFields.IgnoreOptionStates |
			ProjectProfileMergeFields.SelectedPaths,
			TimeSpan.FromSeconds(1),
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.True(guiWrite.Succeeded);
		Assert.True(unchangedTerminal.Succeeded);
		Assert.True(store.TryLoadProfile(project, out var loaded));
		Assert.Equal(["A.cs", "B.cs", "T.cs"], loaded.SelectedPaths);
		Assert.Contains(IgnoreOptionId.HidePrivateData, loaded.SelectedIgnoreOptions);
	}

	[Fact]
	public void GuiOptionChangePreservesAConcurrentTerminalSelectionChange()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateFolder("project");
		var appData = workspace.CreateFolder("data");
		var storeA = new ProjectProfileStore(() => appData);
		var storeB = new ProjectProfileStore(() => appData);
		var baseline = CreateProfile(["A.cs"], hidePrivateData: false);
		Assert.True(storeA.TrySaveProfile(project, baseline));
		var terminal = CreateProfile(["T.cs"], hidePrivateData: false);
		var gui = CreateProfile(["A.cs"], hidePrivateData: true);

		Assert.True(ProjectProfileMergeWriter.TryMerge(
			storeA,
			project,
			terminal,
			baseline,
			ProjectProfileMergeFields.SelectedPaths,
			TimeSpan.FromSeconds(1),
			cancellationToken: TestContext.Current.CancellationToken).Succeeded);
		Assert.True(ProjectProfileMergeWriter.TryMerge(
			storeB,
			project,
			gui,
			baseline,
			ProjectProfileMergeFields.AllSelections,
			TimeSpan.FromSeconds(1),
			cancellationToken: TestContext.Current.CancellationToken).Succeeded);

		Assert.True(storeA.TryLoadProfile(project, out var loaded));
		Assert.Equal(["T.cs"], loaded.SelectedPaths);
		Assert.Contains(IgnoreOptionId.HidePrivateData, loaded.SelectedIgnoreOptions);
	}

	private static ProjectSelectionProfile CreateProfile(
		IReadOnlyCollection<string> selectedPaths,
		bool hidePrivateData)
	{
		var selectedIgnoreOptions = hidePrivateData
			? new[] { IgnoreOptionId.HidePrivateData }
			: [];
		return new ProjectSelectionProfile(
			SelectedRootFolders: [],
			SelectedExtensions: [".cs"],
			SelectedIgnoreOptions: selectedIgnoreOptions,
			ExtensionStates: new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
			{
				[".cs"] = true
			},
			IgnoreOptionStates: new Dictionary<IgnoreOptionId, bool>
			{
				[IgnoreOptionId.HidePrivateData] = hidePrivateData
			},
			SelectedPaths: selectedPaths);
	}
}
