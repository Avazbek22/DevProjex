using DevProjex.Application.Models;

namespace DevProjex.Application.Selection;

public sealed record SelectionRefreshSnapshot(
    IReadOnlyList<SelectionOption>? RootOptions,
    IReadOnlyList<SelectionOption> ExtensionOptions,
    IReadOnlyList<ResolvedIgnoreOptionState> IgnoreOptions,
    int ExtensionlessEntriesCount,
    bool HasIgnoreOptionCounts,
    IgnoreOptionCounts IgnoreOptionCounts,
    IgnoreControllerImpactCounts ControllerImpactCounts,
    IReadOnlyDictionary<IgnoreOptionId, bool> IgnoreOptionStateCache,
    bool RootAccessDenied,
    bool HadAccessDenied);
