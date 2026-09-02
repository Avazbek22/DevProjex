using DevProjex.Application.Models;

namespace DevProjex.Application.Selection;

public sealed record SelectionRefreshSnapshot(
    // TODO(cli): Retained only for the current --root/profile output contract.
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
    bool HadScanFailure = false,
    ProjectTreeInventorySnapshot? TreeInventory = null,
    IReadOnlyList<SelectionOption>? VisibleExtensionOptions = null,
    GitWorkspaceEvidence GitEvidence = default,
    IReadOnlySet<IgnoreOptionId>? SelectedIgnoreOptions = null,
    IgnoreRules? EffectiveRules = null)
{
	public GitScopePresentationProjection? GitScopePresentation { get; init; }
	public GitScopePathResult? GitScope { get; init; }

    public IReadOnlyList<SelectionOption> EffectiveExtensionOptions =>
        VisibleExtensionOptions ?? ExtensionOptions;

    public IReadOnlySet<IgnoreOptionId> EffectiveIgnoreOptions =>
        SelectedIgnoreOptions ?? IgnoreOptions
            .Where(static option => option.IsChecked)
            .Select(static option => option.Id)
            .ToHashSet();
}
