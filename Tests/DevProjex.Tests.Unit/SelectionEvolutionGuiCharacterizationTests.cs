namespace DevProjex.Tests.Unit;

public sealed class SelectionEvolutionGuiCharacterizationTests
{
	[Fact]
	public void Extensions_NewEntriesUseDefaultAndReturningEntriesKeepKnownState()
	{
		var service = new FilterOptionSelectionService();
		var state = new SelectionOptionStateCache(StringComparer.OrdinalIgnoreCase);
		state.RestoreProfile(
			[".cs"],
			new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
			{
				[".cs"] = true,
				[".md"] = false
			});

		var first = service.BuildExtensionOptions(
			[".cs", ".json"],
			state.SnapshotSelectedNames(),
			state.SnapshotOptionStatesOrNull(suppressLegacySelectedOnlyState: false));
		state.UpdateFromVisibleOptions(first);

		Assert.True(first.Single(option => option.Name == ".cs").IsChecked);
		Assert.True(first.Single(option => option.Name == ".json").IsChecked);

		var returned = service.BuildExtensionOptions(
			[".cs", ".json", ".md"],
			state.SnapshotSelectedNames(),
			state.SnapshotOptionStatesOrNull(suppressLegacySelectedOnlyState: false));

		Assert.True(returned.Single(option => option.Name == ".cs").IsChecked);
		Assert.True(returned.Single(option => option.Name == ".json").IsChecked);
		Assert.False(returned.Single(option => option.Name == ".md").IsChecked);
	}

	[Fact]
	public void Roots_NewEntriesUseRuleDefaultAndReturningEntriesKeepKnownState()
	{
		var service = new FilterOptionSelectionService();
		var state = new SelectionOptionStateCache(PathComparer.Default);
		state.RestoreProfile(
			["src"],
			new Dictionary<string, bool>(PathComparer.Default)
			{
				["src"] = true,
				["tests"] = false
			});
		var rules = new IgnoreRules(
			IgnoreHiddenFolders: false,
			IgnoreHiddenFiles: false,
			IgnoreDotFolders: true,
			IgnoreDotFiles: false,
			SmartIgnoredFolders: new HashSet<string>(PathComparer.Default),
			SmartIgnoredFiles: new HashSet<string>(PathComparer.Default));

		var first = service.BuildRootFolderOptions(
			["src", "docs", ".cache"],
			state.SnapshotSelectedNames(),
			rules,
			previousStateCache: state.SnapshotOptionStatesOrNull(
				suppressLegacySelectedOnlyState: false));
		state.UpdateFromVisibleOptions(first);

		Assert.True(first.Single(option => option.Name == "src").IsChecked);
		Assert.True(first.Single(option => option.Name == "docs").IsChecked);
		Assert.False(first.Single(option => option.Name == ".cache").IsChecked);

		var returned = service.BuildRootFolderOptions(
			["src", "docs", "tests"],
			state.SnapshotSelectedNames(),
			rules,
			previousStateCache: state.SnapshotOptionStatesOrNull(
				suppressLegacySelectedOnlyState: false));

		Assert.False(returned.Single(option => option.Name == "tests").IsChecked);
	}

	[Fact]
	public void IgnoreOptions_DisappearingRowsRetainExplicitCheckedAndUncheckedStates()
	{
		var state = new IgnoreSelectionState();
		state.ReplaceStateCache(new Dictionary<IgnoreOptionId, bool>
		{
			[IgnoreOptionId.HiddenFiles] = false,
			[IgnoreOptionId.HiddenFolders] = true
		});

		state.UpdateFromVisibleOptions(
			[(IgnoreOptionId.HiddenFolders, true)],
			state.SnapshotSelectedOptions(),
			[IgnoreOptionId.HiddenFolders]);

		Assert.True(state.TryGetCachedState(IgnoreOptionId.HiddenFiles, out var hiddenFiles));
		Assert.False(hiddenFiles);
		Assert.True(state.TryGetCachedState(IgnoreOptionId.HiddenFolders, out var hiddenFolders));
		Assert.True(hiddenFolders);
	}
}
