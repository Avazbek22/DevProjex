using DevProjex.Application.Models;

namespace DevProjex.Application.Selection;

public sealed record SelectionRefreshSnapshot(
    IReadOnlyList<SelectionOption>? RootOptions,
    // ExtensionOptions retains hidden option state for convergence. UI and tree consumers
    // must use EffectiveExtensionOptions so ignored-only files cannot create no-op choices.
    IReadOnlyList<SelectionOption> ExtensionOptions,
    IReadOnlyList<ResolvedIgnoreOptionState> IgnoreOptions,
    int ExtensionlessEntriesCount,
    bool HasIgnoreOptionCounts,
    IgnoreOptionCounts IgnoreOptionCounts,
    IgnoreControllerImpactCounts ControllerImpactCounts,
    IReadOnlyDictionary<IgnoreOptionId, bool> IgnoreOptionStateCache,
    bool RootAccessDenied,
    bool HadAccessDenied,
    ProjectTreeInventorySnapshot? TreeInventory = null,
    IReadOnlyList<SelectionOption>? VisibleExtensionOptions = null)
{
    public IReadOnlyList<SelectionOption> EffectiveExtensionOptions =>
        VisibleExtensionOptions ?? ExtensionOptions;
}
