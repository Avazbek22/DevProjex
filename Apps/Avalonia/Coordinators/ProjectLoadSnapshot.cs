namespace DevProjex.Avalonia.Coordinators;

internal sealed record ProjectLoadSnapshot(
    SelectionRefreshSnapshot SelectionSnapshot,
    TreeRefreshInput TreeInput,
    BuildTreeResult TreeResult,
    ProjectTreeInventorySnapshot? TreeInventory,
	GitScopePresentationProjection? GitScopePresentation,
    TreeNodeViewModel TreeRoot,
	PersistentSecretMarksSnapshot? PersistentMarks);
