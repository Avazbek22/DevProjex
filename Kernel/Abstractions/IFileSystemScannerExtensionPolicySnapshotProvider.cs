namespace DevProjex.Kernel;

/// <summary>
/// Optimized ignore-section scanner that can apply extension selection policy during traversal.
/// Implementations should avoid a separate "discover extensions, then rescan counts" pass.
/// </summary>
public interface IFileSystemScannerExtensionPolicySnapshotProvider
{
	ScanResult<IgnoreSectionScanData> GetIgnoreSectionSnapshot(
		string rootPath,
		IgnoreRules extensionDiscoveryRules,
		IgnoreRules effectiveRules,
		IExtensionInclusionPolicy? effectiveExtensionPolicy,
		CancellationToken cancellationToken = default);

	ScanResult<IgnoreSectionScanData> GetRootFileIgnoreSectionSnapshot(
		string rootPath,
		IgnoreRules extensionDiscoveryRules,
		IgnoreRules effectiveRules,
		IExtensionInclusionPolicy? effectiveExtensionPolicy,
		CancellationToken cancellationToken = default);
}
