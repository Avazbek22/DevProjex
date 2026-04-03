namespace DevProjex.Kernel;

/// <summary>
/// Provides the complete ignore-section snapshot in one filesystem pass.
/// The caller is expected to keep structural rules aligned between discovery and effective scans.
/// In practice this means directory traversal, .gitignore, and smart-ignore semantics must match,
/// while file-level toggles may differ.
/// </summary>
public interface IFileSystemScannerIgnoreSectionSnapshotProvider
{
	ScanResult<IgnoreSectionScanData> GetIgnoreSectionSnapshot(
		string rootPath,
		IgnoreRules extensionDiscoveryRules,
		IgnoreRules effectiveRules,
		IReadOnlySet<string>? effectiveAllowedExtensions,
		CancellationToken cancellationToken = default);

	ScanResult<IgnoreSectionScanData> GetRootFileIgnoreSectionSnapshot(
		string rootPath,
		IgnoreRules extensionDiscoveryRules,
		IgnoreRules effectiveRules,
		IReadOnlySet<string>? effectiveAllowedExtensions,
		CancellationToken cancellationToken = default);
}
