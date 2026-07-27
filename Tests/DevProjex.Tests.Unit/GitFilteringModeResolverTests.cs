namespace DevProjex.Tests.Unit;

public sealed class GitFilteringModeResolverTests
{
	[Theory]
	[InlineData(false, false, GitFilteringMode.None)]
	[InlineData(true, false, GitFilteringMode.RespectGitIgnore)]
	[InlineData(false, true, GitFilteringMode.TrackedFilesOnly)]
	[InlineData(true, true, GitFilteringMode.TrackedFilesOnly)]
	public void Resolve_SelectedOptionsUsesTrackedModeAsSafeConflictFallback(
		bool useGitIgnore,
		bool trackedOnly,
		GitFilteringMode expected)
	{
		var selected = new HashSet<IgnoreOptionId>();
		if (useGitIgnore)
			selected.Add(IgnoreOptionId.UseGitIgnore);
		if (trackedOnly)
			selected.Add(IgnoreOptionId.TrackedGitFilesOnly);

		Assert.Equal(expected, GitFilteringModeResolver.Resolve(selected));
	}

	[Fact]
	public void Normalize_StateDictionaryDefaultsToStricterModeAndPreservesUnrelatedOptions()
	{
		var states = new Dictionary<IgnoreOptionId, bool>
		{
			[IgnoreOptionId.UseGitIgnore] = true,
			[IgnoreOptionId.TrackedGitFilesOnly] = true,
			[IgnoreOptionId.SmartIgnore] = true
		};

		GitFilteringModeResolver.Normalize(states);

		Assert.False(states[IgnoreOptionId.UseGitIgnore]);
		Assert.True(states[IgnoreOptionId.TrackedGitFilesOnly]);
		Assert.True(states[IgnoreOptionId.SmartIgnore]);
	}

	[Fact]
	public void Normalize_SelectedSetHonorsExplicitGitIgnorePreference()
	{
		var selected = new HashSet<IgnoreOptionId>
		{
			IgnoreOptionId.UseGitIgnore,
			IgnoreOptionId.TrackedGitFilesOnly,
			IgnoreOptionId.DotFiles
		};

		GitFilteringModeResolver.Normalize(selected, GitFilteringMode.RespectGitIgnore);

		Assert.Contains(IgnoreOptionId.UseGitIgnore, selected);
		Assert.DoesNotContain(IgnoreOptionId.TrackedGitFilesOnly, selected);
		Assert.Contains(IgnoreOptionId.DotFiles, selected);
	}

	[Theory]
	[InlineData(IgnoreOptionId.UseGitIgnore, true)]
	[InlineData(IgnoreOptionId.TrackedGitFilesOnly, true)]
	[InlineData(IgnoreOptionId.SmartIgnore, false)]
	[InlineData(IgnoreOptionId.DotFiles, false)]
	public void IsGitFilteringOption_RecognizesOnlyTheTogglePair(
		IgnoreOptionId optionId,
		bool expected)
	{
		Assert.Equal(expected, GitFilteringModeResolver.IsGitFilteringOption(optionId));
	}
}
