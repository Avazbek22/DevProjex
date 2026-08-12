namespace DevProjex.Avalonia.Coordinators;

internal sealed record TreeRefreshInput(
    string CurrentPath,
    string DisplayName,
    TreeFilterOptions Options,
    string? NameFilter,
    ProjectTreeInventorySnapshot? TreeInventory = null,
    ProjectTreeInventoryReuseScope? TreeInventoryScope = null,
    long? SelectionRevision = null,
    BuildTreeResult? InteractiveFilterBaseTree = null,
    IReadOnlySet<string>? CheckedPaths = null);
