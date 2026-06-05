namespace DevProjex.Kernel.Abstractions;

public interface IFileSystemScannerProjectWorkspaceSnapshotProvider
{
	ScanResult<ProjectWorkspaceScanSnapshot> GetProjectWorkspaceSnapshotForRootSelection(
		string rootPath,
		IReadOnlyCollection<string> selectedRootFolders,
		IgnoreRules extensionDiscoveryRules,
		IgnoreRules effectiveRules,
		IExtensionInclusionPolicy? effectiveExtensionPolicy,
		bool includeDirectoryToggleProbeRoots = false,
		CancellationToken cancellationToken = default,
		bool includeControllerImpactProbeRoots = false);
}
