namespace DevProjex.Kernel.Models;

public sealed record IgnoreSectionScanData(
	HashSet<string> Extensions,
	IgnoreOptionCounts RawIgnoreOptionCounts,
	IgnoreOptionCounts EffectiveIgnoreOptionCounts);
