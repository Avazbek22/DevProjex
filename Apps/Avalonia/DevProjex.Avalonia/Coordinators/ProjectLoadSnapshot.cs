namespace DevProjex.Avalonia.Coordinators;

internal sealed record ProjectLoadSnapshot(
    SelectionRefreshSnapshot SelectionSnapshot,
    TreeRefreshInput TreeInput,
    BuildTreeResult TreeResult,
    TreeNodeViewModel TreeRoot);
