using DevProjex.Application.Models;

namespace DevProjex.Application.Selection;

public sealed record SelectionRefreshContext(
    string Path,
    PreparedSelectionMode PreparedSelectionMode,
    // TODO(cli): Remove the legacy root-selection compatibility fields when --root is revised.
    // Desktop always supplies the full project scope and does not persist this state.
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
    IReadOnlyList<SelectionOption>? CurrentRootOptions = null,
    // Explicit CLI collections are closed sets. Persisted settings maps are open-world:
    // known rows retain their state and newly discovered rows receive product defaults.
    bool RootSelectionIsExplicit = false,
    bool ExtensionSelectionIsExplicit = false,
	GitFilteringMode GitMode = GitFilteringMode.None,
	string? GitDiffRange = null,
	IReadOnlySet<string>? GitRepositoryScopePaths = null)
{
    private static readonly IReadOnlySet<string> EmptyRootSelection =
        new HashSet<string>(PathComparer.Default);

    public static SelectionRefreshContext ForDesktop(
        string path,
        PreparedSelectionMode preparedSelectionMode,
        bool allExtensionsChecked,
        bool extensionsSelectionInitialized,
        IReadOnlySet<string> extensionsSelectionCache,
        bool ignoreSelectionInitialized,
        IReadOnlySet<IgnoreOptionId> ignoreSelectionCache,
        IReadOnlyDictionary<IgnoreOptionId, bool> ignoreOptionStateCache,
        bool? ignoreAllPreference,
        IgnoreSectionSnapshotState currentSnapshotState,
        IReadOnlyDictionary<string, bool>? extensionOptionStateCache,
        bool ignoreOptionStateCacheIsComplete,
        bool captureTreeInventory,
        IReadOnlyList<SelectionOption>? currentScanRootOptions,
        bool extensionSelectionIsExplicit,
		GitFilteringMode gitMode = GitFilteringMode.None,
		string? gitDiffRange = null,
		IReadOnlySet<string>? gitRepositoryScopePaths = null) =>
        new(
            Path: path,
            PreparedSelectionMode: preparedSelectionMode,
            AllRootFoldersChecked: true,
            AllExtensionsChecked: allExtensionsChecked,
            RootSelectionInitialized: false,
            RootSelectionCache: EmptyRootSelection,
            ExtensionsSelectionInitialized: extensionsSelectionInitialized,
            ExtensionsSelectionCache: extensionsSelectionCache,
            IgnoreSelectionInitialized: ignoreSelectionInitialized,
            IgnoreSelectionCache: ignoreSelectionCache,
            IgnoreOptionStateCache: ignoreOptionStateCache,
            IgnoreAllPreference: ignoreAllPreference,
            CurrentSnapshotState: currentSnapshotState,
            RootOptionStateCache: null,
            ExtensionOptionStateCache: extensionOptionStateCache,
            IgnoreOptionStateCacheIsComplete: ignoreOptionStateCacheIsComplete,
            CaptureTreeInventory: captureTreeInventory,
            CurrentRootOptions: currentScanRootOptions,
            RootSelectionIsExplicit: false,
            ExtensionSelectionIsExplicit: extensionSelectionIsExplicit,
			GitMode: gitMode,
			GitDiffRange: gitDiffRange,
			GitRepositoryScopePaths: gitRepositoryScopePaths);
}
