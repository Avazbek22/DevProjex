using DevProjex.Application.Models;

namespace DevProjex.Avalonia.Coordinators;

internal sealed record SelectionRefreshRollbackSnapshot(
    IReadOnlyList<SelectionOption> RootOptions,
    IReadOnlyList<SelectionOption> ExtensionOptions,
    IReadOnlyList<ResolvedIgnoreOptionState> IgnoreOptions,
    int ExtensionlessEntriesCount,
    bool HasIgnoreOptionCounts,
    IgnoreOptionCounts IgnoreOptionCounts,
    IgnoreControllerImpactCounts ControllerImpactCounts,
    IReadOnlyDictionary<IgnoreOptionId, bool> IgnoreOptionStateCache,
    bool AllRootFoldersChecked,
    bool AllExtensionsChecked,
    bool AllIgnoreChecked,
    bool IgnoreOptionsInitialized,
    bool? IgnoreAllPreference,
    bool IgnoreOptionStateCacheIsComplete);
