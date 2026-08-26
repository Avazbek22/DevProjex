using DevProjex.Application.Models;
using DevProjex.Application.Selection;

namespace DevProjex.Avalonia.Coordinators;

internal sealed record SelectionRefreshRollbackSnapshot(
    string Path,
    IReadOnlyList<SelectionOption> ScanRootOptions,
    IReadOnlyList<SelectionOption> ExtensionOptions,
    IReadOnlyList<ResolvedIgnoreOptionState> IgnoreOptions,
    int ExtensionlessEntriesCount,
    bool HasIgnoreOptionCounts,
    IgnoreOptionCounts IgnoreOptionCounts,
    IgnoreControllerImpactCounts ControllerImpactCounts,
    IgnoreSelectionStateSnapshot IgnoreSelectionState,
    IReadOnlySet<string> SelectedExtensions,
    IReadOnlyDictionary<string, bool> ExtensionOptionStateCache,
    bool ExtensionSelectionInitialized,
    bool ExtensionOptionStateCacheIsComplete,
    bool IgnoreOptionStateCacheIsComplete,
    bool SelectionPersistenceBlockedByIncompleteScan,
    bool HasAuthoritativeScanRoots,
    GitWorkspaceEvidence GitEvidence = default);
