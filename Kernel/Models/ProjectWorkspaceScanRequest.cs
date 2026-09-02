namespace DevProjex.Kernel.Models;

/// <summary>
/// Canonical request for project workspace scans. CaptureTreeInventory controls reusable
/// tree metadata without changing ignore-section semantics.
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
