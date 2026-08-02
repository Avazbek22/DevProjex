namespace DevProjex.Application.Selection;

public static class ProjectWorkspaceScanProjection
{
	public static bool TryProjectSelectedRoots(
		ProjectWorkspaceScanSnapshot source,
		IReadOnlyCollection<string> selectedRoots,
		bool includeDirectoryToggleProbeRoots,
		bool includeControllerImpactProbeRoots,
		IReadOnlySet<string>? retainedRemovedRootEmptyFolderImpactRoots,
		out ScanResult<ProjectWorkspaceScanSnapshot> projected)
	{
		projected = default!;
		var breakdown = source.Breakdown;
		if (breakdown is null ||
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
		// Keep extension choices from the original scan so a root hidden by the current
		// extension filter can be restored without another filesystem traversal.
		var extensions = new HashSet<string>(
			source.IgnoreSection.Extensions,
			StringComparer.OrdinalIgnoreCase);
		var effectiveExtensions = new HashSet<string>(
			source.IgnoreSection.VisibleExtensions,
			StringComparer.OrdinalIgnoreCase);
		var rawCounts = rootFiles.RawIgnoreOptionCounts;
		var effectiveCounts = rootFiles.EffectiveIgnoreOptionCounts;
		var controllerImpactCounts = rootFiles.ControllerImpactCounts;
		var gitEvidence = rootFiles.GitEvidence;
		var rootAccessDenied = breakdown.RootEnumerationAccessDenied || breakdown.RootFilesAccessDenied;
		var hadAccessDenied = breakdown.RootEnumerationHadAccessDenied || breakdown.RootFilesHadAccessDenied;

		foreach (var (rootName, rootSnapshot) in breakdown.SelectedRoots)
		{
			if (selectedNames.Contains(rootName))
			{
				rawCounts = rawCounts.Add(rootSnapshot.IgnoreSection.RawIgnoreOptionCounts);
				effectiveCounts = effectiveCounts.Add(rootSnapshot.IgnoreSection.EffectiveIgnoreOptionCounts);
				controllerImpactCounts = controllerImpactCounts.Add(rootSnapshot.IgnoreSection.ControllerImpactCounts);
				gitEvidence = gitEvidence.Add(rootSnapshot.IgnoreSection.GitEvidence);
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
			if (retainedRemovedRootEmptyFolderImpactRoots?.Contains(rootName) == true)
			{
				// Preserve only roots classified as EmptyFolders-owned by the root projection.
				// Controller-owned roots stay excluded from both the tree and option count.
				effectiveCounts = effectiveCounts with
				{
					EmptyFolders = effectiveCounts.EmptyFolders +
					               rootSnapshot.IgnoreSection.EffectiveIgnoreOptionCounts.EmptyFolders
				};
			}
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
			effectiveExtensions,
			GitEvidence: gitEvidence);
		projected = new ScanResult<ProjectWorkspaceScanSnapshot>(
			new ProjectWorkspaceScanSnapshot(ignoreSection, source.TreeInventory, breakdown),
			rootAccessDenied,
			hadAccessDenied);
		return true;
	}
}
