namespace DevProjex.Tests.Unit;

public sealed class IgnoreOptionsAvailabilityResolverTests
{
	[Fact]
	public void Resolve_MeasuredSnapshotIsAuthoritativeForEveryAdvancedOption()
	{
		var structural = new IgnoreOptionsAvailability(
			IncludeGitIgnore: true,
			IncludeSmartIgnore: true,
			IncludeHiddenFolders: true,
			HiddenFoldersCount: 99,
			IncludeHiddenFiles: true,
			HiddenFilesCount: 99,
			IncludeDotFolders: true,
			DotFoldersCount: 99,
			IncludeDotFiles: true,
			DotFilesCount: 99,
			IncludeEmptyFolders: true,
			EmptyFoldersCount: 99,
			IncludeExtensionlessFiles: true,
			ExtensionlessFilesCount: 99,
			IncludeEmptyFiles: true,
			EmptyFilesCount: 99,
			ShowAdvancedCounts: true);
		var counts = new IgnoreOptionCounts(
			HiddenFolders: 2,
			HiddenFiles: 0,
			DotFolders: 3,
			DotFiles: 0,
			EmptyFolders: 4,
			ExtensionlessFiles: 0,
			EmptyFiles: 5);
		var snapshot = new IgnoreSectionSnapshotState(
			HasIgnoreOptionCounts: true,
			IgnoreOptionCounts: counts,
			ControllerImpactCounts: new IgnoreControllerImpactCounts(GitIgnore: 7, SmartIgnore: 0),
			HasExtensionlessEntries: false,
			ExtensionlessEntriesCount: 0);

		var actual = IgnoreOptionsAvailabilityResolver.Resolve(
			structural,
			snapshot,
			new Dictionary<IgnoreOptionId, bool>(),
			stateCacheIsComplete: false);

		Assert.True(actual.IncludeGitIgnore);
		Assert.False(actual.IncludeTrackedGitFilesOnly);
		Assert.False(actual.IncludeSmartIgnore);
		Assert.True(actual.IncludeHiddenFolders);
		Assert.Equal(2, actual.HiddenFoldersCount);
		Assert.False(actual.IncludeHiddenFiles);
		Assert.True(actual.IncludeDotFolders);
		Assert.Equal(3, actual.DotFoldersCount);
		Assert.False(actual.IncludeDotFiles);
		Assert.True(actual.IncludeEmptyFolders);
		Assert.Equal(4, actual.EmptyFoldersCount);
		Assert.True(actual.IncludeEmptyFiles);
		Assert.Equal(5, actual.EmptyFilesCount);
		Assert.False(actual.IncludeExtensionlessFiles);
		Assert.True(actual.ShowAdvancedCounts);
	}

	[Theory]
	[MemberData(nameof(ControllerVisibilityCases))]
	public void Resolve_ControllerVisibilityFollowsMeasuredImpactAndReversibleState(
		bool structuralCandidate,
		int measuredImpact,
		bool stateCacheIsComplete,
		bool? cachedCheckedState,
		bool expectedVisible)
	{
		var stateCache = cachedCheckedState.HasValue
			? new Dictionary<IgnoreOptionId, bool>
			{
				[IgnoreOptionId.UseGitIgnore] = cachedCheckedState.GetValueOrDefault()
			}
			: new Dictionary<IgnoreOptionId, bool>();
		var snapshot = new IgnoreSectionSnapshotState(
			HasIgnoreOptionCounts: true,
			IgnoreOptionCounts: IgnoreOptionCounts.Empty,
			ControllerImpactCounts: new IgnoreControllerImpactCounts(measuredImpact, SmartIgnore: 0),
			HasExtensionlessEntries: false,
			ExtensionlessEntriesCount: 0);

		var actual = IgnoreOptionsAvailabilityResolver.Resolve(
			new IgnoreOptionsAvailability(structuralCandidate, IncludeSmartIgnore: false),
			snapshot,
			stateCache,
			stateCacheIsComplete);

		Assert.Equal(expectedVisible, actual.IncludeGitIgnore);
	}

	public static TheoryData<bool, int, bool, bool?, bool> ControllerVisibilityCases => new()
	{
		{ true, 3, false, null, true },
		{ false, 3, false, null, true },
		{ true, 0, false, null, false },
		{ true, 0, true, true, false },
		{ true, 0, true, false, false },
		{ false, 0, true, false, false }
	};

	[Fact]
	public void Resolve_RepositoryKeepsBothGitModesVisibleAfterMeasuredImpactDropsToZero()
	{
		var snapshot = new IgnoreSectionSnapshotState(
			HasIgnoreOptionCounts: true,
			IgnoreOptionCounts: IgnoreOptionCounts.Empty,
			ControllerImpactCounts: IgnoreControllerImpactCounts.Empty,
			HasExtensionlessEntries: false,
			ExtensionlessEntriesCount: 0);

		var actual = IgnoreOptionsAvailabilityResolver.Resolve(
			new IgnoreOptionsAvailability(
				IncludeGitIgnore: false,
				IncludeSmartIgnore: false,
				IncludeTrackedGitFilesOnly: true),
			snapshot,
			new Dictionary<IgnoreOptionId, bool>
			{
				[IgnoreOptionId.UseGitIgnore] = false,
				[IgnoreOptionId.TrackedGitFilesOnly] = true
			},
			stateCacheIsComplete: true);

		Assert.True(actual.IncludeGitIgnore);
		Assert.True(actual.IncludeTrackedGitFilesOnly);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void Resolve_ScanEvidenceExposesStableGitModePair(bool hasMeasuredCounts)
	{
		var snapshot = new IgnoreSectionSnapshotState(
			HasIgnoreOptionCounts: hasMeasuredCounts,
			IgnoreOptionCounts: IgnoreOptionCounts.Empty,
			ControllerImpactCounts: IgnoreControllerImpactCounts.Empty,
			HasExtensionlessEntries: false,
			ExtensionlessEntriesCount: 0,
			GitEvidence: new GitWorkspaceEvidence(HasRepositoryBoundary: true));

		var actual = IgnoreOptionsAvailabilityResolver.Resolve(
			new IgnoreOptionsAvailability(
				IncludeGitIgnore: false,
				IncludeSmartIgnore: false),
			snapshot,
			new Dictionary<IgnoreOptionId, bool>(),
			stateCacheIsComplete: false);

		Assert.True(actual.IncludeGitIgnore);
		Assert.True(actual.IncludeTrackedGitFilesOnly);
	}

	[Fact]
	public void Resolve_RemovedScanEvidenceDoesNotLeakCachedGitModes()
	{
		var actual = IgnoreOptionsAvailabilityResolver.Resolve(
			new IgnoreOptionsAvailability(
				IncludeGitIgnore: false,
				IncludeSmartIgnore: false),
			new IgnoreSectionSnapshotState(
				HasIgnoreOptionCounts: true,
				IgnoreOptionCounts: IgnoreOptionCounts.Empty,
				ControllerImpactCounts: IgnoreControllerImpactCounts.Empty,
				HasExtensionlessEntries: false,
				ExtensionlessEntriesCount: 0),
			new Dictionary<IgnoreOptionId, bool>
			{
				[IgnoreOptionId.UseGitIgnore] = true,
				[IgnoreOptionId.TrackedGitFilesOnly] = false
			},
			stateCacheIsComplete: true);

		Assert.False(actual.IncludeGitIgnore);
		Assert.False(actual.IncludeTrackedGitFilesOnly);
	}

	[Fact]
	public void SnapshotState_GitEvidenceChangeAffectsAvailabilityComparison()
	{
		var before = new IgnoreSectionSnapshotState(
			HasIgnoreOptionCounts: true,
			IgnoreOptionCounts: IgnoreOptionCounts.Empty,
			ControllerImpactCounts: IgnoreControllerImpactCounts.Empty,
			HasExtensionlessEntries: false,
			ExtensionlessEntriesCount: 0);
		var after = before with
		{
			GitEvidence = new GitWorkspaceEvidence(HasRepositoryBoundary: true)
		};

		Assert.True(before.HasAvailabilityDifference(after));
		Assert.True(after.HasAvailabilityDifference(before));
	}

	[Fact]
	public void Resolve_UnmeasuredSnapshotPreservesControllersButHidesUnverifiedAdvancedOptions()
	{
		var structural = new IgnoreOptionsAvailability(
			IncludeGitIgnore: true,
			IncludeSmartIgnore: true,
			IncludeHiddenFolders: true,
			IncludeHiddenFiles: true,
			IncludeDotFolders: true,
			IncludeDotFiles: true);
		var snapshot = new IgnoreSectionSnapshotState(
			HasIgnoreOptionCounts: false,
			IgnoreOptionCounts: IgnoreOptionCounts.Empty,
			ControllerImpactCounts: IgnoreControllerImpactCounts.Empty,
			HasExtensionlessEntries: true,
			ExtensionlessEntriesCount: 6);

		var actual = IgnoreOptionsAvailabilityResolver.Resolve(
			structural,
			snapshot,
			new Dictionary<IgnoreOptionId, bool>(),
			stateCacheIsComplete: false);

		Assert.True(actual.IncludeGitIgnore);
		Assert.True(actual.IncludeSmartIgnore);
		Assert.False(actual.IncludeHiddenFolders);
		Assert.False(actual.IncludeHiddenFiles);
		Assert.False(actual.IncludeDotFolders);
		Assert.False(actual.IncludeDotFiles);
		Assert.True(actual.IncludeExtensionlessFiles);
		Assert.Equal(6, actual.ExtensionlessFilesCount);
	}
}
