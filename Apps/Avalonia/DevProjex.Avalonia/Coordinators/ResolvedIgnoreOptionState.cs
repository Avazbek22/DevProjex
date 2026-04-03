namespace DevProjex.Avalonia.Coordinators;

internal readonly record struct ResolvedIgnoreOptionState(
    IgnoreOptionId Id,
    string Label,
    bool DefaultChecked,
    bool IsChecked);
