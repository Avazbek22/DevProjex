namespace DevProjex.Tests.Unit;

public sealed class IgnoreSelectionStateTests
{
	[Fact]
	public void RestoreProfileSelection_InitializesSelectedOptionsAndStateCache()
	{
		var state = new IgnoreSelectionState();

		state.RestoreProfileSelection([IgnoreOptionId.SmartIgnore, IgnoreOptionId.DotFolders]);

		Assert.True(state.IsInitialized);
		Assert.Contains(IgnoreOptionId.SmartIgnore, state.SelectedOptions);
		Assert.Contains(IgnoreOptionId.DotFolders, state.SelectedOptions);
		Assert.True(state.OptionStateCache[IgnoreOptionId.SmartIgnore]);
		Assert.True(state.OptionStateCache[IgnoreOptionId.DotFolders]);
	}

	[Fact]
	public void ReplaceStateCache_RebuildsSelectedOptionsFromCachedStates()
	{
		var state = new IgnoreSelectionState();

		state.ReplaceStateCache(new Dictionary<IgnoreOptionId, bool>
		{
			[IgnoreOptionId.SmartIgnore] = true,
			[IgnoreOptionId.DotFolders] = false,
			[IgnoreOptionId.EmptyFiles] = true
		});

		Assert.True(state.IsInitialized);
		Assert.Contains(IgnoreOptionId.SmartIgnore, state.SelectedOptions);
		Assert.Contains(IgnoreOptionId.EmptyFiles, state.SelectedOptions);
		Assert.DoesNotContain(IgnoreOptionId.DotFolders, state.SelectedOptions);
	}

	[Fact]
	public void EnsureDefaults_DoesNotOverrideInitializedManualState()
	{
		var state = new IgnoreSelectionState();
		state.ReplaceStateCache(new Dictionary<IgnoreOptionId, bool>
		{
			[IgnoreOptionId.DotFolders] = false
		});

		state.EnsureDefaults([
			new IgnoreOptionDescriptor(IgnoreOptionId.DotFolders, "Dot folders", true),
			new IgnoreOptionDescriptor(IgnoreOptionId.EmptyFiles, "Empty files", true)
		]);

		Assert.False(state.OptionStateCache[IgnoreOptionId.DotFolders]);
		Assert.DoesNotContain(IgnoreOptionId.DotFolders, state.SelectedOptions);
		Assert.DoesNotContain(IgnoreOptionId.EmptyFiles, state.OptionStateCache.Keys);
	}

	[Fact]
	public void UpdateFromVisibleOptions_PreservesMissingSelectedOptions()
	{
		var state = new IgnoreSelectionState();
		state.RestoreProfileSelection([IgnoreOptionId.DotFolders]);

		state.UpdateFromVisibleOptions(
			[(IgnoreOptionId.SmartIgnore, false)],
			preserveMissingFrom: new HashSet<IgnoreOptionId> { IgnoreOptionId.DotFolders },
			visibleDescriptorIds: [IgnoreOptionId.SmartIgnore]);

		Assert.Contains(IgnoreOptionId.DotFolders, state.SelectedOptions);
		Assert.DoesNotContain(IgnoreOptionId.SmartIgnore, state.SelectedOptions);
	}

	[Fact]
	public void UpdateFromVisibleOptions_PreservesMissingUncheckedAndCheckedStates()
	{
		var state = new IgnoreSelectionState();
		state.ReplaceStateCache(new Dictionary<IgnoreOptionId, bool>
		{
			[IgnoreOptionId.DotFolders] = false,
			[IgnoreOptionId.EmptyFiles] = true
		});

		state.UpdateFromVisibleOptions(
			[(IgnoreOptionId.SmartIgnore, true)],
			preserveMissingFrom: new HashSet<IgnoreOptionId>
			{
				IgnoreOptionId.DotFolders,
				IgnoreOptionId.EmptyFiles
			},
			visibleDescriptorIds: [IgnoreOptionId.SmartIgnore]);

		Assert.True(state.OptionStateCache[IgnoreOptionId.SmartIgnore]);
		Assert.False(state.OptionStateCache[IgnoreOptionId.DotFolders]);
		Assert.True(state.OptionStateCache[IgnoreOptionId.EmptyFiles]);
		Assert.Contains(IgnoreOptionId.SmartIgnore, state.SelectedOptions);
		Assert.Contains(IgnoreOptionId.EmptyFiles, state.SelectedOptions);
		Assert.DoesNotContain(IgnoreOptionId.DotFolders, state.SelectedOptions);
	}

	[Fact]
	public void ApplyAllPreferenceToKnownStates_RebuildsSelectedSet()
	{
		var state = new IgnoreSelectionState();
		state.EnsureDefaults([
			new IgnoreOptionDescriptor(IgnoreOptionId.SmartIgnore, "Smart", true),
			new IgnoreOptionDescriptor(IgnoreOptionId.DotFolders, "Dot folders", true)
		]);

		state.ApplyAllPreferenceToKnownStates(false);

		Assert.Empty(state.SelectedOptions);
		Assert.All(state.OptionStateCache, pair => Assert.False(pair.Value));
	}

	[Fact]
	public void RestoreProfileSelection_ConflictingGitModesFailClosedToTrackedOnly()
	{
		var state = new IgnoreSelectionState();

		state.RestoreProfileSelection([
			IgnoreOptionId.UseGitIgnore,
			IgnoreOptionId.TrackedGitFilesOnly,
			IgnoreOptionId.SmartIgnore
		]);

		Assert.DoesNotContain(IgnoreOptionId.UseGitIgnore, state.SelectedOptions);
		Assert.Contains(IgnoreOptionId.TrackedGitFilesOnly, state.SelectedOptions);
		Assert.Contains(IgnoreOptionId.SmartIgnore, state.SelectedOptions);
	}

	[Fact]
	public void ApplyAllPreferenceToKnownStates_PreservesTrackedModeAsTheLogicalGitSlot()
	{
		var state = new IgnoreSelectionState();
		state.ReplaceStateCache(new Dictionary<IgnoreOptionId, bool>
		{
			[IgnoreOptionId.UseGitIgnore] = false,
			[IgnoreOptionId.TrackedGitFilesOnly] = true,
			[IgnoreOptionId.SmartIgnore] = false,
			[IgnoreOptionId.DotFolders] = false
		});

		state.ApplyAllPreferenceToKnownStates(true);

		Assert.False(state.OptionStateCache[IgnoreOptionId.UseGitIgnore]);
		Assert.True(state.OptionStateCache[IgnoreOptionId.TrackedGitFilesOnly]);
		Assert.True(state.OptionStateCache[IgnoreOptionId.SmartIgnore]);
		Assert.True(state.OptionStateCache[IgnoreOptionId.DotFolders]);

		state.ApplyAllPreferenceToKnownStates(false);

		Assert.Empty(state.SelectedOptions);
		Assert.All(state.OptionStateCache, static pair => Assert.False(pair.Value));
	}

	[Fact]
	public void ApplyAllPreferenceToKnownStates_DefaultsEmptyGitSlotToGitIgnore()
	{
		var state = new IgnoreSelectionState();
		state.ReplaceStateCache(new Dictionary<IgnoreOptionId, bool>
		{
			[IgnoreOptionId.UseGitIgnore] = false,
			[IgnoreOptionId.TrackedGitFilesOnly] = false,
			[IgnoreOptionId.SmartIgnore] = false
		});

		state.ApplyAllPreferenceToKnownStates(true);

		Assert.True(state.OptionStateCache[IgnoreOptionId.UseGitIgnore]);
		Assert.False(state.OptionStateCache[IgnoreOptionId.TrackedGitFilesOnly]);
		Assert.True(state.OptionStateCache[IgnoreOptionId.SmartIgnore]);
	}

	[Fact]
	public void Reset_ClearsSelectionStateAndAllPreference()
	{
		var state = new IgnoreSelectionState();
		state.RestoreProfileSelection([IgnoreOptionId.SmartIgnore]);
		state.AllPreference = false;

		state.Reset(trimExcess: true);

		Assert.False(state.IsInitialized);
		Assert.Null(state.AllPreference);
		Assert.Empty(state.SelectedOptions);
		Assert.Empty(state.OptionStateCache);
	}
}
