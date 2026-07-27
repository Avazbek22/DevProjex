namespace DevProjex.Application.Selection;

public readonly record struct IgnoreSectionSnapshotState(
    bool HasIgnoreOptionCounts,
    IgnoreOptionCounts IgnoreOptionCounts,
    IgnoreControllerImpactCounts ControllerImpactCounts,
    bool HasExtensionlessEntries,
    int ExtensionlessEntriesCount,
    GitWorkspaceEvidence GitEvidence = default)
{
    // Availability is driven by counts, the extensionless marker, and structural Git
    // evidence collected by the same scan. Comparing all three keeps convergence exact.
    public bool HasAvailabilityDifference(in IgnoreSectionSnapshotState other)
    {
        return HasIgnoreOptionCounts != other.HasIgnoreOptionCounts ||
               IgnoreOptionCounts != other.IgnoreOptionCounts ||
               ControllerImpactCounts != other.ControllerImpactCounts ||
               HasExtensionlessEntries != other.HasExtensionlessEntries ||
               ExtensionlessEntriesCount != other.ExtensionlessEntriesCount ||
               GitEvidence != other.GitEvidence;
    }
}
