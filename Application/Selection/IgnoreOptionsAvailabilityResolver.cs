namespace DevProjex.Application.Selection;

public static class IgnoreOptionsAvailabilityResolver
{
	public static IgnoreOptionsAvailability CreateUnmeasured(
		bool includeGitIgnore,
		bool includeSmartIgnore,
		bool includeTrackedGitFilesOnly = false) =>
		new(
			IncludeGitIgnore: includeGitIgnore,
			IncludeSmartIgnore: includeSmartIgnore,
			IncludeTrackedGitFilesOnly: includeTrackedGitFilesOnly);

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
			// Inside a real repository the two Git modes form a stable toggle pair.
			// A standalone .gitignore file remains evidence-driven and is hidden when
			// it cannot change the effective tree.
			IncludeGitIgnore =
				availability.IncludeTrackedGitFilesOnly ||
				controllerCounts.GitIgnore > 0,
			IncludeTrackedGitFilesOnly = ShouldKeepModeVisible(
				availability.IncludeTrackedGitFilesOnly),
			// Smart Ignore is evidence-driven after the measured pass. A project marker
			// alone must not leave a checkbox that cannot change the effective tree.
			IncludeSmartIgnore = controllerCounts.SmartIgnore > 0,
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

	private static bool ShouldKeepModeVisible(bool structurallyAvailable) =>
		structurallyAvailable;
}
