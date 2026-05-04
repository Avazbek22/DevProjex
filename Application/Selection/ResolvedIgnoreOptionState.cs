namespace DevProjex.Application.Selection;

public readonly record struct ResolvedIgnoreOptionState(
    IgnoreOptionId Id,
    string Label,
    bool DefaultChecked,
    bool IsChecked);
