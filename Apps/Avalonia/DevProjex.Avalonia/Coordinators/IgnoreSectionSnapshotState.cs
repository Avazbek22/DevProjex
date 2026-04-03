namespace DevProjex.Avalonia.Coordinators;

internal readonly record struct IgnoreSectionSnapshotState(
    bool HasIgnoreOptionCounts,
    IgnoreOptionCounts IgnoreOptionCounts,
    bool HasExtensionlessEntries,
    int ExtensionlessEntriesCount)
{
    // Ignore-option visibility is driven by both aggregated counts and the special
    // extensionless marker path, so orchestration decisions must compare both.
    public bool HasAvailabilityDifference(in IgnoreSectionSnapshotState other)
    {
        return HasIgnoreOptionCounts != other.HasIgnoreOptionCounts ||
               IgnoreOptionCounts != other.IgnoreOptionCounts ||
               HasExtensionlessEntries != other.HasExtensionlessEntries ||
               ExtensionlessEntriesCount != other.ExtensionlessEntriesCount;
    }
}
