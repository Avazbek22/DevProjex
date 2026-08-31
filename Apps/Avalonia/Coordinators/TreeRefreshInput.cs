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
	GitFilteringMode GitMode = GitFilteringMode.None,
    GitScopePathResult? GitScope = null,
	GitScopePresentationProjection? GitScopePresentation = null,
	IExtensionInclusionPolicy? EffectiveExtensionPolicy = null,
	IReadOnlySet<string>? AvailableRootFolders = null,
	IReadOnlySet<string>? GitRepositoryScopePaths = null,
    bool PreserveCheckedPaths = false,
    bool PreserveExpandedPaths = false);
