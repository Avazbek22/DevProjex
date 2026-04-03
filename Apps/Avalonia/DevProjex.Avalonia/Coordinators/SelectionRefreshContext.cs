namespace DevProjex.Avalonia.Coordinators;

internal sealed record SelectionRefreshContext(
    string Path,
    PreparedSelectionMode PreparedSelectionMode,
    bool AllRootFoldersChecked,
    bool AllExtensionsChecked,
    bool RootSelectionInitialized,
    IReadOnlySet<string> RootSelectionCache,
    bool ExtensionsSelectionInitialized,
    IReadOnlySet<string> ExtensionsSelectionCache,
    bool IgnoreSelectionInitialized,
    IReadOnlySet<IgnoreOptionId> IgnoreSelectionCache,
    IReadOnlyDictionary<IgnoreOptionId, bool> IgnoreOptionStateCache,
    bool? IgnoreAllPreference,
    IgnoreSectionSnapshotState CurrentSnapshotState);
