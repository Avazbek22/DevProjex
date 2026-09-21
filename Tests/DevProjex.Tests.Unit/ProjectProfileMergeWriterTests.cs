using System.Diagnostics;
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

	[Fact]
	public void IndependentIgnoreOptionChangesFromTwoWindowsAreMergedPerOption()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateFolder("project");
		var appData = workspace.CreateFolder("data");
		var firstStore = new ProjectProfileStore(() => appData);
		var secondStore = new ProjectProfileStore(() => appData);
		var baseline = CreateProfileWithIgnoreStates(
			(IgnoreOptionId.HidePrivateData, false),
			(IgnoreOptionId.EmptyFolders, false));
		Assert.True(firstStore.TrySaveProfile(project, baseline));

		var firstWindow = CreateProfileWithIgnoreStates(
			(IgnoreOptionId.HidePrivateData, true),
			(IgnoreOptionId.EmptyFolders, false));
		var secondWindow = CreateProfileWithIgnoreStates(
			(IgnoreOptionId.HidePrivateData, false),
			(IgnoreOptionId.EmptyFolders, true));

		Assert.True(ProjectProfileMergeWriter.TryMerge(
			firstStore,
			project,
			firstWindow,
			baseline,
			ProjectProfileMergeFields.IgnoreOptions | ProjectProfileMergeFields.IgnoreOptionStates,
			TimeSpan.FromSeconds(1),
			cancellationToken: TestContext.Current.CancellationToken).Succeeded);
		Assert.True(ProjectProfileMergeWriter.TryMerge(
			secondStore,
			project,
			secondWindow,
			baseline,
			ProjectProfileMergeFields.IgnoreOptions | ProjectProfileMergeFields.IgnoreOptionStates,
			TimeSpan.FromSeconds(1),
			cancellationToken: TestContext.Current.CancellationToken).Succeeded);

		Assert.True(firstStore.TryLoadProfile(project, out var loaded));
		Assert.True(loaded.IgnoreOptionStates![IgnoreOptionId.HidePrivateData]);
		Assert.True(loaded.IgnoreOptionStates[IgnoreOptionId.EmptyFolders]);
		Assert.Contains(IgnoreOptionId.HidePrivateData, loaded.SelectedIgnoreOptions);
		Assert.Contains(IgnoreOptionId.EmptyFolders, loaded.SelectedIgnoreOptions);
	}

	[Fact]
	public void LaterChangeToTheSameIgnoreOptionWinsAndWritesConflictTrace()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateFolder("project");
		var store = new ProjectProfileStore(() => workspace.CreateFolder("data"));
		var baseline = CreateProfileWithIgnoreStates((IgnoreOptionId.HidePrivateData, false));
		var firstWindow = CreateProfileWithIgnoreStates((IgnoreOptionId.HidePrivateData, true));
		Assert.True(store.TrySaveProfile(project, baseline));
		Assert.True(ProjectProfileMergeWriter.TryMerge(
			store,
			project,
			firstWindow,
			baseline,
			ProjectProfileMergeFields.IgnoreOptions | ProjectProfileMergeFields.IgnoreOptionStates,
			TimeSpan.FromSeconds(1),
			cancellationToken: TestContext.Current.CancellationToken).Succeeded);

		using var traceText = new StringWriter();
		using var listener = new TextWriterTraceListener(traceText);
		Trace.Listeners.Add(listener);
		try
		{
			Assert.True(ProjectProfileMergeWriter.TryMerge(
				store,
				project,
				baseline,
				firstWindow,
				ProjectProfileMergeFields.IgnoreOptions | ProjectProfileMergeFields.IgnoreOptionStates,
				TimeSpan.FromSeconds(1),
				cancellationToken: TestContext.Current.CancellationToken).Succeeded);
			listener.Flush();
		}
		finally
		{
			Trace.Listeners.Remove(listener);
		}

		Assert.True(store.TryLoadProfile(project, out var loaded));
		Assert.False(loaded.IgnoreOptionStates![IgnoreOptionId.HidePrivateData]);
		Assert.DoesNotContain(IgnoreOptionId.HidePrivateData, loaded.SelectedIgnoreOptions);
		Assert.Contains("HidePrivateData", traceText.ToString(), StringComparison.Ordinal);
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

	private static ProjectSelectionProfile CreateProfileWithIgnoreStates(
		params (IgnoreOptionId Id, bool Selected)[] states)
	{
		var stateMap = states.ToDictionary(static pair => pair.Id, static pair => pair.Selected);
		return new ProjectSelectionProfile(
			SelectedRootFolders: [],
			SelectedExtensions: [".cs"],
			SelectedIgnoreOptions: states.Where(static pair => pair.Selected).Select(static pair => pair.Id).ToArray(),
			ExtensionStates: new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
			{
				[".cs"] = true
			},
			IgnoreOptionStates: stateMap,
			SelectedPaths: ["A.cs"]);
	}
}
