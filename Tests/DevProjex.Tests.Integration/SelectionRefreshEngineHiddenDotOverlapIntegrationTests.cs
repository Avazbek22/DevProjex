using DevProjex.Tests.Shared.ProjectLoadWorkflow;

namespace DevProjex.Tests.Integration;

public sealed class SelectionRefreshEngineHiddenDotOverlapIntegrationTests
{
	[Fact]
	public void ComputeFullRefreshSnapshot_UnixDotRoot_KeepsDotFoldersVisibleWhenHiddenFoldersAlsoMatches()
	{
		if (OperatingSystem.IsWindows())
			return;

		using var temp = new TemporaryDirectory();
		temp.CreateFile("requirements.txt", string.Empty);
		temp.CreateFile("src/app.py", "print('ok')");
		temp.CreateFile("__pycache__/app.pyc", "binary");
		temp.CreateFile(".idea/workspace.xml", "<project />");
		var services = ProjectLoadWorkflowRefreshHarness.CreateServices();

		var baseline = services.Engine.ComputeFullRefreshSnapshot(
			ProjectLoadWorkflowRefreshHarness.CreateDefaultContext(temp.Path),
			CancellationToken.None);
		AssertVisibleOption(baseline, IgnoreOptionId.SmartIgnore, isChecked: true);
		AssertVisibleOption(baseline, IgnoreOptionId.DotFolders, isChecked: true);
		AssertHiddenOption(baseline, IgnoreOptionId.HiddenFolders);
		Assert.DoesNotContain(baseline.RootOptions!, option => option.Name == ".idea");

		var dotFoldersOff = services.Engine.ComputeFullRefreshSnapshot(
			CreateContextWithIgnoreStates(
				temp.Path,
				baseline,
				new Dictionary<IgnoreOptionId, bool>
				{
					[IgnoreOptionId.DotFolders] = false
				}),
			CancellationToken.None);
		AssertVisibleOption(dotFoldersOff, IgnoreOptionId.DotFolders, isChecked: false);
		AssertHiddenOption(dotFoldersOff, IgnoreOptionId.HiddenFolders);
		Assert.Contains(dotFoldersOff.RootOptions!, option => option.Name == ".idea" && option.IsChecked);

		var allDirectoryTogglesOff = services.Engine.ComputeFullRefreshSnapshot(
			CreateContextWithIgnoreStates(
				temp.Path,
				dotFoldersOff,
				new Dictionary<IgnoreOptionId, bool>
				{
					[IgnoreOptionId.DotFolders] = false,
					[IgnoreOptionId.HiddenFolders] = false
				}),
			CancellationToken.None);
		AssertVisibleOption(allDirectoryTogglesOff, IgnoreOptionId.DotFolders, isChecked: false);
		AssertHiddenOption(allDirectoryTogglesOff, IgnoreOptionId.HiddenFolders);
		Assert.Contains(allDirectoryTogglesOff.RootOptions!, option => option.Name == ".idea" && option.IsChecked);
	}

	[Fact]
	public void ComputeFullRefreshSnapshot_WindowsHiddenDotRoot_DotToggleExposesCheckedHiddenToggle()
	{
		if (!OperatingSystem.IsWindows())
			return;

		using var temp = CreateHiddenDotWorkspaceWithVisibleGitContent();
		var services = ProjectLoadWorkflowRefreshHarness.CreateServices();

		var baseline = services.Engine.ComputeFullRefreshSnapshot(
			ProjectLoadWorkflowRefreshHarness.CreateDefaultContext(temp.Path),
			CancellationToken.None);
		AssertVisibleOption(baseline, IgnoreOptionId.DotFolders, isChecked: true);
		AssertHiddenOption(baseline, IgnoreOptionId.HiddenFolders);
		AssertCachedState(baseline, IgnoreOptionId.HiddenFolders, isChecked: true);

		var dotFoldersOff = services.Engine.ComputeFullRefreshSnapshot(
			CreateContextWithIgnoreStates(
				temp.Path,
				baseline,
				new Dictionary<IgnoreOptionId, bool>
				{
					[IgnoreOptionId.DotFolders] = false
				}),
			CancellationToken.None);
		AssertVisibleOption(dotFoldersOff, IgnoreOptionId.DotFolders, isChecked: false);
		AssertVisibleOption(dotFoldersOff, IgnoreOptionId.HiddenFolders, isChecked: true);

		var hiddenFoldersOff = services.Engine.ComputeFullRefreshSnapshot(
			CreateContextWithIgnoreStates(
				temp.Path,
				dotFoldersOff,
				new Dictionary<IgnoreOptionId, bool>
				{
					[IgnoreOptionId.HiddenFolders] = false,
					[IgnoreOptionId.DotFolders] = false
				}),
			CancellationToken.None);
		AssertVisibleOption(hiddenFoldersOff, IgnoreOptionId.HiddenFolders, isChecked: false);
		Assert.Contains(hiddenFoldersOff.RootOptions!, option => option.Name == ".git" && option.IsChecked);

		var dotFoldersOnAgain = services.Engine.ComputeFullRefreshSnapshot(
			CreateContextWithIgnoreStates(
				temp.Path,
				hiddenFoldersOff,
				new Dictionary<IgnoreOptionId, bool>
				{
					[IgnoreOptionId.HiddenFolders] = false,
					[IgnoreOptionId.DotFolders] = true
				}),
			CancellationToken.None);
		AssertVisibleOption(dotFoldersOnAgain, IgnoreOptionId.DotFolders, isChecked: true);
		AssertHiddenOption(dotFoldersOnAgain, IgnoreOptionId.HiddenFolders);
		AssertCachedState(dotFoldersOnAgain, IgnoreOptionId.HiddenFolders, isChecked: false);

		var dotFoldersOffAgain = services.Engine.ComputeFullRefreshSnapshot(
			CreateContextWithIgnoreStates(
				temp.Path,
				dotFoldersOnAgain,
				new Dictionary<IgnoreOptionId, bool>
				{
					[IgnoreOptionId.HiddenFolders] = false,
					[IgnoreOptionId.DotFolders] = false
				}),
			CancellationToken.None);
		AssertVisibleOption(dotFoldersOffAgain, IgnoreOptionId.HiddenFolders, isChecked: false);
		Assert.Contains(dotFoldersOffAgain.RootOptions!, option => option.Name == ".git" && option.IsChecked);
	}

	[Fact]
	public void ComputeFullRefreshSnapshot_WindowsNestedHiddenDotFolder_CountsAsDotFoldersBeforeHiddenFolders()
	{
		if (!OperatingSystem.IsWindows())
			return;

		using var temp = new TemporaryDirectory();
		temp.CreateFile("src/App.cs", "class App {}");
		temp.CreateFile("src/.visible-dot/payload.txt", "visible dot payload");
		temp.CreateFile("src/.hidden-dot/payload.txt", "hidden dot payload");
		MarkHidden(Path.Combine(temp.Path, "src", ".hidden-dot"));
		var services = ProjectLoadWorkflowRefreshHarness.CreateServices();

		var baseline = services.Engine.ComputeFullRefreshSnapshot(
			ProjectLoadWorkflowRefreshHarness.CreateDefaultContext(temp.Path),
			CancellationToken.None);
		AssertVisibleOption(baseline, IgnoreOptionId.DotFolders, isChecked: true);
		AssertHiddenOption(baseline, IgnoreOptionId.HiddenFolders);
		Assert.Equal(2, baseline.IgnoreOptionCounts.DotFolders);
		Assert.Equal(0, baseline.IgnoreOptionCounts.HiddenFolders);

		var secondPass = services.Engine.ComputeFullRefreshSnapshot(
			ProjectLoadWorkflowRefreshHarness.CreateContextFromSnapshot(temp.Path, baseline),
			CancellationToken.None);
		AssertVisibleOption(secondPass, IgnoreOptionId.DotFolders, isChecked: true);
		AssertHiddenOption(secondPass, IgnoreOptionId.HiddenFolders);
		Assert.Equal(baseline.IgnoreOptionCounts.DotFolders, secondPass.IgnoreOptionCounts.DotFolders);
	}

	private static TemporaryDirectory CreateHiddenDotWorkspaceWithVisibleGitContent()
	{
		var temp = new TemporaryDirectory();
		temp.CreateFile("src/Program.cs", "class Program {}");
		temp.CreateFile(".idea/workspace.xml", "<project />");
		temp.CreateFile(".git/config.txt", "[core]\n");
		MarkHidden(Path.Combine(temp.Path, ".git"));
		return temp;
	}

	private static SelectionRefreshContext CreateContextWithIgnoreStates(
		string rootPath,
		SelectionRefreshSnapshot snapshot,
		IReadOnlyDictionary<IgnoreOptionId, bool> overrides)
	{
		var stateCache = new Dictionary<IgnoreOptionId, bool>(snapshot.IgnoreOptionStateCache);
		foreach (var (id, isChecked) in overrides)
			stateCache[id] = isChecked;

		return ProjectLoadWorkflowRefreshHarness.CreateContextFromSnapshot(rootPath, snapshot) with
		{
			IgnoreSelectionInitialized = true,
			IgnoreSelectionCache = stateCache
				.Where(pair => pair.Value)
				.Select(pair => pair.Key)
				.ToHashSet(),
			IgnoreOptionStateCache = stateCache,
			IgnoreAllPreference = null
		};
	}

	private static void AssertVisibleOption(
		SelectionRefreshSnapshot snapshot,
		IgnoreOptionId id,
		bool isChecked)
	{
		var option = Assert.Single(snapshot.IgnoreOptions, option => option.Id == id);
		Assert.Equal(isChecked, option.IsChecked);
	}

	private static void AssertHiddenOption(SelectionRefreshSnapshot snapshot, IgnoreOptionId id)
	{
		Assert.DoesNotContain(snapshot.IgnoreOptions, option => option.Id == id);
	}

	private static void AssertCachedState(
		SelectionRefreshSnapshot snapshot,
		IgnoreOptionId id,
		bool isChecked)
	{
		Assert.True(snapshot.IgnoreOptionStateCache.TryGetValue(id, out var actual));
		Assert.Equal(isChecked, actual);
	}

	private static void MarkHidden(string path)
	{
		var attributes = File.GetAttributes(path);
		File.SetAttributes(path, attributes | FileAttributes.Hidden);
	}
}
