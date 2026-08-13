using DevProjex.Application.Secrets;

namespace DevProjex.Infrastructure.ProjectProfiles;

internal static class ProjectProfileStorageLimits
{
	// Normal profile stores are measured in kilobytes. Eight MiB leaves ample room for large
	// selections while bounding JsonDocument and string allocation on untrusted local data.
	public const long MaximumJsonBytes = 8 * 1024 * 1024;
	public const int MaximumSelectionProfiles = 500;
	// Marks are deliberately outside the 500-profile LRU. A hard 4K-project ceiling bounds a
	// malformed store without silently evicting security decisions for legitimate projects.
	public const int MaximumPersistentMarkProjects = 4_096;
	// Large generated repositories can expose tens of thousands of options and paths; 100K keeps
	// those valid profiles usable while bounding each parsed collection independently.
	public const int MaximumSelectionItemsPerCollection = 100_000;
	// Paths and option names above four KiB are not actionable filesystem identities and would
	// otherwise amplify dictionary and normalization allocations from malformed JSON.
	public const int MaximumStateNameLength = 4_096;
	public const int MaximumMarkedSecretKeyLength = SecretInspectionLimits.MaximumPersistentMarkKeyLength;
	public const int MaximumMarkedSecretPathLength = SecretInspectionLimits.MaximumPersistentMarkPathLength;
	public const int MaximumPersistentMarksPerProject =
		SecretInspectionLimits.MaximumPersistentMarksPerProject;
	// A removed state is retained as a tombstone so delayed typed deltas cannot resurrect it.
	// Keeping one tombstone per active-mark slot bounds disk growth without weakening that rule.
	public const int MaximumPersistentMarkStatesPerProject =
		MaximumPersistentMarksPerProject * 2;
}
