namespace DevProjex.Avalonia.Coordinators;

internal sealed record ProjectLoadSnapshot(
    SelectionRefreshSnapshot SelectionSnapshot,
    TreeRefreshInput TreeInput,
    BuildTreeResult TreeResult,
    ProjectTreeInventorySnapshot? TreeInventory,
    TreeNodeViewModel TreeRoot,
	PersistentSecretMarksSnapshot? PersistentMarks);
