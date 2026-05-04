namespace DevProjex.Tests.Unit;

public sealed class IgnoreSelectionStateTests
{
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
}
