namespace DevProjex.Application.Selection;

public static class IgnoreRulesProjection
{
	public static IgnoreRules ForExtensionAvailability(IgnoreRules effectiveRules)
	{
		// Directory and controller rules still define reachability. Relax only file-level
		// rules so the snapshot can measure each toggle without exposing ignored-only
		// extensions in the final UI projection.
		if (!effectiveRules.IgnoreHiddenFiles &&
		    !effectiveRules.IgnoreDotFiles &&
		    !effectiveRules.IgnoreEmptyFiles &&
		    !effectiveRules.IgnoreExtensionlessFiles)
		{
			return effectiveRules;
		}

		return effectiveRules with
		{
			IgnoreHiddenFiles = false,
			IgnoreDotFiles = false,
			IgnoreEmptyFiles = false,
			IgnoreExtensionlessFiles = false
		};
	}
}
