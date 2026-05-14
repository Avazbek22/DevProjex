namespace DevProjex.Kernel.Abstractions;

public interface IFileSystemScannerRootSelectionSnapshotProvider
{
	ScanResult<IgnoreSectionScanData> GetIgnoreSectionSnapshotForRootSelection(
		string rootPath,
		IReadOnlyCollection<string> selectedRootFolders,
		IgnoreRules extensionDiscoveryRules,
		IgnoreRules effectiveRules,
		IExtensionInclusionPolicy? effectiveExtensionPolicy,
		bool includeDirectoryToggleProbeRoots = false,
		CancellationToken cancellationToken = default);
}
