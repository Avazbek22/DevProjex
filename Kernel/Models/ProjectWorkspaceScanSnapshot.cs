namespace DevProjex.Kernel.Models;

/// <summary>
/// Single scan product for project-load workflows. Ignore-section data and tree
/// inventory are produced from the same filesystem observation when the scanner
/// can provide it, so initial load can avoid re-enumerating the project for tree IO.
/// </summary>
public sealed record ProjectWorkspaceScanSnapshot(
	IgnoreSectionScanData IgnoreSection,
	ProjectTreeInventorySnapshot? TreeInventory,
	ProjectWorkspaceScanBreakdown? Breakdown = null);
