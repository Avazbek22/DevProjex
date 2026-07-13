namespace DevProjex.Application.Selection;

public static class ProjectWorkspaceScanProjection
{
	public static bool TryProjectSelectedRoots(
		ProjectWorkspaceScanSnapshot source,
		IReadOnlyCollection<string> selectedRoots,
		bool includeDirectoryToggleProbeRoots,
		bool includeControllerImpactProbeRoots,
		out ScanResult<ProjectWorkspaceScanSnapshot> projected)
	{
		projected = default!;
		var breakdown = source.Breakdown;
		if (source.TreeInventory is null ||
		    breakdown is null ||
		    breakdown.IncludesDirectoryToggleProbeRoots != includeDirectoryToggleProbeRoots ||
		    breakdown.IncludesControllerImpactProbeRoots != includeControllerImpactProbeRoots)
		{
			return false;
		}

		var selectedNames = new HashSet<string>(selectedRoots, PathComparer.Default);
		foreach (var selectedName in selectedNames)
		{
			if (!breakdown.SelectedRoots.ContainsKey(selectedName))
				return false;
		}

		var rootFiles = breakdown.RootFiles;
		var extensions = new HashSet<string>(rootFiles.Extensions, StringComparer.OrdinalIgnoreCase);
		var effectiveExtensions = new HashSet<string>(rootFiles.VisibleExtensions, StringComparer.OrdinalIgnoreCase);
		var rawCounts = rootFiles.RawIgnoreOptionCounts;
		var effectiveCounts = rootFiles.EffectiveIgnoreOptionCounts;
		var controllerImpactCounts = rootFiles.ControllerImpactCounts;
		var rootAccessDenied = breakdown.RootEnumerationAccessDenied || breakdown.RootFilesAccessDenied;
		var hadAccessDenied = breakdown.RootEnumerationHadAccessDenied || breakdown.RootFilesHadAccessDenied;

		foreach (var (rootName, rootSnapshot) in breakdown.SelectedRoots)
		{
			if (selectedNames.Contains(rootName))
			{
				extensions.UnionWith(rootSnapshot.IgnoreSection.Extensions);
				effectiveExtensions.UnionWith(rootSnapshot.IgnoreSection.VisibleExtensions);
				rawCounts = rawCounts.Add(rootSnapshot.IgnoreSection.RawIgnoreOptionCounts);
				effectiveCounts = effectiveCounts.Add(rootSnapshot.IgnoreSection.EffectiveIgnoreOptionCounts);
				controllerImpactCounts = controllerImpactCounts.Add(rootSnapshot.IgnoreSection.ControllerImpactCounts);
				rootAccessDenied |= rootSnapshot.RootAccessDenied;
				hadAccessDenied |= rootSnapshot.HadAccessDenied;
				continue;
			}

			// A denied selected subtree cannot be safely converted into a cheap root probe:
			// keep the normal filesystem fallback so access flags remain exact.
			if (rootSnapshot.RootAccessDenied || rootSnapshot.HadAccessDenied)
				return false;

			if (includeDirectoryToggleProbeRoots)
				effectiveCounts = effectiveCounts.Add(rootSnapshot.DirectoryToggleProbeCounts);
			if (includeControllerImpactProbeRoots)
				controllerImpactCounts = controllerImpactCounts.Add(rootSnapshot.ControllerImpactProbeCounts);
		}

		if (includeDirectoryToggleProbeRoots)
			effectiveCounts = effectiveCounts.Add(breakdown.UnselectedDirectoryToggleProbeCounts);
		if (includeControllerImpactProbeRoots)
			controllerImpactCounts = controllerImpactCounts.Add(breakdown.UnselectedControllerImpactProbeCounts);

		var ignoreSection = new IgnoreSectionScanData(
			extensions,
			rawCounts,
			effectiveCounts,
			controllerImpactCounts,
			effectiveExtensions);
		projected = new ScanResult<ProjectWorkspaceScanSnapshot>(
			new ProjectWorkspaceScanSnapshot(ignoreSection, source.TreeInventory, breakdown),
			rootAccessDenied,
			hadAccessDenied);
		return true;
	}
}
