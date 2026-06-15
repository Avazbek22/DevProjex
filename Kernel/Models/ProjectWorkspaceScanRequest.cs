namespace DevProjex.Kernel.Models;

/// <summary>
/// Canonical request for project workspace scans. CaptureTreeInventory controls only
/// whether the scan keeps reusable tree inventory; ignore-section semantics must remain
/// identical between lightweight and inventory-capturing requests.
/// </summary>
public sealed record ProjectWorkspaceScanRequest(
	string RootPath,
	IReadOnlyCollection<string> SelectedRootFolders,
	IgnoreRules ExtensionDiscoveryRules,
	IgnoreRules EffectiveRules,
	IExtensionInclusionPolicy? EffectiveExtensionPolicy,
	bool CaptureTreeInventory,
	bool IncludeDirectoryToggleProbeRoots,
	bool IncludeControllerImpactProbeRoots);
