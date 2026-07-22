namespace DevProjex.Kernel.Models;

public sealed record IgnoreSectionScanData(
	// Extensions is the relaxed state set used to keep selection and toggle counters stable.
	// EffectiveExtensions is the user-facing set after every active ignore rule is applied.
	HashSet<string> Extensions,
	IgnoreOptionCounts RawIgnoreOptionCounts,
	IgnoreOptionCounts EffectiveIgnoreOptionCounts,
	IgnoreControllerImpactCounts ControllerImpactCounts = default,
	HashSet<string>? EffectiveExtensions = null,
	bool? HasVisibleTreeStructure = null,
	bool IsTreeStructureHiddenByEmptyFolders = false)
{
	public IReadOnlySet<string> VisibleExtensions => EffectiveExtensions ?? Extensions;
}
