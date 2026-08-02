namespace DevProjex.Kernel.Models;

public sealed record ExtensionsScanData(
	HashSet<string> Extensions,
	IgnoreOptionCounts IgnoreOptionCounts,
	IgnoreControllerImpactCounts ControllerImpactCounts = default);
