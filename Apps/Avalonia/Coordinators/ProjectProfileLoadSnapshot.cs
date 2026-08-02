namespace DevProjex.Avalonia.Coordinators;

public readonly record struct ProjectProfileLoadSnapshot(
    bool HasProfile,
    ProjectSelectionProfile? Profile);
