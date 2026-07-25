namespace DevProjex.Application.Selection;

public static class IgnoreOptionsAvailabilityResolver
{
	public static IgnoreOptionsAvailability CreateUnmeasured(
		bool includeGitIgnore,
		bool includeSmartIgnore) =>
		new(
			IncludeGitIgnore: includeGitIgnore,
			IncludeSmartIgnore: includeSmartIgnore);

	public static IgnoreOptionsAvailability Resolve(
		IgnoreOptionsAvailability structuralAvailability,
		in IgnoreSectionSnapshotState snapshotState,
		IReadOnlyDictionary<IgnoreOptionId, bool> stateCache,
		bool stateCacheIsComplete)
	{
		var availability = WithoutMeasuredOptions(structuralAvailability);
		if (!snapshotState.HasIgnoreOptionCounts)
		{
			return snapshotState.HasExtensionlessEntries
				? availability with
				{
					IncludeExtensionlessFiles = true,
					ExtensionlessFilesCount = snapshotState.ExtensionlessEntriesCount
				}
				: availability;
		}

		var counts = snapshotState.IgnoreOptionCounts;
		var controllerCounts = snapshotState.ControllerImpactCounts;
		return availability with
		{
			// A controller can hide the very root that proves its structural availability.
			// Measured impact is therefore authoritative, while an explicit unchecked state
			// remains visible so the user can always enable the controller again.
			IncludeGitIgnore = (availability.IncludeGitIgnore || controllerCounts.GitIgnore > 0) &&
			                   ShouldKeepControllerVisible(
				                   IgnoreOptionId.UseGitIgnore,
				                   controllerCounts.GitIgnore,
				                   stateCache,
				                   stateCacheIsComplete),
			IncludeSmartIgnore = (availability.IncludeSmartIgnore || controllerCounts.SmartIgnore > 0) &&
			                     ShouldKeepControllerVisible(
				                     IgnoreOptionId.SmartIgnore,
				                     controllerCounts.SmartIgnore,
				                     stateCache,
				                     stateCacheIsComplete),
			IncludeHiddenFolders = counts.HiddenFolders > 0,
			HiddenFoldersCount = counts.HiddenFolders,
			IncludeHiddenFiles = counts.HiddenFiles > 0,
			HiddenFilesCount = counts.HiddenFiles,
			IncludeDotFolders = counts.DotFolders > 0,
			DotFoldersCount = counts.DotFolders,
			IncludeDotFiles = counts.DotFiles > 0,
			DotFilesCount = counts.DotFiles,
			IncludeEmptyFolders = counts.EmptyFolders > 0,
			EmptyFoldersCount = counts.EmptyFolders,
			IncludeEmptyFiles = counts.EmptyFiles > 0,
			EmptyFilesCount = counts.EmptyFiles,
			IncludeExtensionlessFiles = counts.ExtensionlessFiles > 0,
			ExtensionlessFilesCount = counts.ExtensionlessFiles
		};
	}

	private static IgnoreOptionsAvailability WithoutMeasuredOptions(
		IgnoreOptionsAvailability availability) =>
		availability with
		{
			IncludeHiddenFolders = false,
			HiddenFoldersCount = 0,
			IncludeHiddenFiles = false,
			HiddenFilesCount = 0,
			IncludeDotFolders = false,
			DotFoldersCount = 0,
			IncludeDotFiles = false,
			DotFilesCount = 0,
			IncludeEmptyFolders = false,
			EmptyFoldersCount = 0,
			IncludeEmptyFiles = false,
			EmptyFilesCount = 0,
			IncludeExtensionlessFiles = false,
			ExtensionlessFilesCount = 0
		};

	private static bool ShouldKeepControllerVisible(
		IgnoreOptionId optionId,
		int controllerImpactCount,
		IReadOnlyDictionary<IgnoreOptionId, bool> stateCache,
		bool stateCacheIsComplete)
	{
		if (controllerImpactCount > 0)
			return true;

		return stateCacheIsComplete &&
		       stateCache.TryGetValue(optionId, out var isChecked) &&
		       !isChecked;
	}
}
