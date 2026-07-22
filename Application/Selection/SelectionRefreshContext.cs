using DevProjex.Application.Models;

namespace DevProjex.Application.Selection;

public sealed record SelectionRefreshContext(
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
    IgnoreSectionSnapshotState CurrentSnapshotState,
    IReadOnlyDictionary<string, bool>? RootOptionStateCache = null,
    IReadOnlyDictionary<string, bool>? ExtensionOptionStateCache = null,
    bool IgnoreOptionStateCacheIsComplete = false,
    bool CaptureTreeInventory = false,
    IReadOnlyList<SelectionOption>? CurrentRootOptions = null);
