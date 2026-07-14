using DevProjex.Application.Models;

namespace DevProjex.Avalonia.Coordinators;

internal sealed record SelectionRefreshRollbackSnapshot(
    string Path,
    IReadOnlyList<SelectionOption> RootOptions,
    IReadOnlyList<SelectionOption> ExtensionOptions,
    IReadOnlyList<ResolvedIgnoreOptionState> IgnoreOptions,
    int ExtensionlessEntriesCount,
    bool HasIgnoreOptionCounts,
    IgnoreOptionCounts IgnoreOptionCounts,
    IgnoreControllerImpactCounts ControllerImpactCounts,
    IReadOnlyDictionary<IgnoreOptionId, bool> IgnoreOptionStateCache,
    IReadOnlySet<string> SelectedRootFolders,
    IReadOnlyDictionary<string, bool> RootOptionStateCache,
    bool RootSelectionInitialized,
    bool RootOptionStateCacheIsComplete,
    IReadOnlySet<string> SelectedExtensions,
    IReadOnlyDictionary<string, bool> ExtensionOptionStateCache,
    bool ExtensionSelectionInitialized,
    bool ExtensionOptionStateCacheIsComplete,
    bool AllRootFoldersChecked,
    bool AllExtensionsChecked,
    bool AllIgnoreChecked,
    bool IgnoreOptionsInitialized,
    bool? IgnoreAllPreference,
    bool IgnoreOptionStateCacheIsComplete);
